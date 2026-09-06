using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Praxy.Tests.Integration.Infrastructure;
using Praxy.Vcs;

namespace Praxy.Tests.Integration;

/// <summary>
/// security-review-phase-3: <c>SitesService.HandleGitPushAsync</c>/<c>FunctionsService.HandleGitPushAsync</c>
/// resolve a push purely by matching <c>RepositoryFullName</c>, project-agnostic — there is no
/// per-connection record of which GitHub App installation authorized it
/// (<c>GitHubAppService</c>'s own remark: "Praxy never tracked which installation covered which
/// repository"). Before this fix, two different projects on the same instance could both connect a
/// site or function to the identical <c>owner/repo</c> string, and a single push would redeploy both
/// — one project's build triggered, and its commit metadata copied into a deployment row, by a push
/// the other project's team never asked for. No forged webhook payload needed: two ordinary,
/// independently-created projects connecting to the same real repository is enough. Reuses
/// <see cref="SiteGitDeploymentTests"/>'s fakes (no real GitHub network call anywhere in this suite).
/// </summary>
public class VcsRepositoryIsolationTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const string WebhookSecret = "integration-test-webhook-secret";
    private readonly FakeGitHubClient _github = new();

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Vcs:GitHub:AppId"] = "test-app",
        ["Praxy:Vcs:GitHub:ClientId"] = "test-client",
        ["Praxy:Vcs:GitHub:ClientSecret"] = "test-client-secret",
        ["Praxy:Vcs:GitHub:PrivateKey"] = "unused-in-this-suite",
        ["Praxy:Vcs:GitHub:WebhookSecret"] = WebhookSecret,
    };

    protected override Action<IServiceCollection>? TestServices => services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IGitHubClient>(_github));
        services.Replace(ServiceDescriptor.Singleton<IGitRepositoryCloner>(new FakeGitRepositoryCloner()));
    };

    [Fact]
    public async Task A_second_projects_site_cannot_connect_to_a_repository_another_project_already_connected()
    {
        var (operatorToken, projectA) = await SetupProjectAsync("A");
        var projectB = await CreateSecondProjectAsync(operatorToken, "B");

        await ConnectGitHubAsync("acme/website", ["main"]);
        var siteA = await CreateSiteAsync(operatorToken, projectA, "blog");
        await ConnectSiteRepositoryAsync(operatorToken, projectA, siteA, "acme/website", "main", expectedStatus: 200);

        var siteB = await CreateSiteAsync(operatorToken, projectB, "blog");
        var response = await ConnectSiteRepositoryAsync(operatorToken, projectB, siteB, "acme/website", "main", expectedStatus: 409);
        await AssertError(response, 409, "vcs_repository_already_connected");
    }

    [Fact]
    public async Task A_second_projects_function_cannot_connect_to_a_repository_a_sites_project_already_connected()
    {
        var (operatorToken, projectA) = await SetupProjectAsync("A");
        var projectB = await CreateSecondProjectAsync(operatorToken, "B");

        await ConnectGitHubAsync("acme/website", ["main"]);
        var siteA = await CreateSiteAsync(operatorToken, projectA, "blog");
        await ConnectSiteRepositoryAsync(operatorToken, projectA, siteA, "acme/website", "main", expectedStatus: 200);

        var functionB = await CreateFunctionAsync(operatorToken, projectB, "worker");
        var response = await ConnectFunctionRepositoryAsync(operatorToken, projectB, functionB, "acme/website", "main");
        await AssertError(response, 409, "vcs_repository_already_connected");
    }

    /// <summary>The documented, intended case stays allowed: one project's site and function sharing one repository.</summary>
    [Fact]
    public async Task A_site_and_a_function_in_the_same_project_may_share_one_repository()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await ConnectGitHubAsync("acme/website", ["main"]);

        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        await ConnectSiteRepositoryAsync(operatorToken, projectId, siteId, "acme/website", "main", expectedStatus: 200);

        var functionId = await CreateFunctionAsync(operatorToken, projectId, "worker");
        var response = await ConnectFunctionRepositoryAsync(operatorToken, projectId, functionId, "acme/website", "main");
        Assert.Equal(200, (int)response.StatusCode);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> CreateSecondProjectAsync(string operatorToken, string name)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", operatorToken, new { name }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task ConnectGitHubAsync(string repositoryFullName, string[] branches)
    {
        _github.AccessibleRepositories.Add(repositoryFullName);
        _github.Branches = branches;
        var callback = await Client.GetAsync($"/v1/vcs/github/callback?installation_id={_github.InstallationId}");
        Assert.Equal(302, (int)callback.StatusCode);
    }

    private async Task<string> CreateSiteAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites", operatorToken, new { key, name = key, rootDirectory = "" }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateFunctionAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new { key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 15, execute = Array.Empty<string>() }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> ConnectSiteRepositoryAsync(
        string operatorToken, string projectId, string siteId, string repositoryFullName, string productionBranch, int expectedStatus)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites/{siteId}/git", operatorToken,
            new { repositoryFullName, productionBranch }));
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        return response;
    }

    private async Task<HttpResponseMessage> ConnectFunctionRepositoryAsync(
        string operatorToken, string projectId, string functionId, string repositoryFullName, string productionBranch) =>
        await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/git", operatorToken,
            new { repositoryFullName, productionBranch }));
}
