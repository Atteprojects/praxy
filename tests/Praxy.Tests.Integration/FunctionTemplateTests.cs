using System.Net.Http.Json;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The bundled function starters (<c>FunctionTemplates.cs</c>) against a real Docker daemon — the
/// same "no stubbing the Docker leg" discipline <see cref="FunctionTests"/> uses. Each template must
/// actually build and run through the real <c>FunctionBuildWorker</c>/<c>DockerExecutor</c> path, not
/// just produce tar bytes that look reasonable.
/// </summary>
public class FunctionTemplateTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Functions:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Functions:ExecutionPollIntervalSeconds"] = "1",
        ["Praxy:Functions:BuildTimeoutSeconds"] = "120",
    };

    [Fact]
    public async Task Template_catalog_lists_the_bundled_templates()
    {
        var response = await Client.GetAsync("/v1/functions/templates");
        Assert.Equal(200, (int)response.StatusCode);
        var body = await ReadJson(response);
        var templates = body.GetProperty("templates").EnumerateArray().ToList();
        var keys = templates.Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Contains("http-echo", keys);
        Assert.Contains("scheduled-cleanup", keys);
        Assert.Contains("webhook-receiver", keys);

        var echo = templates.First(t => t.GetProperty("key").GetString() == "http-echo");
        Assert.Equal("dart", echo.GetProperty("runtime").GetString());
        Assert.Equal("main.dart", echo.GetProperty("entrypoint").GetString());

        var cleanup = templates.First(t => t.GetProperty("key").GetString() == "scheduled-cleanup");
        Assert.Equal("node", cleanup.GetProperty("runtime").GetString());
        Assert.Equal("0 3 * * *", cleanup.GetProperty("defaultSchedule").GetString());
    }

    [Fact]
    public async Task Unknown_template_key_is_rejected()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/from-template", operatorToken,
            new { templateKey = "does-not-exist", key = "x", name = "X" }));
        Assert.Equal(404, (int)response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("function_template_not_found", body.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("http-echo")]
    [InlineData("scheduled-cleanup")]
    [InlineData("webhook-receiver")]
    public async Task Each_bundled_template_builds_and_auto_activates_for_real(string templateKey)
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (functionId, deploymentId) = await CreateFromTemplateAsync(operatorToken, projectId, templateKey);

        var deployment = await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "ready");
        Assert.Equal("ready", deployment.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(deployment.GetProperty("imageTag").GetString()));

        var fn = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken)));
        // The build worker auto-activates the newest successful build — same as a normal tar upload.
        Assert.Equal(deploymentId, fn.GetProperty("activeDeploymentId").GetString());
    }

    [Fact]
    public async Task Http_echo_template_actually_echoes_the_request()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (functionId, deploymentId) = await CreateFromTemplateAsync(operatorToken, projectId, "http-echo");
        await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "ready");

        var invoke = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/executions", operatorToken,
            new { method = "POST", path = "/greet", body = "hello there" }));
        Assert.Equal(200, (int)invoke.StatusCode);
        var execution = await ReadJson(invoke);
        Assert.Equal("completed", execution.GetProperty("status").GetString());
        Assert.Equal(200, execution.GetProperty("statusCode").GetInt32());

        using var echoed = JsonDocument.Parse(execution.GetProperty("responseBody").GetString()!);
        Assert.Equal("POST", echoed.RootElement.GetProperty("method").GetString());
        Assert.Equal("/greet", echoed.RootElement.GetProperty("path").GetString());
        Assert.Equal("hello there", echoed.RootElement.GetProperty("body").GetString());
    }

    /// <summary>
    /// Doesn't (and can't, in this test harness — see the report's "what wasn't tested" note) exercise
    /// the template's real Tables round trip, which needs the function container to call back out to
    /// a live Praxy API reachable at PRAXY_ENDPOINT. What it does prove: the built image actually runs,
    /// the entrypoint resolves, and the template's own config guard — not a container crash — is what
    /// produces the failure when PRAXY_API_KEY/CLEANUP_DATABASE_ID/CLEANUP_TABLE_ID are unset.
    /// </summary>
    [Fact]
    public async Task Scheduled_cleanup_template_fails_closed_without_required_env_vars()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (functionId, deploymentId) = await CreateFromTemplateAsync(operatorToken, projectId, "scheduled-cleanup");
        await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "ready");

        var invoke = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/executions", operatorToken,
            new { method = "GET", path = "/" }));
        Assert.Equal(200, (int)invoke.StatusCode);
        var execution = await ReadJson(invoke);
        Assert.Equal("completed", execution.GetProperty("status").GetString());
        Assert.Equal(500, execution.GetProperty("statusCode").GetInt32());
        Assert.Contains("Missing required env var", execution.GetProperty("responseBody").GetString());
    }

    /// <summary>Same "runs for real, fails closed on its own config guard" proof as the cleanup job's test — see its remarks.</summary>
    [Fact]
    public async Task Webhook_receiver_template_fails_closed_without_a_configured_secret()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (functionId, deploymentId) = await CreateFromTemplateAsync(operatorToken, projectId, "webhook-receiver");
        await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "ready");

        var invoke = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/executions", operatorToken,
            new { method = "POST", path = "/", body = "{}" }));
        Assert.Equal(200, (int)invoke.StatusCode);
        var execution = await ReadJson(invoke);
        Assert.Equal("completed", execution.GetProperty("status").GetString());
        Assert.Equal(500, execution.GetProperty("statusCode").GetInt32());
        Assert.Contains("WEBHOOK_SECRET", execution.GetProperty("responseBody").GetString());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<(string FunctionId, string DeploymentId)> CreateFromTemplateAsync(
        string operatorToken, string projectId, string templateKey)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/from-template", operatorToken,
            new { templateKey, key = templateKey, name = templateKey }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return (body.GetProperty("function").GetProperty("id").GetString()!, body.GetProperty("deployment").GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> WaitForDeploymentStatusAsync(
        string operatorToken, string projectId, string functionId, string deploymentId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/deployments/{deploymentId}", operatorToken)));
            var status = deployment.GetProperty("status").GetString();
            if (status == targetStatus || status == "failed")
                return deployment;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Deployment never reached status '{targetStatus}'.");
    }
}
