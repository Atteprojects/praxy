using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Praxy.Tests.Integration.Infrastructure;
using Praxy.Vcs;

namespace Praxy.Tests.Integration;

/// <summary>
/// Functions git integration's own owner-test flow, end to end through the real webhook endpoint and
/// <c>FunctionBuildWorker</c> — <see cref="IGitHubClient"/> and <see cref="IGitRepositoryCloner"/> are
/// swapped for fakes (same testing posture <c>SiteGitDeploymentTests</c> uses; <see cref="FakeGitHubClient"/>
/// is reused directly from there since it's fully generic, no Sites types anywhere in it). Everything
/// downstream — signature verification, the webhook dispatch query, deployment creation, the real
/// Docker build, and the auto-activate-only-on-production-branch guard — runs for real. No real GitHub
/// App, installation, or network call happens anywhere in this suite.
/// </summary>
public class FunctionGitDeploymentTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const string WebhookSecret = "integration-test-webhook-secret";
    private readonly FakeGitHubClient _github = new();

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Functions:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Functions:BuildTimeoutSeconds"] = "60",
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
        services.Replace(ServiceDescriptor.Singleton<IGitRepositoryCloner>(new FakeFunctionGitRepositoryCloner()));
    };

    [Fact]
    public async Task A_push_to_the_production_branch_creates_and_auto_activates_a_deployment()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        await ConnectGitHubAsync("acme/greeter-fn", ["main", "preview"]);
        await ConnectRepositoryAsync(operatorToken, projectId, functionId, "acme/greeter-fn", "main");

        var response = await PostWebhookAsync(BuildPushPayload("acme/greeter-fn", "main", "commit-1", "Ship it"));
        Assert.Equal(204, (int)response.StatusCode);

        var deployment = await FindDeploymentByCommitAsync(operatorToken, projectId, functionId, "commit-1");
        Assert.Equal("git", deployment.GetProperty("source").GetString());
        Assert.Equal("main", deployment.GetProperty("branch").GetString());
        Assert.Equal("Ship it", deployment.GetProperty("commitMessage").GetString());
        var deploymentId = deployment.GetProperty("id").GetString()!;

        await WaitForFunctionActiveAsync(operatorToken, projectId, functionId, deploymentId);
    }

    [Fact]
    public async Task A_push_to_a_non_production_branch_builds_a_ready_deployment_without_touching_the_active_one()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        await ConnectGitHubAsync("acme/greeter-fn", ["main", "preview"]);
        await ConnectRepositoryAsync(operatorToken, projectId, functionId, "acme/greeter-fn", "main");

        // A production push first, so there's a real active deployment to prove stays untouched.
        await PostWebhookAsync(BuildPushPayload("acme/greeter-fn", "main", "commit-prod", "Prod"));
        var prod = await FindDeploymentByCommitAsync(operatorToken, projectId, functionId, "commit-prod");
        var activeBefore = prod.GetProperty("id").GetString()!;
        await WaitForFunctionActiveAsync(operatorToken, projectId, functionId, activeBefore);

        var response = await PostWebhookAsync(BuildPushPayload("acme/greeter-fn", "preview", "commit-preview", "Try this"));
        Assert.Equal(204, (int)response.StatusCode);

        var preview = await FindDeploymentByCommitAsync(operatorToken, projectId, functionId, "commit-preview");
        var previewId = preview.GetProperty("id").GetString()!;
        var finishedPreview = await WaitForDeploymentFinishedAsync(operatorToken, projectId, functionId, previewId);
        Assert.Equal("ready", finishedPreview.GetProperty("status").GetString());
        // Null properties are omitted entirely (Program.cs's DefaultIgnoreCondition) — a never-activated
        // deployment simply has no "activatedAt" key, not a present-with-null one.
        Assert.False(finishedPreview.TryGetProperty("activatedAt", out _));

        var fnAfterPreview = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken)));
        Assert.Equal(activeBefore, fnAfterPreview.GetProperty("activeDeploymentId").GetString());
        Assert.NotEqual(previewId, fnAfterPreview.GetProperty("activeDeploymentId").GetString());
    }

    [Fact]
    public async Task An_unsigned_or_badly_signed_webhook_is_rejected_before_any_deployment_is_created()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        await ConnectGitHubAsync("acme/greeter-fn", ["main"]);
        await ConnectRepositoryAsync(operatorToken, projectId, functionId, "acme/greeter-fn", "main");

        var payload = BuildPushPayload("acme/greeter-fn", "main", "unsigned-commit", "Nope");

        var unsigned = await PostWebhookAsync(payload, signatureHeader: null);
        Assert.Equal(401, (int)unsigned.StatusCode);

        var badSecret = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("wrong-secret"), payload));
        var badlySigned = await PostWebhookAsync(payload, badSecret);
        Assert.Equal(401, (int)badlySigned.StatusCode);

        var deployments = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}/deployments", operatorToken)));
        Assert.Equal(0, deployments.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_push_for_a_repository_no_function_references_is_a_no_op()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        await ConnectGitHubAsync("acme/greeter-fn", ["main"]);
        await ConnectRepositoryAsync(operatorToken, projectId, functionId, "acme/greeter-fn", "main");

        var response = await PostWebhookAsync(BuildPushPayload("someone-else/unrelated", "main", "commit-x", "Not us"));
        Assert.Equal(204, (int)response.StatusCode);

        var deployments = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}/deployments", operatorToken)));
        Assert.Equal(0, deployments.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The one genuinely new cross-resource check the kickoff prompt's "Done means" calls out: the same
    /// GitHub App installation now serves two resource types, and a repository can be connected by a
    /// site and a function simultaneously (even with different production branches). A single push must
    /// trigger both independently — exercises <c>VcsEndpoints.Webhook</c>'s new second dispatch call.
    /// </summary>
    [Fact]
    public async Task A_push_matching_both_a_connected_site_and_a_connected_function_triggers_both_independently()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        await ConnectGitHubAsync("acme/monorepo", ["main", "site-main"]);
        await ConnectRepositoryAsync(operatorToken, projectId, functionId, "acme/monorepo", "main");
        await ConnectSiteRepositoryAsync(operatorToken, projectId, siteId, "acme/monorepo", "site-main");

        var response = await PostWebhookAsync(BuildPushPayload("acme/monorepo", "main", "commit-both", "Touch both"));
        Assert.Equal(204, (int)response.StatusCode);

        var fnDeployments = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}/deployments", operatorToken)));
        Assert.Equal(1, fnDeployments.GetProperty("total").GetInt32());
        Assert.Equal("commit-both",
            fnDeployments.GetProperty("deployments")[0].GetProperty("commitSha").GetString());

        // The site's production branch ("site-main") didn't match this push's branch ("main"), but a
        // site deployment is still created for any push to the connected repository — only auto-activate
        // is branch-gated, not deployment creation itself (SitesService.HandleGitPushAsync's own rule).
        var siteDeployments = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/sites/{siteId}/deployments", operatorToken)));
        Assert.Equal(1, siteDeployments.GetProperty("total").GetInt32());
        Assert.Equal("commit-both",
            siteDeployments.GetProperty("deployments")[0].GetProperty("commitSha").GetString());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> CreateFunctionAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new { key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 15 }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSiteAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites", operatorToken, new { key, name = key, rootDirectory = "" }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>Registers the repository (and its branches) as accessible to the fake installation, then drives the real callback endpoint so a real <c>VcsInstallation</c> row exists — <c>ConnectRepositoryAsync</c> refuses to connect until one does.</summary>
    private async Task ConnectGitHubAsync(string repositoryFullName, string[] branches)
    {
        _github.AccessibleRepositories.Add(repositoryFullName);
        _github.Branches = branches;
        var callback = await Client.GetAsync($"/v1/vcs/github/callback?installation_id={_github.InstallationId}");
        Assert.Equal(302, (int)callback.StatusCode);
    }

    private async Task ConnectRepositoryAsync(
        string operatorToken, string projectId, string functionId, string repositoryFullName, string productionBranch)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/git", operatorToken,
            new { repositoryFullName, productionBranch }));
        Assert.Equal(200, (int)response.StatusCode);
    }

    private async Task ConnectSiteRepositoryAsync(
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
        string operatorToken, string projectId, string functionId, string commitSha)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var response = await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/deployments", operatorToken));
            var body = await ReadJson(response);
            foreach (var d in body.GetProperty("deployments").EnumerateArray())
                if (d.GetProperty("commitSha").GetString() == commitSha)
                    return d;
            await Task.Delay(200);
        }
        throw new TimeoutException($"No deployment with commit '{commitSha}' appeared.");
    }

    /// <summary>
    /// A "ready" deployment status and the function actually being flipped active are two separate,
    /// sequential steps inside <c>FunctionBuildWorker</c> — polling only for "ready" can observe the gap
    /// between them. Simpler than Sites' own wait: Functions has no long-lived container/"isRunning"
    /// concept, activation is just the two DB writes (deployment's <c>activatedAt</c>, function's
    /// <c>activeDeploymentId</c>).
    /// </summary>
    private async Task WaitForFunctionActiveAsync(string operatorToken, string projectId, string functionId, string deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/deployments/{deploymentId}", operatorToken)));
            Assert.NotEqual("failed", deployment.GetProperty("status").GetString());

            var fn = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken)));
            if (fn.TryGetProperty("activeDeploymentId", out var active) && active.GetString() == deploymentId)
                return;

            await Task.Delay(300);
        }
        throw new TimeoutException($"Function never became active on deployment '{deploymentId}'.");
    }

    private async Task<JsonElement> WaitForDeploymentFinishedAsync(
        string operatorToken, string projectId, string functionId, string deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/deployments/{deploymentId}", operatorToken)));
            var status = deployment.GetProperty("status").GetString();
            if (status is "ready" or "failed")
                return deployment;
            await Task.Delay(300);
        }
        throw new TimeoutException("Deployment never finished building.");
    }
}

/// <summary>Materializes a bare Node function (mirroring <c>RuntimeTemplates</c>' wrapper contract) as real files on disk — the real Docker build pipeline still runs genuinely, only the clone step is faked. Not the Sites test's own <c>FakeGitRepositoryCloner</c>: that one bakes a Next.js-shaped fixture, which wouldn't build under Functions' generated Dockerfile/wrapper.</summary>
public sealed class FakeFunctionGitRepositoryCloner : IGitRepositoryCloner
{
    public Task<GitCheckout> CloneAsync(string repositoryFullName, string commitSha, string installationToken, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("praxy-vcs-fn-test-").FullName;
        var indexJs = """
            module.exports = async (context) => ({
              statusCode: 200,
              body: JSON.stringify({ echoedPath: context.path }),
              headers: {},
            });
            """;
        File.WriteAllText(Path.Combine(dir, "index.js"), indexJs);
        return Task.FromResult(new GitCheckout(dir));
    }
}
