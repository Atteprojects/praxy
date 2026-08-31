using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Praxy.Vcs;

/// <summary>
/// <see cref="IGitHubClient"/> against the real <c>https://api.github.com</c> — registered as a typed
/// client (<c>AddHttpClient&lt;IGitHubClient, GitHubClient&gt;</c> in Program.cs sets the base
/// address). Every call needs a User-Agent (GitHub rejects requests without one) and the
/// <c>X-GitHub-Api-Version</c> header GitHub's docs recommend pinning.
/// </summary>
public sealed class GitHubClient(HttpClient http, VcsOptions options) : IGitHubClient
{
    private const string ApiVersion = "2022-11-28";

    public async Task<GitHubAppInfo> GetAppAsync(CancellationToken ct)
    {
        var doc = await SendAppAsync(HttpMethod.Get, "app", ct)
            ?? throw new InvalidOperationException("GET /app unexpectedly returned 404 for our own App.");
        return new GitHubAppInfo(doc.GetProperty("slug").GetString()!);
    }

    public async Task<GitHubInstallation?> GetInstallationAsync(long installationId, CancellationToken ct)
    {
        var doc = await SendAppAsync(HttpMethod.Get, $"app/installations/{installationId}", ct);
        return doc is null ? null : ParseInstallation(doc.Value);
    }

    public async Task<GitHubInstallation?> GetRepositoryInstallationAsync(string owner, string repo, CancellationToken ct)
    {
        var doc = await SendAppAsync(HttpMethod.Get, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/installation", ct);
        return doc is null ? null : ParseInstallation(doc.Value);
    }

    public async Task<string> CreateInstallationTokenAsync(long installationId, CancellationToken ct)
    {
        var doc = await SendAppAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", ct)
            ?? throw new InvalidOperationException($"Installation {installationId} disappeared while minting a token.");
        return doc.GetProperty("token").GetString()!;
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(string installationToken, string owner, string repo, CancellationToken ct)
    {
        var branches = new List<string>();
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/branches?per_page=100&page={page}");
            Prepare(request, installationToken);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            var count = 0;
            foreach (var branch in body.EnumerateArray())
            {
                branches.Add(branch.GetProperty("name").GetString()!);
                count++;
            }
            if (count < 100)
                break;
        }
        return branches;
    }

    public Task DeleteInstallationAsync(long installationId, CancellationToken ct) =>
        SendAppNoContentAsync(HttpMethod.Delete, $"app/installations/{installationId}", ct);

    private async Task<JsonElement?> SendAppAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        Prepare(request, GitHubAppJwt.Create(options.GitHub));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    /// <summary>Same 404-tolerant shape as <see cref="SendAppAsync"/>, for calls (like DELETE) whose success response has no body to parse.</summary>
    private async Task SendAppNoContentAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        Prepare(request, GitHubAppJwt.Create(options.GitHub));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        response.EnsureSuccessStatusCode();
    }

    private static void Prepare(HttpRequestMessage request, string bearerToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Praxy", Core.PraxyVersion.Current));
    }

    private static GitHubInstallation ParseInstallation(JsonElement doc) => new(
        doc.GetProperty("id").GetInt64(),
        doc.GetProperty("account").GetProperty("login").GetString()!,
        doc.GetProperty("account").GetProperty("type").GetString()!);
}
