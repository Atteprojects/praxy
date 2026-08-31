using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Vcs;

/// <summary>
/// Orchestration over <see cref="IGitHubClient"/> and the <c>vcs_installations</c> table — installation
/// bookkeeping, the console's "Connect GitHub" install URL, and repository access/branch lookups. Which
/// specific installation covers a given repository is always resolved live against GitHub
/// (<see cref="GetRepositoryInstallationAsync"/> via <see cref="IGitHubClient"/>), never by joining
/// through <c>vcs_installations</c> — that table exists purely so callers can ask "has GitHub been
/// connected to this instance at all," a cheap existence check independent of any one repository.
/// Entirely resource-agnostic: nothing here knows a <c>Site</c> exists.
/// </summary>
public sealed class GitHubAppService(PraxyDb db, IGitHubClient github)
{
    public async Task<Uri> GetInstallUrlAsync(CancellationToken ct)
    {
        var app = await github.GetAppAsync(ct);
        return new Uri($"https://github.com/apps/{app.Slug}/installations/new");
    }

    /// <summary>Called from the <c>/v1/vcs/github/callback</c> redirect target. Upserts on <paramref name="installationId"/> so re-running the install flow (GitHub's "Configure" action) doesn't create a duplicate row.</summary>
    public async Task<VcsInstallation> HandleInstallCallbackAsync(long installationId, CancellationToken ct)
    {
        var installation = await github.GetInstallationAsync(installationId, ct)
            ?? throw new PraxyException(404, ErrorTypes.VcsGithubInstallationRequired,
                "GitHub reports no installation with that id for this App.");

        var existing = await db.VcsInstallations.FirstOrDefaultAsync(i => i.InstallationId == installationId, ct);
        if (existing is not null)
        {
            existing.AccountLogin = installation.AccountLogin;
            existing.AccountType = installation.AccountType;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var created = new VcsInstallation
        {
            Id = Ids.NewUuid(),
            InstallationId = installation.Id,
            AccountLogin = installation.AccountLogin,
            AccountType = installation.AccountType,
        };
        db.VcsInstallations.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    public Task<List<VcsInstallation>> ListInstallationsAsync(CancellationToken ct) =>
        db.VcsInstallations.OrderByDescending(i => i.CreatedAt).ToListAsync(ct);

    /// <summary>
    /// The console's "Disconnect" action — uninstalls the App from GitHub's side (not just clearing
    /// our own record; leaving the App installed while Praxy forgets about it would leave GitHub
    /// still delivering webhooks and honoring API calls the console no longer shows as connected)
    /// and then removes the local row. Any site or function still holding this repository's
    /// <c>repositoryFullName</c> is deliberately left alone — Praxy never tracked which installation
    /// covered which repository (see this class's own remarks), so there is nothing to cascade; its
    /// next build simply fails with <see cref="ErrorTypes.VcsGithubRepositoryInaccessible"/> the same
    /// way a revoked-on-GitHub's-side installation already does.
    /// </summary>
    public async Task RemoveInstallationAsync(Guid id, CancellationToken ct)
    {
        var installation = await db.VcsInstallations.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.VcsGithubInstallationNotFound, "Installation not found.");

        await github.DeleteInstallationAsync(installation.InstallationId, ct);

        db.VcsInstallations.Remove(installation);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Throws <see cref="ErrorTypes.VcsGithubInstallationRequired"/> if no installation is connected at all, or <see cref="ErrorTypes.VcsGithubRepositoryInaccessible"/> if one is but doesn't cover this repository.</summary>
    public async Task EnsureRepositoryAccessibleAsync(string repositoryFullName, CancellationToken ct)
    {
        await RequireAnyInstallationAsync(ct);
        var (owner, repo) = SplitRepository(repositoryFullName);
        if (await github.GetRepositoryInstallationAsync(owner, repo, ct) is null)
            throw new PraxyException(422, ErrorTypes.VcsGithubRepositoryInaccessible,
                $"The GitHub App isn't installed on '{repositoryFullName}', or it doesn't exist.");
    }

    public async Task<IReadOnlyList<string>> ListBranchesForRepositoryAsync(string repositoryFullName, CancellationToken ct)
    {
        var (owner, repo) = SplitRepository(repositoryFullName);
        var token = await GetInstallationTokenForRepositoryAsync(repositoryFullName, ct);
        return await github.ListBranchesAsync(token, owner, repo, ct);
    }

    /// <summary>Used by <c>SiteBuildWorker</c> at clone time — re-resolves the installation live rather than trusting anything cached, so a revoked/reconfigured installation is caught immediately rather than on the next connect.</summary>
    public async Task<string> GetInstallationTokenForRepositoryAsync(string repositoryFullName, CancellationToken ct)
    {
        var (owner, repo) = SplitRepository(repositoryFullName);
        var installation = await github.GetRepositoryInstallationAsync(owner, repo, ct)
            ?? throw new PraxyException(422, ErrorTypes.VcsGithubRepositoryInaccessible,
                $"The GitHub App isn't installed on '{repositoryFullName}', or it doesn't exist.");
        return await github.CreateInstallationTokenAsync(installation.Id, ct);
    }

    private async Task RequireAnyInstallationAsync(CancellationToken ct)
    {
        if (!await db.VcsInstallations.AnyAsync(ct))
            throw new PraxyException(422, ErrorTypes.VcsGithubInstallationRequired,
                "Install the Praxy GitHub App for this instance before connecting a repository.");
    }

    private static (string Owner, string Repo) SplitRepository(string repositoryFullName)
    {
        var parts = repositoryFullName.Split('/', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw PraxyException.ArgumentInvalid("Invalid repository.",
                new Dictionary<string, string[]> { ["repositoryFullName"] = ["Must be 'owner/repo'."] });
        return (parts[0], parts[1]);
    }
}
