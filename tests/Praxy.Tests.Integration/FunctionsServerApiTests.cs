using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The server-side function management surface (<c>/v1/functions</c>, API key,
/// <c>functions.read</c>/<c>functions.write</c>/<c>execution.read</c> scopes) — before this, every
/// management operation (create/update/delete a function, set an env var, upload or activate a
/// deployment) required a console operator session; there was no way for a CI/CD pipeline or backend
/// script to do any of it. Docker-free: CRUD, env vars, and deployment *metadata* never touch Docker
/// — only building a deployment's image does, and <see cref="FunctionTests"/> owns that real
/// end-to-end coverage (including a deployment built and activated through this exact surface).
/// </summary>
public class FunctionsServerApiTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task A_functions_write_key_creates_a_function_and_a_functions_read_key_sees_it()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, writeKey) = await CreateApiKeyAsync(operatorToken, projectId, "functions.write");
        var (_, readKey) = await CreateApiKeyAsync(operatorToken, projectId, "functions.read");

        var create = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/functions", projectId, apiKey: writeKey,
            body: new { key = "greeter", name = "Greeter", runtime = "node", entrypoint = "index.js", timeoutSeconds = 15 }));
        Assert.Equal(201, (int)create.StatusCode);
        var functionId = (await ReadJson(create)).GetProperty("id").GetString()!;

        var get = await Client.SendAsync(DataPlane(HttpMethod.Get, $"/v1/functions/{functionId}", projectId, apiKey: readKey));
        Assert.Equal(200, (int)get.StatusCode);
        Assert.Equal("Greeter", (await ReadJson(get)).GetProperty("name").GetString());

        var list = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/functions", projectId, apiKey: readKey)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_functions_read_key_cannot_create_and_a_functions_write_key_cannot_list()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, readKey) = await CreateApiKeyAsync(operatorToken, projectId, "functions.read");
        var (_, writeKey) = await CreateApiKeyAsync(operatorToken, projectId, "functions.write");

        var create = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/functions", projectId, apiKey: readKey,
            body: new { key = "greeter", name = "Greeter", runtime = "node", entrypoint = "index.js" }));
        await AssertError(create, 401, ErrorTypes.GeneralUnauthorizedScope);

        var list = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/functions", projectId, apiKey: writeKey));
        await AssertError(list, 401, ErrorTypes.GeneralUnauthorizedScope);
    }

    [Fact]
    public async Task A_functions_write_key_updates_and_deletes_a_function_both_audited_as_a_key_actor()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (keyId, writeKey) = await CreateApiKeyAsync(operatorToken, projectId, "functions.write");
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");

        var update = await Client.SendAsync(DataPlane(HttpMethod.Patch, $"/v1/functions/{functionId}", projectId,
            apiKey: writeKey, body: new { name = "Renamed" }));
        Assert.Equal(200, (int)update.StatusCode);
        Assert.Equal("Renamed", (await ReadJson(update)).GetProperty("name").GetString());

        var delete = await Client.SendAsync(DataPlane(HttpMethod.Delete, $"/v1/functions/{functionId}", projectId, apiKey: writeKey));
        Assert.Equal(204, (int)delete.StatusCode);

        var audit = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/audit?actor={Uri.EscapeDataString($"key:{keyId}")}", operatorToken)));
        var actions = audit.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("functions.update", actions);
        Assert.Contains("functions.delete", actions);
    }

    /// <summary>
    /// Mirrors the console's own distinction (<c>FunctionEndpoints.UpdateFunction</c>): a permission
    /// change gets its own action string, because folding "who may run this" into the same
    /// <c>functions.update</c> as a timeout tweak would make the one security-relevant edit
    /// invisible to anyone reading the log later.
    /// </summary>
    [Fact]
    public async Task Changing_the_execute_role_list_via_a_key_is_audited_distinctly()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (keyId, writeKey) = await CreateApiKeyAsync(operatorToken, projectId, "functions.write");
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");

        var response = await Client.SendAsync(DataPlane(HttpMethod.Patch, $"/v1/functions/{functionId}", projectId,
            apiKey: writeKey, body: new { execute = new[] { "any" } }));
        Assert.Equal(200, (int)response.StatusCode);

        var audit = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/audit?action=functions.execute.update", operatorToken)));
        Assert.Equal(1, audit.GetProperty("total").GetInt32());
        Assert.Equal($"key:{keyId}", audit.GetProperty("entries")[0].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task A_functions_write_key_sets_and_deletes_an_env_var_never_exposing_the_value()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, writeKey) = await CreateApiKeyAsync(operatorToken, projectId, "functions.write");
        var (_, readKey) = await CreateApiKeyAsync(operatorToken, projectId, "functions.read");
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");

        var set = await Client.SendAsync(DataPlane(HttpMethod.Put, $"/v1/functions/{functionId}/env/API_KEY", projectId,
            apiKey: writeKey, body: new { value = "super-secret" }));
        Assert.Equal(200, (int)set.StatusCode);
        Assert.False((await ReadJson(set)).TryGetProperty("value", out _));

        var list = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Get, $"/v1/functions/{functionId}/env", projectId, apiKey: readKey)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.False(list.GetProperty("vars")[0].TryGetProperty("value", out _));

        var delete = await Client.SendAsync(DataPlane(HttpMethod.Delete, $"/v1/functions/{functionId}/env/API_KEY", projectId, apiKey: writeKey));
        Assert.Equal(204, (int)delete.StatusCode);
    }

    [Fact]
    public async Task An_execution_read_key_reads_any_execution_an_execution_write_only_key_cannot()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var executionId = await SeedExecutionAsync(projectId, functionId, "key:someoneelse");

        var (_, readKey) = await CreateApiKeyAsync(operatorToken, projectId, "execution.read");
        var (_, writeOnlyKey) = await CreateApiKeyAsync(operatorToken, projectId, "execution.write");

        var broad = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/functions/{functionId}/executions/{executionId}", projectId, apiKey: readKey));
        Assert.Equal(200, (int)broad.StatusCode);

        var narrow = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/functions/{functionId}/executions/{executionId}", projectId, apiKey: writeOnlyKey));
        await AssertError(narrow, 404, ErrorTypes.FunctionExecutionNotFound);
    }

    [Fact]
    public async Task An_execution_write_key_still_reads_back_its_own_execution_without_execution_read()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (keyId, secret) = await CreateApiKeyAsync(operatorToken, projectId, "execution.write");
        var executionId = await SeedExecutionAsync(projectId, functionId, $"key:{keyId}");

        var response = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/functions/{functionId}/executions/{executionId}", projectId, apiKey: secret));
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task Listing_all_executions_needs_execution_read_not_execution_write()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        await SeedExecutionAsync(projectId, functionId, "key:someoneelse");

        var (_, readKey) = await CreateApiKeyAsync(operatorToken, projectId, "execution.read");
        var (_, writeOnlyKey) = await CreateApiKeyAsync(operatorToken, projectId, "execution.write");

        var list = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/functions/{functionId}/executions", projectId, apiKey: readKey)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        var denied = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/functions/{functionId}/executions", projectId, apiKey: writeOnlyKey));
        await AssertError(denied, 401, ErrorTypes.GeneralUnauthorizedScope);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> CreateFunctionAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new { key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 15 }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    /// <summary>Same seed-directly precedent <c>FunctionExecutionReadTests</c> uses — no endpoint creates an execution row without a real Docker build.</summary>
    private async Task<string> SeedExecutionAsync(string projectId, string functionId, string? triggeredBy)
    {
        var executionId = Ids.NewUuid();
        Ids.TryParseWire(functionId, out var functionGuid);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO praxy.function_executions
                (id, function_id, project_id, trigger, async, status, method, path, status_code,
                 response_body, logs, cold_start, triggered_by, created_at, completed_at)
            VALUES
                ($1, $2, $3, 'http', true, 'completed', 'GET', '/', 200,
                 '{}', '', false, $4, now(), now())
            """, conn);
        cmd.Parameters.AddWithValue(executionId);
        cmd.Parameters.AddWithValue(functionGuid);
        cmd.Parameters.AddWithValue(projectId);
        cmd.Parameters.AddWithValue((object?)triggeredBy ?? DBNull.Value);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());

        return Ids.Wire(executionId);
    }
}
