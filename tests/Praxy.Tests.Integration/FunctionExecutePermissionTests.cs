using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The data plane's execute gate. Deliberately Docker-free: the endpoint authorizes before it
/// checks deployment state, so an *authorized* caller on an undeployed function gets
/// <c>function_no_active_deployment</c> — which is exactly the signal that the gate opened, without
/// paying for a container build. <see cref="FunctionTests"/> still proves a granted role invokes a
/// real container end to end.
/// </summary>
public class FunctionExecutePermissionTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    /// <summary>The 401 an unauthorized caller gets, whatever the function's deployment state.</summary>
    private const string Denied = "general_unauthorized";

    /// <summary>Reached only once the execute gate has allowed the caller through.</summary>
    private const string PastTheGate = "function_no_active_deployment";

    [Fact]
    public async Task A_new_function_denies_a_guest()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");

        var response = await Invoke(projectId, functionId);
        await AssertError(response, 401, Denied);
    }

    [Fact]
    public async Task A_new_function_denies_a_signed_in_user()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (session, _) = await SignupAsync(projectId, "member@example.com");

        var response = await Invoke(projectId, functionId, sessionToken: session);
        await AssertError(response, 401, Denied);
    }

    /// <summary>
    /// The regression test for the hole this gate closes: before it existed, a bare project id was
    /// enough to run any enabled function. Granting <c>any</c> is now an explicit, visible choice.
    /// </summary>
    [Fact]
    public async Task Granting_any_lets_a_guest_through()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");

        await GrantAsync(operatorToken, projectId, functionId, "any");

        var response = await Invoke(projectId, functionId);
        await AssertError(response, 400, PastTheGate);
    }

    [Fact]
    public async Task Granting_users_lets_a_session_through_but_still_denies_a_guest()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (session, _) = await SignupAsync(projectId, "member@example.com");

        await GrantAsync(operatorToken, projectId, functionId, "users");

        await AssertError(await Invoke(projectId, functionId, sessionToken: session), 400, PastTheGate);
        await AssertError(await Invoke(projectId, functionId), 401, Denied);
    }

    [Fact]
    public async Task A_specific_user_role_admits_only_that_user()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (mineSession, mine) = await SignupAsync(projectId, "mine@example.com");
        var (othersSession, _) = await SignupAsync(projectId, "theirs@example.com");

        await GrantAsync(operatorToken, projectId, functionId, $"user:{mine.GetProperty("id").GetString()}");

        await AssertError(await Invoke(projectId, functionId, sessionToken: mineSession), 400, PastTheGate);
        await AssertError(await Invoke(projectId, functionId, sessionToken: othersSession), 401, Denied);
    }

    /// <summary>
    /// A key needs its scope AND a role, the same way it needs a <c>databases.*</c> scope AND a table
    /// permission. The scope alone was the old behaviour and must no longer be sufficient.
    /// </summary>
    [Fact]
    public async Task An_api_key_with_the_execute_scope_still_needs_a_granted_role()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "execution.write");

        await AssertError(await Invoke(projectId, functionId, apiKey: apiKey), 401, Denied);

        await GrantAsync(operatorToken, projectId, functionId, "any");
        await AssertError(await Invoke(projectId, functionId, apiKey: apiKey), 400, PastTheGate);
    }

    [Fact]
    public async Task A_key_without_the_execute_scope_is_refused_even_when_the_function_is_public()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read");

        await GrantAsync(operatorToken, projectId, functionId, "any");

        await AssertError(await Invoke(projectId, functionId, apiKey: apiKey), 401, "general_unauthorized_scope");
    }

    /// <summary>
    /// <c>bypassRowPermissions</c> is the documented "trusted server, skip the permission layer"
    /// key flag on rows; it means the same thing here rather than inventing a second escape hatch.
    /// </summary>
    [Fact]
    public async Task A_bypass_key_skips_the_execute_gate()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");

        var created = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/keys", operatorToken,
            new { name = "trusted server", scopes = new[] { "execution.write" }, bypassRowPermissions = true }));
        Assert.Equal(201, (int)created.StatusCode);
        var apiKey = (await ReadJson(created)).GetProperty("secret").GetString()!;

        await AssertError(await Invoke(projectId, functionId, apiKey: apiKey), 400, PastTheGate);
    }

    /// <summary>The operator's escape hatch: a deny-by-default function stays testable from the console.</summary>
    [Fact]
    public async Task The_console_invoke_is_not_gated_on_execute_roles()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");

        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/executions", operatorToken,
            new { method = "GET", path = "/" }));

        // Past the gate, stopped only by the missing deployment — never by a permission.
        await AssertError(response, 400, PastTheGate);
    }

    [Fact]
    public async Task Execute_roles_round_trip_and_reject_a_malformed_role()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "greeter");

        var created = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken)));
        Assert.Empty(created.GetProperty("execute").EnumerateArray());

        var updated = await ReadJson(await GrantAsync(operatorToken, projectId, functionId, "users", "label:vip"));
        Assert.Equal(
            ["users", "label:vip"],
            updated.GetProperty("execute").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var bad = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken,
            new { execute = new[] { "read(\"any\")" } }));
        var error = await AssertError(bad, 400, "general_argument_invalid");
        Assert.True(error.GetProperty("fields").TryGetProperty("execute", out _));
    }

    /// <summary>Creating with an explicit grant works too — deny-by-default is the default, not a wall.</summary>
    [Fact]
    public async Task A_function_can_be_created_with_execute_roles()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new
            {
                key = "public-greeter", name = "Public greeter", runtime = "node", entrypoint = "index.js",
                timeoutSeconds = 15, execute = new[] { "any" },
            }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(["any"], body.GetProperty("execute").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    // ---- helpers ----------------------------------------------------------------------------

    private async Task<string> CreateFunctionAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new { key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 15 }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> GrantAsync(
        string operatorToken, string projectId, string functionId, params string[] roles)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/functions/{functionId}", operatorToken, new { execute = roles }));
        Assert.Equal(200, (int)response.StatusCode);
        return response;
    }

    private Task<HttpResponseMessage> Invoke(
        string projectId, string functionId, string? sessionToken = null, string? apiKey = null) =>
        Client.SendAsync(DataPlane(HttpMethod.Post, $"/v1/functions/{functionId}/executions", projectId,
            sessionToken, apiKey, new { method = "GET", path = "/" }));
}
