namespace Praxy.Vcs;

public sealed record GitHubInstallation(long Id, string AccountLogin, string AccountType);

public sealed record GitHubAppInfo(string Slug);

/// <summary>
/// GitHub's REST API, exactly as far as <c>Praxy.Vcs</c> needs it — App-JWT-authenticated calls
/// (installation lookup/token minting) and installation-token-authenticated calls (branch listing).
/// An interface purely so tests can substitute a fake instead of hitting real GitHub (see the kickoff
/// prompt's Tests section) — <see cref="GitHubClient"/> is the only production implementation.
/// </summary>
public interface IGitHubClient
{
    /// <summary>App-JWT-authed. Used to compute the console's "Connect GitHub" install URL.</summary>
    Task<GitHubAppInfo> GetAppAsync(CancellationToken ct);

    /// <summary>App-JWT-authed. Null on a 404 (no such installation for this App).</summary>
    Task<GitHubInstallation?> GetInstallationAsync(long installationId, CancellationToken ct);

    /// <summary>App-JWT-authed. Null on a 404 (the App isn't installed on this repository, or it doesn't exist).</summary>
    Task<GitHubInstallation?> GetRepositoryInstallationAsync(string owner, string repo, CancellationToken ct);

    /// <summary>App-JWT-authed. Mints a ~1 hour installation access token scoped to one installation.</summary>
    Task<string> CreateInstallationTokenAsync(long installationId, CancellationToken ct);

    /// <summary>Installation-token-authed.</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(string installationToken, string owner, string repo, CancellationToken ct);
}
