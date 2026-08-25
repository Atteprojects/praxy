using System.Diagnostics;

namespace Praxy.Vcs;

/// <summary>
/// Shells out to the system <c>git</c> CLI rather than a library like LibGit2Sharp (see
/// docs/research/dotnet-stack.md's Phase 4 section) — <c>git init</c> + <c>remote add</c> +
/// <c>fetch --depth 1 origin &lt;sha&gt;</c> + <c>checkout FETCH_HEAD</c> pins the exact pushed commit,
/// not just a branch's tip at fetch time (which could have moved again by the time the build worker
/// gets to it). Requires <c>git</c> to be on PATH — the deploy image installs it explicitly (see
/// deploy/Dockerfile).
/// </summary>
public sealed class GitCliRepositoryCloner(VcsOptions options) : IGitRepositoryCloner
{
    public async Task<GitCheckout> CloneAsync(string repositoryFullName, string commitSha, string installationToken, CancellationToken ct)
    {
        var checkout = new GitCheckout(Directory.CreateTempSubdirectory("praxy-vcs-").FullName);
        try
        {
            // The token rides in the remote URL, never in a log line or an argv string another user
            // on the box could read via `ps` — ArgumentList passes each argument directly to the
            // child process, never through a shell that would need it escaped or would echo it.
            var remoteUrl = $"https://x-access-token:{installationToken}@github.com/{repositoryFullName}.git";
            await RunGitAsync(checkout.Path, "init", ["-q"], ct);
            await RunGitAsync(checkout.Path, "remote", ["add", "origin", remoteUrl], ct);
            await RunGitAsync(checkout.Path, "fetch", ["--depth", "1", "origin", commitSha], ct);
            await RunGitAsync(checkout.Path, "checkout", ["-q", "FETCH_HEAD"], ct);
            return checkout;
        }
        catch
        {
            await checkout.DisposeAsync();
            throw;
        }
    }

    /// <summary>Only <paramref name="subcommand"/> ever appears in a thrown error's message — the "remote add" call's args contain the installation token, so the full argument list must never be echoed anywhere, including exception text a log sink might capture.</summary>
    private async Task RunGitAsync(string workingDirectory, string subcommand, string[] args, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", subcommand)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.CloneTimeoutSeconds));

        process.Start();
        // Drained concurrently with the wait, not after — git can write more progress output to
        // stderr than the OS pipe buffer holds, and reading it only after WaitForExitAsync returns
        // is the classic way to deadlock a redirected child process that never gets to exit.
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException($"git {subcommand} timed out after {options.CloneTimeoutSeconds}s.");
        }

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException($"git {subcommand} failed (exit {process.ExitCode}): {Truncate(stderr)}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout firing and the kill call.
        }
    }

    private static string Truncate(string text)
    {
        const int max = 2000;
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : string.Concat(trimmed.AsSpan(0, max), "…");
    }
}
