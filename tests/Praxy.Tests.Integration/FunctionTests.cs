using System.Formats.Tar;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The Phase 7 owner-test flow against a real Docker daemon — <c>FunctionBuildWorker</c>,
/// <c>FunctionExecutionWorker</c>, <c>FunctionEventDispatcher</c> and <c>FunctionScheduler</c> are
/// real hosted services in this process doing real <c>docker build</c>/<c>docker run</c> calls, not
/// stubbed (the same "no ASP.NET Core in-memory transport on the outbound leg" discipline
/// <c>WebhookDeliveryTests</c> uses for its HTTP leg, here applied to the Docker leg). Requires a
/// reachable Docker daemon and the <c>node:22-alpine</c> image — both already proven available in
/// this project's environment by every prior phase's Testcontainers-backed run.
/// </summary>
public class FunctionTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Functions:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Functions:ExecutionPollIntervalSeconds"] = "1",
        ["Praxy:Functions:SchedulePollIntervalSeconds"] = "1",
        ["Praxy:Functions:BuildTimeoutSeconds"] = "120",
    };

    [Fact]
    public async Task Deploy_from_console_invoke_sync_and_async_and_trigger_via_row_create()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write", "functions.execute");

        var indexJs = """
            module.exports = async (context) => ({
              statusCode: 200,
              body: JSON.stringify({ echoedPath: context.path, echoedMethod: context.method }),
              headers: {},
            });
            """;

        // ---- sync function ----------------------------------------------------------------
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter", "node", "index.js");
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, functionId, BuildTar(("index.js", indexJs)));
        var deployment = await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "ready");
        Assert.Equal("ready", deployment.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(deployment.GetProperty("imageTag").GetString()));

        var fn = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken)));
        Assert.Equal(deploymentId, fn.GetProperty("activeDeploymentId").GetString());

        // Sync invoke: awaited directly, response and log come back in the same call.
        var syncResponse = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/executions", operatorToken,
            new { method = "GET", path = "/hello" }));
        Assert.Equal(200, (int)syncResponse.StatusCode);
        var syncExecution = await ReadJson(syncResponse);
        Assert.Equal("completed", syncExecution.GetProperty("status").GetString());
        Assert.Equal(200, syncExecution.GetProperty("statusCode").GetInt32());
        Assert.Contains("/hello", syncExecution.GetProperty("responseBody").GetString());
        // Null properties are omitted entirely (Program.cs's DefaultIgnoreCondition) — a clean
        // execution simply has no "errors" key, not a present-with-null one.
        Assert.False(syncExecution.TryGetProperty("errors", out _));

        // Async invoke via the data-plane endpoint (API key, functions.execute scope): the initial
        // response is just the queued row — the stored result must be polled for, and it must
        // actually be stored, not discarded (the roadmap's explicit async/sync distinction).
        var asyncResponse = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/functions/{functionId}/executions?async=true", projectId, apiKey: apiKey,
            body: new { method = "GET", path = "/async-hello" }));
        Assert.Equal(202, (int)asyncResponse.StatusCode);
        var queued = await ReadJson(asyncResponse);
        Assert.Equal("waiting", queued.GetProperty("status").GetString());
        var asyncExecutionId = queued.GetProperty("id").GetString()!;

        var asyncExecution = await WaitForExecutionStatusAsync(operatorToken, projectId, functionId, asyncExecutionId, "completed");
        Assert.Equal(200, asyncExecution.GetProperty("statusCode").GetInt32());
        Assert.Contains("/async-hello", asyncExecution.GetProperty("responseBody").GetString());

        // ---- event-triggered function -----------------------------------------------------
        var triggeredFunctionId = await CreateFunctionAsync(
            operatorToken, projectId, "on-row-create", "node", "index.js",
            events: ["databases.*.tables.*.rows.*.create"]);
        var triggeredDeploymentId = await UploadDeploymentAsync(
            operatorToken, projectId, triggeredFunctionId, BuildTar(("index.js", indexJs)));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, triggeredFunctionId, triggeredDeploymentId, "ready");

        var (databaseId, tableId) = await CreateTableAsync(projectId, apiKey);
        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey,
            body: new { data = new { title = "trigger me" } }));

        var triggered = await WaitForAnyExecutionAsync(operatorToken, projectId, triggeredFunctionId, "event");
        Assert.Equal("completed", triggered.GetProperty("status").GetString());
        Assert.StartsWith("event:databases.", triggered.GetProperty("triggeredBy").GetString());
    }

    [Fact]
    public async Task Failed_build_shows_its_log()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "broken", "node", "index.js");

        // package.json that isn't valid JSON makes `npm install` — a RUN step in the generated
        // Dockerfile — exit non-zero, which is a genuine `docker build` failure, not a runtime one.
        var tar = BuildTar(
            ("index.js", "module.exports = async () => ({ statusCode: 200 });"),
            ("package.json", "{ this is not json"));
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, functionId, tar);

        var deployment = await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "failed");
        Assert.Equal("failed", deployment.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(deployment.GetProperty("error").GetString()));
        Assert.False(string.IsNullOrEmpty(deployment.GetProperty("buildLog").GetString()));

        var fn = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken)));
        // Null properties are omitted entirely (Program.cs's DefaultIgnoreCondition), so "never
        // activated" means the key is simply absent, not present-with-null.
        Assert.False(fn.TryGetProperty("activeDeploymentId", out _));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> CreateFunctionAsync(
        string operatorToken, string projectId, string key, string runtime, string entrypoint,
        string[]? events = null, string? schedule = null)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new { key, name = key, runtime, entrypoint, timeoutSeconds = 15, events = events ?? [], schedule }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> UploadDeploymentAsync(
        string operatorToken, string projectId, string functionId, byte[] tar)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/deployments")
        {
            Content = new ByteArrayContent(tar),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        request.Headers.Add("X-Praxy-Session", operatorToken);
        var response = await Client.SendAsync(request);
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
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

    private async Task<JsonElement> WaitForExecutionStatusAsync(
        string operatorToken, string projectId, string functionId, string executionId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var execution = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/executions/{executionId}", operatorToken)));
            var status = execution.GetProperty("status").GetString();
            if (status == targetStatus || status == "failed")
                return execution;
            await Task.Delay(300);
        }
        throw new TimeoutException($"Execution never reached status '{targetStatus}'.");
    }

    private async Task<JsonElement> WaitForAnyExecutionAsync(
        string operatorToken, string projectId, string functionId, string trigger)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/executions", operatorToken)));
            var match = list.GetProperty("executions").EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("trigger").GetString() == trigger);
            if (match.ValueKind == JsonValueKind.Object &&
                match.GetProperty("status").GetString() is "completed" or "failed")
                return match;
            await Task.Delay(300);
        }
        throw new TimeoutException($"No '{trigger}'-triggered execution completed in time.");
    }

    private async Task<(string DatabaseId, string TableId)> CreateTableAsync(string projectId, string apiKey)
    {
        var database = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            "/v1/databases", projectId, apiKey: apiKey, body: new { key = "shop", name = "Shop" })));
        var databaseId = database.GetProperty("id").GetString()!;
        var table = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables", projectId, apiKey: apiKey, body: new { key = "orders", name = "Orders" })));
        var tableId = table.GetProperty("id").GetString()!;

        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/string", projectId, apiKey: apiKey,
            body: new { key = "title", size = 200, required = true }));
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { permissions = new[] { "create(\"any\")", "read(\"any\")" } }));

        return (databaseId, tableId);
    }

    private static byte[] BuildTar(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                };
                writer.WriteEntry(entry);
            }
        }
        return stream.ToArray();
    }
}
