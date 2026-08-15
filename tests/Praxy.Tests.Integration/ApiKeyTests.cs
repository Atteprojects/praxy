using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class ApiKeyTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Scoped_key_reads_users_but_the_missing_scope_is_a_401()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await SignupAsync(projectId, "ada@example.com");
        var (_, readOnlyKey) = await CreateApiKeyAsync(operatorToken, projectId, "users.read");

        var list = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/users", projectId, apiKey: readOnlyKey));
        var body = await ReadJson(list);
        Assert.Equal(200, (int)list.StatusCode);
        Assert.Equal(1, body.GetProperty("total").GetInt32());

        // users.write is not on the key — the owner-test "wrong scope → 401".
        var create = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/users", projectId, apiKey: readOnlyKey,
            body: new { email = "new@example.com", password = "correct-horse-battery" }));
        await AssertError(create, 401, "general_unauthorized_scope");

        // Teams API is a different scope family entirely.
        var teams = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/teams", projectId, apiKey: readOnlyKey));
        await AssertError(teams, 401, "general_unauthorized_scope");
    }

    [Fact]
    public async Task Invalid_cross_project_and_revoked_keys_are_hard_401s()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var createB = await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name = "Second" }));
        var projectB = (await ReadJson(createB)).GetProperty("id").GetString()!;

        var (keyId, secret) = await CreateApiKeyAsync(operatorToken, projectId, "users.read");

        var garbage = await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/users", projectId, apiKey: "not-a-real-key"));
        await AssertError(garbage, 401, "general_unauthorized");

        var crossProject = await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/users", projectB, apiKey: secret));
        await AssertError(crossProject, 401, "general_unauthorized");

        var revoke = await Client.SendAsync(Authed(
            HttpMethod.Delete, $"/v1/console/projects/{projectId}/keys/{keyId}", operatorToken));
        Assert.Equal(204, (int)revoke.StatusCode);
        var afterRevoke = await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/users", projectId, apiKey: secret));
        await AssertError(afterRevoke, 401, "general_unauthorized");
    }

    [Fact]
    public async Task Secret_is_revealed_once_and_last_used_is_stamped()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (keyId, secret) = await CreateApiKeyAsync(operatorToken, projectId, "users.read");

        await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/users", projectId, apiKey: secret));

        var list = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/keys", operatorToken)));
        var key = list.GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("id").GetString() == keyId);
        Assert.False(key.TryGetProperty("secret", out _));
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, key.GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task Unknown_scopes_are_refused_at_creation()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/keys", operatorToken,
            new { name = "bad", scopes = new[] { "users.read", "databases.write" } }));
        var body = await AssertError(response, 400, "general_argument_invalid");
        Assert.Contains("databases.write", body.GetProperty("fields").GetProperty("scopes")[0].GetString());
    }

    [Fact]
    public async Task Api_key_roles_are_any_only_and_the_debug_endpoint_says_key()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, key) = await CreateApiKeyAsync(operatorToken, projectId, "users.read");

        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/account/roles", projectId, apiKey: key));
        var body = await ReadJson(response);
        Assert.Equal("key", body.GetProperty("principal").GetString());
        Assert.Equal(new[] { "any" }, body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Contains("users.read",
            body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
    }
}
