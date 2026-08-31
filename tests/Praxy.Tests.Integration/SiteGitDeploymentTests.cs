using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Praxy.Tests.Integration.Infrastructure;
using Praxy.Vcs;

namespace Praxy.Tests.Integration;

/// <summary>
/// Sites Phase 4's own owner-test flow, end to end through the real webhook endpoint and
/// <c>SiteBuildWorker</c> — <see cref="IGitHubClient"/> and <see cref="IGitRepositoryCloner"/> are
/// swapped for fakes (per the kickoff prompt's own testing guidance: the JWT/token-exchange/clone
/// pieces are the hardest to test without hitting real GitHub), but everything downstream — signature
/// verification, the webhook dispatch query, deployment creation, the real Docker build, and the
/// auto-activate-only-on-production-branch guard — runs for real. No real GitHub App, installation, or
/// network call happens anywhere in this suite.
/// </summary>
public class SiteGitDeploymentTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const string WebhookSecret = "integration-test-webhook-secret";
    private readonly FakeGitHubClient _github = new();

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Sites:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Sites:BuildTimeoutSeconds"] = "60",
        ["Praxy:Sites:ReconcileIntervalSeconds"] = "3600",
        ["Praxy:Vcs:GitHub:AppId"] = "test-app",
        ["Praxy:Vcs:GitHub:ClientId"] = "test-client",
        ["Praxy:Vcs:GitHub:ClientSecret"] = "test-client-secret",
        // Never actually used to sign anything — IGitHubClient is faked out entirely, so
        // GitHubAppJwt.Create (the only thing that would ever read this) is never called.
        ["Praxy:Vcs:GitHub:PrivateKey"] = "unused-in-this-suite",
        ["Praxy:Vcs:GitHub:WebhookSecret"] = WebhookSecret,
    };

    protected override Action<IServiceCollection>? TestServices => services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IGitHubClient>(_github));
        services.Replace(ServiceDescriptor.Singleton<IGitRepositoryCloner>(new FakeGitRepositoryCloner()));
    };

    /// <summary>Same reasoning as SiteTests'/SiteCustomDomainTests' own overrides — a site's container is deliberately left running when api shuts down.</summary>
    public override async Task DisposeAsync()
    {
        var containerIds = new HashSet<string>();
        await using (var conn = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT container_id FROM praxy.site_deployments WHERE container_id IS NOT NULL", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                containerIds.Add(reader.GetString(0));
        }

        // The DB column above only ever holds an *activated* deployment's container — preview
        // containers (e.g. a non-production-branch push, which this suite deliberately builds but
        // never activates) never get written there, so they'd otherwise leak past this cleanup.
        var registry = Factory.Services.GetRequiredService<Praxy.Sites.SiteContainerRegistry>();
        foreach (var deploymentId in registry.TrackedDeploymentIds())
            if (registry.TryGet(deploymentId, out var container))
                containerIds.Add(container.ContainerId);

        if (containerIds.Count > 0)
        {
            var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();
            foreach (var containerId in containerIds)
                await docker.StopAndRemoveAsync(containerId, CancellationToken.None);
        }
        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_push_to_the_production_branch_creates_and_auto_activates_a_deployment()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        await ConnectGitHubAsync("acme/website", ["main", "preview"]);
        await ConnectRepositoryAsync(operatorToken, projectId, siteId, "acme/website", "main");

        var response = await PostWebhookAsync(BuildPushPayload("acme/website", "main", "commit-1", "Ship it"));
        Assert.Equal(204, (int)response.StatusCode);

        var deployment = await FindDeploymentByCommitAsync(operatorToken, projectId, siteId, "commit-1");
        Assert.Equal("git", deployment.GetProperty("source").GetString());
        Assert.Equal("main", deployment.GetProperty("branch").GetString());
        Assert.Equal("Ship it", deployment.GetProperty("commitMessage").GetString());
        var deploymentId = deployment.GetProperty("id").GetString()!;

        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, deploymentId);
    }

    [Fact]
    public async Task A_push_to_a_non_production_branch_builds_a_reachable_preview_without_touching_production()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        await ConnectGitHubAsync("acme/website", ["main", "preview"]);
        await ConnectRepositoryAsync(operatorToken, projectId, siteId, "acme/website", "main");

        // A production push first, so there's a real active deployment to prove stays untouched.
        await PostWebhookAsync(BuildPushPayload("acme/website", "main", "commit-prod", "Prod"));
        var prod = await FindDeploymentByCommitAsync(operatorToken, projectId, siteId, "commit-prod");
        var activeBefore = prod.GetProperty("id").GetString()!;
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, activeBefore);

        var response = await PostWebhookAsync(BuildPushPayload("acme/website", "preview", "commit-preview", "Try this"));
        Assert.Equal(204, (int)response.StatusCode);

        var preview = await FindDeploymentByCommitAsync(operatorToken, projectId, siteId, "commit-preview");
        var previewId = preview.GetProperty("id").GetString()!;
        var finishedPreview = await WaitForDeploymentFinishedAsync(operatorToken, projectId, siteId, previewId);
        Assert.Equal("ready", finishedPreview.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, finishedPreview.GetProperty("previewUrl").ValueKind);

        var siteAfterPreview = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken)));
        Assert.Equal(activeBefore, siteAfterPreview.GetProperty("activeDeploymentId").GetString());
        Assert.NotEqual(previewId, siteAfterPreview.GetProperty("activeDeploymentId").GetString());

        var previewUrl = finishedPreview.GetProperty("previewUrl").GetString()!;
        var host = new Uri(previewUrl).Host;
        var previewRequest = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = host } };
        var previewResponse = await Client.SendAsync(previewRequest);
        Assert.Equal(200, (int)previewResponse.StatusCode);
        Assert.Equal("ok", await previewResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unsigned_or_badly_signed_webhook_is_rejected_before_any_deployment_is_created()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        await ConnectGitHubAsync("acme/website", ["main"]);
        await ConnectRepositoryAsync(operatorToken, projectId, siteId, "acme/website", "main");

        var payload = BuildPushPayload("acme/website", "main", "unsigned-commit", "Nope");

        var unsigned = await PostWebhookAsync(payload, signatureHeader: null);
        Assert.Equal(401, (int)unsigned.StatusCode);

        var badSecret = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("wrong-secret"), payload));
        var badlySigned = await PostWebhookAsync(payload, badSecret);
        Assert.Equal(401, (int)badlySigned.StatusCode);

        var deployments = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/sites/{siteId}/deployments", operatorToken)));
        Assert.Equal(0, deployments.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_push_for_a_repository_no_site_references_is_a_no_op()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        await ConnectGitHubAsync("acme/website", ["main"]);
        await ConnectRepositoryAsync(operatorToken, projectId, siteId, "acme/website", "main");

        var response = await PostWebhookAsync(BuildPushPayload("someone-else/unrelated", "main", "commit-x", "Not us"));
        Assert.Equal(204, (int)response.StatusCode);

        var deployments = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/sites/{siteId}/deployments", operatorToken)));
        Assert.Equal(0, deployments.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Disconnecting_an_installation_removes_it_and_uninstalls_it_on_githubs_side()
    {
        var (operatorToken, _) = await SetupProjectAsync();
        await ConnectGitHubAsync("acme/website", ["main"]);

        var listed = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            "/v1/console/vcs/github/installations", operatorToken)));
        Assert.Equal(1, listed.GetProperty("total").GetInt32());
        var installationId = listed.GetProperty("installations")[0].GetProperty("id").GetString()!;

        var deleteResponse = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/vcs/github/installations/{installationId}", operatorToken));
        Assert.Equal(204, (int)deleteResponse.StatusCode);

        var afterDelete = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            "/v1/console/vcs/github/installations", operatorToken)));
        Assert.Equal(0, afterDelete.GetProperty("total").GetInt32());
        Assert.Contains(_github.InstallationId, _github.DeletedInstallationIds);
    }

    [Fact]
    public async Task Disconnecting_an_unknown_installation_id_is_a_clean_404_not_a_crash()
    {
        var (operatorToken, _) = await SetupProjectAsync();

        var response = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/vcs/github/installations/{Guid.NewGuid()}", operatorToken));

        Assert.Equal(404, (int)response.StatusCode);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> CreateSiteAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites", operatorToken, new { key, name = key, rootDirectory = "" }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>Registers the repository (and its branches) as accessible to the fake installation, then drives the real callback endpoint so a real <c>VcsInstallation</c> row exists — <c>ConnectRepositoryAsync</c> refuses to connect a site until one does.</summary>
    private async Task ConnectGitHubAsync(string repositoryFullName, string[] branches)
    {
        _github.AccessibleRepositories.Add(repositoryFullName);
        _github.Branches = branches;
        var callback = await Client.GetAsync($"/v1/vcs/github/callback?installation_id={_github.InstallationId}");
        Assert.Equal(302, (int)callback.StatusCode);
    }

    private async Task ConnectRepositoryAsync(
        string operatorToken, string projectId, string siteId, string repositoryFullName, string productionBranch)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites/{siteId}/git", operatorToken,
            new { repositoryFullName, productionBranch }));
        Assert.Equal(200, (int)response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(byte[] body, string? signatureHeader = "compute")
    {
        var signature = signatureHeader == "compute" ? Sign(WebhookSecret, body) : signatureHeader;
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/vcs/github/webhook")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", "push");
        if (signature is not null)
            request.Headers.Add("X-Hub-Signature-256", signature);
        return await Client.SendAsync(request);
    }

    private static string Sign(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static byte[] BuildPushPayload(string repositoryFullName, string branch, string sha, string message) =>
        Encoding.UTF8.GetBytes($$"""
            {
              "ref": "refs/heads/{{branch}}",
              "after": "{{sha}}",
              "repository": { "full_name": "{{repositoryFullName}}" },
              "installation": { "id": 999 },
              "head_commit": { "id": "{{sha}}", "message": "{{message}}" }
            }
            """);

    private async Task<JsonElement> FindDeploymentByCommitAsync(
        string operatorToken, string projectId, string siteId, string commitSha)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var response = await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}/deployments", operatorToken));
            var body = await ReadJson(response);
            foreach (var d in body.GetProperty("deployments").EnumerateArray())
                if (d.GetProperty("commitSha").GetString() == commitSha)
                    return d;
            await Task.Delay(200);
        }
        throw new TimeoutException($"No deployment with commit '{commitSha}' appeared.");
    }

    /// <summary>
    /// A "ready" deployment status and the site actually being activated on it are two separate,
    /// sequential steps inside <c>SiteBuildWorker</c> — polling only for "ready" can observe the gap
    /// between them. Waits for both, mirroring <c>SiteCustomDomainTests.WaitForSiteRunningAsync</c>.
    /// </summary>
    private async Task WaitForSiteActiveAsync(string operatorToken, string projectId, string siteId, string deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{deploymentId}", operatorToken)));
            Assert.NotEqual("failed", deployment.GetProperty("status").GetString());

            var site = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken)));
            if (site.TryGetProperty("activeDeploymentId", out var active) && active.GetString() == deploymentId
                && site.GetProperty("isRunning").GetBoolean())
                return;

            await Task.Delay(300);
        }
        throw new TimeoutException($"Site never became active on deployment '{deploymentId}'.");
    }

    private async Task<JsonElement> WaitForDeploymentFinishedAsync(
        string operatorToken, string projectId, string siteId, string deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{deploymentId}", operatorToken)));
            var status = deployment.GetProperty("status").GetString();
            if (status is "ready" or "failed")
                return deployment;
            await Task.Delay(300);
        }
        throw new TimeoutException("Deployment never finished building.");
    }
}

/// <summary>Deterministic in-process stand-in for GitHub's REST API — no network, canned responses driven entirely by <see cref="AccessibleRepositories"/>/<see cref="Branches"/>.</summary>
public sealed class FakeGitHubClient : IGitHubClient
{
    public HashSet<string> AccessibleRepositories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] Branches { get; set; } = ["main"];
    public long InstallationId { get; set; } = 999;
    public string AccountLogin { get; set; } = "acme";
    public string AccountType { get; set; } = "Organization";

    public Task<GitHubAppInfo> GetAppAsync(CancellationToken ct) =>
        Task.FromResult(new GitHubAppInfo("praxy-test-app"));

    public Task<GitHubInstallation?> GetInstallationAsync(long installationId, CancellationToken ct) =>
        Task.FromResult(installationId == InstallationId ? new GitHubInstallation(InstallationId, AccountLogin, AccountType) : null);

    public Task<GitHubInstallation?> GetRepositoryInstallationAsync(string owner, string repo, CancellationToken ct) =>
        Task.FromResult(AccessibleRepositories.Contains($"{owner}/{repo}")
            ? new GitHubInstallation(InstallationId, AccountLogin, AccountType) : null);

    public Task<string> CreateInstallationTokenAsync(long installationId, CancellationToken ct) =>
        Task.FromResult("fake-installation-token");

    public Task<IReadOnlyList<string>> ListBranchesAsync(string installationToken, string owner, string repo, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Branches);

    public List<long> DeletedInstallationIds { get; } = [];

    public Task DeleteInstallationAsync(long installationId, CancellationToken ct)
    {
        DeletedInstallationIds.Add(installationId);
        return Task.CompletedTask;
    }
}

/// <summary>Materializes the same minimal fake Next.js-shaped app <c>SiteCustomDomainTests</c>/<c>SitesAskTlsTests</c> tar up, but as real files on disk — the real Docker build pipeline still runs genuinely, only the clone step is faked.</summary>
public sealed class FakeGitRepositoryCloner : IGitRepositoryCloner
{
    public Task<GitCheckout> CloneAsync(string repositoryFullName, string commitSha, string installationToken, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("praxy-vcs-test-").FullName;
        var serverJs = """
            require('http').createServer((req, res) => { res.end('ok'); })
              .listen(process.env.PORT || 3000, process.env.HOSTNAME || '0.0.0.0');
            """;
        var buildScript = "mkdir -p .next/standalone .next/static public && cp server.js .next/standalone/server.js";
        var packageJson = $$"""
            { "name": "fake-site", "version": "1.0.0", "scripts": { "build": "{{buildScript}}" } }
            """;
        File.WriteAllText(Path.Combine(dir, "package.json"), packageJson);
        File.WriteAllText(Path.Combine(dir, "server.js"), serverJs);
        return Task.FromResult(new GitCheckout(dir));
    }
}
