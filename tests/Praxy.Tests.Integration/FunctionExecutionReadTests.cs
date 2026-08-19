using Npgsql;
using Praxy.Core;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The data plane's own-execution read (<c>GET /v1/functions/{functionId}/executions/{executionId}</c>)
/// — the half of async invocation that lets a caller ever learn what happened. Deliberately
/// Docker-free, same discipline as <see cref="FunctionExecutePermissionTests"/>: execution rows are
/// seeded directly (no endpoint creates one without a real container build, since
/// <c>RequireInvokable</c> refuses an undeployed function before a row is ever written), so what's
/// under test is the read's authorization rule, not the invoke pipeline —
/// <see cref="FunctionTests"/> already proves that end to end.
/// </summary>
public class FunctionExecutionReadTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task The_triggering_user_reads_their_own_execution()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (session, user) = await SignupAsync(projectId, "mine@example.com");
        var executionId = await SeedExecutionAsync(projectId, functionId, $"user:{user.GetProperty("id").GetString()}");

        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId, session));

        Assert.Equal(200, (int)response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(executionId, body.GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_different_signed_in_user_cannot_read_it()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (_, owner) = await SignupAsync(projectId, "mine@example.com");
        var (otherSession, _) = await SignupAsync(projectId, "theirs@example.com");
        var executionId = await SeedExecutionAsync(projectId, functionId, $"user:{owner.GetProperty("id").GetString()}");

        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId, otherSession));

        await AssertError(response, 404, "function_execution_not_found");
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_read_any_execution()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (_, owner) = await SignupAsync(projectId, "mine@example.com");
        var executionId = await SeedExecutionAsync(projectId, functionId, $"user:{owner.GetProperty("id").GetString()}");

        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId));

        await AssertError(response, 404, "function_execution_not_found");
    }

    /// <summary>
    /// A guest-triggered execution really is unrecoverable through this endpoint — there is no "this
    /// guest" to distinguish from any other. Proves the null-identity path, not just the missing-key
    /// path above.
    /// </summary>
    [Fact]
    public async Task A_guest_triggered_execution_is_unrecoverable_by_anyone()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var executionId = await SeedExecutionAsync(projectId, functionId, "guest");
        var (session, _) = await SignupAsync(projectId, "someone@example.com");

        var asGuest = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId));
        await AssertError(asGuest, 404, "function_execution_not_found");

        var asSignedInUser = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId, session));
        await AssertError(asSignedInUser, 404, "function_execution_not_found");
    }

    /// <summary>
    /// The actual regression test for the TriggeredBy fix: before it, every API key recorded the bare
    /// literal "key", so any key holding functions.execute (execution.write today) could read any other key's execution.
    /// </summary>
    [Fact]
    public async Task A_key_reads_its_own_triggered_execution_but_not_a_different_keys()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (ownKeyId, ownSecret) = await CreateApiKeyAsync(operatorToken, projectId, "execution.write");
        var (_, otherSecret) = await CreateApiKeyAsync(operatorToken, projectId, "execution.write");
        var executionId = await SeedExecutionAsync(projectId, functionId, $"key:{ownKeyId}");

        var ownRead = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId, apiKey: ownSecret));
        Assert.Equal(200, (int)ownRead.StatusCode);

        var otherRead = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId, apiKey: otherSecret));
        await AssertError(otherRead, 404, "function_execution_not_found");
    }

    [Fact]
    public async Task A_key_without_the_execute_scope_is_refused_even_for_its_own_execution()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (keyId, secret) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read");
        var executionId = await SeedExecutionAsync(projectId, functionId, $"key:{keyId}");

        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId, apiKey: secret));

        await AssertError(response, 401, "general_unauthorized_scope");
    }

    /// <summary>
    /// The bypass flag's documented reach: a key trusted to invoke anything and skip row permissions
    /// entirely is trusted to read an execution it did not itself trigger.
    /// </summary>
    [Fact]
    public async Task A_bypass_key_reads_an_execution_it_did_not_trigger()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (_, owner) = await SignupAsync(projectId, "mine@example.com");
        var executionId = await SeedExecutionAsync(projectId, functionId, $"user:{owner.GetProperty("id").GetString()}");

        var created = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/keys", operatorToken,
            new { name = "trusted server", scopes = new[] { "execution.write" }, bypassRowPermissions = true }));
        Assert.Equal(201, (int)created.StatusCode);
        var bypassSecret = (await ReadJson(created)).GetProperty("secret").GetString()!;

        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId, apiKey: bypassSecret));

        Assert.Equal(200, (int)response.StatusCode);
    }

    /// <summary>The console admin read is untouched — same execution, same response shape, either route.</summary>
    [Fact]
    public async Task The_console_and_data_plane_reads_return_the_same_shape()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (session, owner) = await SignupAsync(projectId, "mine@example.com");
        var ownerId = owner.GetProperty("id").GetString();
        var executionId = await SeedExecutionAsync(projectId, functionId, $"user:{ownerId}");

        var consoleRead = await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}/executions/{executionId}", operatorToken));
        Assert.Equal(200, (int)consoleRead.StatusCode);
        var consoleBody = await ReadJson(consoleRead);

        var dataPlaneRead = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/functions/{functionId}/executions/{executionId}", projectId, session));
        Assert.Equal(200, (int)dataPlaneRead.StatusCode);
        var dataPlaneBody = await ReadJson(dataPlaneRead);

        Assert.Equal(consoleBody.GetProperty("id").GetString(), dataPlaneBody.GetProperty("id").GetString());
        Assert.Equal(consoleBody.GetProperty("triggeredBy").GetString(), $"user:{ownerId}");
        Assert.Equal(consoleBody.GetProperty("triggeredBy").GetString(), dataPlaneBody.GetProperty("triggeredBy").GetString());
        Assert.Equal(consoleBody.GetProperty("status").GetString(), dataPlaneBody.GetProperty("status").GetString());
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

    /// <summary>
    /// Seeds a completed execution row directly. Nothing in the public API creates an execution row
    /// for an undeployed function — <c>RequireInvokable</c> refuses before <c>CreateExecutionAsync</c>
    /// is ever called — and a real deployment needs a Docker build, which this file deliberately
    /// avoids (<see cref="FunctionTests"/> owns that coverage). Mirrors
    /// <c>ApiTestBase.CreateSecondOperatorAsync</c>'s "seed directly, no endpoint exists for this
    /// shape" precedent.
    /// </summary>
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
