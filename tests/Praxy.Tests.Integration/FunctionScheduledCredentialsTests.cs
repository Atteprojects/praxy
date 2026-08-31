using System.Formats.Tar;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Real Docker, same discipline as <see cref="FunctionTests"/>: <c>FunctionScheduler</c>,
/// <c>FunctionEventDispatcher</c> and <c>FunctionExecutionWorker</c> are real hosted services doing
/// real <c>docker build</c>/<c>docker run</c> calls, not stubbed. Covers the gap
/// docs/handoff/functions-scheduled-credentials-report.md closes: a schedule- or event-triggered
/// execution has no calling app user to mint <c>PRAXY_FUNCTION_JWT</c> for, so it got nothing at all
/// before this — now it gets <c>PRAXY_FUNCTION_API_KEY</c>, but only once an operator has explicitly
/// granted the function platform scopes.
///
/// The function under test never makes its own outbound HTTP call — Praxy injects no "call back to
/// this instance" URL into a container today (a real, separate gap, out of this task's scope; noted
/// in the report). Instead it echoes <c>PRAXY_FUNCTION_API_KEY</c> back in its response body, and the
/// test itself — which already has a real path to the API — uses that literal value as
/// <c>X-Praxy-Key</c> against the normal Tables endpoints. That still proves the credential the
/// function actually received is real and scoped correctly; it just relocates who dials out.
/// </summary>
public class FunctionScheduledCredentialsTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Functions:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Functions:ExecutionPollIntervalSeconds"] = "1",
        ["Praxy:Functions:SchedulePollIntervalSeconds"] = "1",
        ["Praxy:Functions:BuildTimeoutSeconds"] = "120",
    };

    /// <summary>Echoes the one env var this feature injects, or null when nothing was granted.</summary>
    private const string EchoKeyJs = """
        module.exports = async () => ({
          statusCode: 200,
          body: JSON.stringify({ key: process.env.PRAXY_FUNCTION_API_KEY || null }),
          headers: {},
        });
        """;

    [Fact]
    public async Task Schedule_triggered_execution_gets_the_platform_key_only_when_scopes_are_granted()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, setupKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");

        // A fully private table — nobody has been granted read on it. The zero-scope function has no
        // credential to even try with, so a plain unauthenticated read is the only thing to check,
        // and the normal row-permission filter (RowsService.ListAsync bakes permissions into the SQL
        // predicate itself) silently returns nothing to a caller nothing was granted to — no error,
        // no function-specific special case, exactly like every other guest request on a private table.
        var (privateDb, privateTable) = await CreateTableAsync(projectId, setupKey, grantAnyRead: false);

        // A shared table only the granted-scope function's key needs to actually read data back from.
        var (sharedDb, sharedTable) = await CreateTableAsync(projectId, setupKey, grantAnyRead: true);
        await CreateRowAsync(projectId, setupKey, sharedDb, sharedTable, "seeded");

        // ---- zero scopes granted --------------------------------------------------------------
        var noneId = await CreateFunctionAsync(operatorToken, projectId, "cred-none", schedule: "* * * * *");
        await UploadAndWaitReadyAsync(operatorToken, projectId, noneId);

        var noneExecution = await WaitForAnyExecutionAsync(operatorToken, projectId, noneId, "schedule");
        Assert.Equal("completed", noneExecution.GetProperty("status").GetString());
        var noneBody = JsonDocument.Parse(noneExecution.GetProperty("responseBody").GetString()!).RootElement;
        Assert.Equal(JsonValueKind.Null, noneBody.GetProperty("key").ValueKind);

        var privateRead = await ReadJson(await Client.SendAsync(
            DataPlane(HttpMethod.Get, $"/v1/databases/{privateDb}/tables/{privateTable}/rows", projectId)));
        Assert.Equal(0, privateRead.GetProperty("total").GetInt32());

        // ---- databases.read granted before the function ever fires -----------------------------
        var grantedId = await CreateFunctionAsync(operatorToken, projectId, "cred-granted", schedule: "* * * * *");
        await GrantPlatformScopesAsync(operatorToken, projectId, grantedId, "databases.read");
        await UploadAndWaitReadyAsync(operatorToken, projectId, grantedId);

        var grantedExecution = await WaitForAnyExecutionAsync(operatorToken, projectId, grantedId, "schedule");
        Assert.Equal("completed", grantedExecution.GetProperty("status").GetString());
        var grantedBody = JsonDocument.Parse(grantedExecution.GetProperty("responseBody").GetString()!).RootElement;
        var injectedKey = grantedBody.GetProperty("key").GetString();
        Assert.False(string.IsNullOrEmpty(injectedKey));

        var sharedRead = await ReadJson(await Client.SendAsync(
            DataPlane(HttpMethod.Get, $"/v1/databases/{sharedDb}/tables/{sharedTable}/rows", projectId, apiKey: injectedKey)));
        Assert.Equal(1, sharedRead.GetProperty("total").GetInt32());
        Assert.Equal("seeded", sharedRead.GetProperty("rows")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Event_triggered_execution_gets_the_same_platform_key_treatment_as_schedule()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, setupKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");

        var (databaseId, tableId) = await CreateTableAsync(projectId, setupKey, grantAnyRead: true);

        var functionId = await CreateFunctionAsync(
            operatorToken, projectId, "cred-event", events: ["databases.*.tables.*.rows.*.create"]);
        await GrantPlatformScopesAsync(operatorToken, projectId, functionId, "databases.read");
        await UploadAndWaitReadyAsync(operatorToken, projectId, functionId);

        // Both rows are created only once the function is enabled *and* deployed — a row created any
        // earlier queues an outbox event FunctionEventDispatcher can end up matching against this
        // function later (dispatch order isn't tied to when the row was written, and Enabled flips
        // true the instant the function is created, well before it has a deployment to run), producing
        // a spurious execution that fails with "No active deployment" and racing the real one below.
        await CreateRowAsync(projectId, setupKey, databaseId, tableId, "already there");
        // The trigger itself: any row create on this project fans out to every subscribed function.
        await CreateRowAsync(projectId, setupKey, databaseId, tableId, "the trigger");

        var execution = await WaitForAnyExecutionAsync(operatorToken, projectId, functionId, "event");
        Assert.Equal("completed", execution.GetProperty("status").GetString());
        var body = JsonDocument.Parse(execution.GetProperty("responseBody").GetString()!).RootElement;
        var injectedKey = body.GetProperty("key").GetString();
        Assert.False(string.IsNullOrEmpty(injectedKey));

        var read = await ReadJson(await Client.SendAsync(
            DataPlane(HttpMethod.Get, $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: injectedKey)));
        Assert.Equal(2, read.GetProperty("total").GetInt32());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> CreateFunctionAsync(
        string operatorToken, string projectId, string key, string[]? events = null, string? schedule = null)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new
            {
                key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 15,
                events = events ?? [], schedule,
            }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task GrantPlatformScopesAsync(
        string operatorToken, string projectId, string functionId, params string[] scopes)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken,
            new { platformScopes = scopes }));
        Assert.Equal(200, (int)response.StatusCode);
    }

    private async Task UploadAndWaitReadyAsync(string operatorToken, string projectId, string functionId)
    {
        var upload = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/deployments")
        {
            Content = new ByteArrayContent(BuildTar(("index.js", EchoKeyJs))),
        };
        upload.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        upload.Headers.Add("X-Praxy-Session", operatorToken);
        var response = await Client.SendAsync(upload);
        Assert.Equal(201, (int)response.StatusCode);
        var deploymentId = (await ReadJson(response)).GetProperty("id").GetString()!;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/deployments/{deploymentId}", operatorToken)));
            var status = deployment.GetProperty("status").GetString();
            if (status is "ready" or "failed")
            {
                Assert.Equal("ready", status);
                // FunctionBuildWorker flips the deployment to "ready" and activates it on the
                // function (fn.ActiveDeploymentId) in two separate writes, not one transaction —
                // waiting for the first alone leaves a real (if narrow) window where a trigger fired
                // immediately after would still see "no active deployment". Close it here instead of
                // racing every caller of this helper against it.
                await WaitForActivationAsync(operatorToken, projectId, functionId, deploymentId);
                return;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("Deployment never became ready.");
    }

    private async Task WaitForActivationAsync(
        string operatorToken, string projectId, string functionId, string deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var fn = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken)));
            if (fn.TryGetProperty("activeDeploymentId", out var active) && active.GetString() == deploymentId)
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Deployment reported ready but was never activated on the function.");
    }

    /// <summary>Schedule and event triggers create async executions — poll until one of the given trigger reaches a final status.</summary>
    private async Task<JsonElement> WaitForAnyExecutionAsync(
        string operatorToken, string projectId, string functionId, string trigger)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/executions", operatorToken)));
            var match = list.GetProperty("executions").EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("trigger").GetString() == trigger);
            if (match.ValueKind == JsonValueKind.Object &&
                match.GetProperty("status").GetString() is "completed" or "failed")
                return match;
            await Task.Delay(500);
        }
        throw new TimeoutException($"No '{trigger}'-triggered execution completed in time.");
    }

    private async Task<(string DatabaseId, string TableId)> CreateTableAsync(
        string projectId, string apiKey, bool grantAnyRead)
    {
        var database = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            "/v1/databases", projectId, apiKey: apiKey, body: new { key = $"db{Guid.NewGuid():n}"[..12], name = "DB" })));
        var databaseId = database.GetProperty("id").GetString()!;
        var table = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables", projectId, apiKey: apiKey, body: new { key = "orders", name = "Orders" })));
        var tableId = table.GetProperty("id").GetString()!;

        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/string", projectId, apiKey: apiKey,
            body: new { key = "title", size = 200, required = true }));

        if (grantAnyRead)
        {
            await Client.SendAsync(DataPlane(HttpMethod.Patch,
                $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
                body: new { permissions = new[] { "create(\"any\")", "read(\"any\")" } }));
        }
        else
        {
            // Still needs create("any") so the setup key itself can seed rows through the normal
            // data-plane path rather than reaching around it — read stays ungranted on purpose.
            await Client.SendAsync(DataPlane(HttpMethod.Patch,
                $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
                body: new { permissions = new[] { "create(\"any\")" } }));
        }

        return (databaseId, tableId);
    }

    private async Task CreateRowAsync(
        string projectId, string apiKey, string databaseId, string tableId, string title)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey,
            body: new { data = new { title } }));
        Assert.Equal(201, (int)response.StatusCode);
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
