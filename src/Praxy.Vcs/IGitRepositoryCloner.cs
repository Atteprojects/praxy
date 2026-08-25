namespace Praxy.Vcs;

/// <summary>Clones a repository at an exact commit into a temp directory. An interface so build-worker logic can be tested against a fake instead of a real clone.</summary>
public interface IGitRepositoryCloner
{
    Task<GitCheckout> CloneAsync(string repositoryFullName, string commitSha, string installationToken, CancellationToken ct);
}

/// <summary>
/// A checked-out working directory, deleted on dispose whether the build that follows succeeds or
/// fails — the same discipline <c>SiteDeploymentSource</c>'s uploaded tar bytes already follow.
/// </summary>
public sealed class GitCheckout(string path) : IAsyncDisposable
{
    public string Path { get; } = path;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort — a build that already finished must not fail because cleanup couldn't
            // remove a temp directory (e.g. a lingering file handle).
        }
        return ValueTask.CompletedTask;
    }
}
