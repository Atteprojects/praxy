using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class ConsoleAdminAuthTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Operator_creates_a_user_who_can_then_sign_in()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var create = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/users", operatorToken,
            new { email = "ada@example.com", password = "correct-horse-battery", name = "Ada" }));
        var user = await ReadJson(create);
        Assert.Equal(201, (int)create.StatusCode);

        var list = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/users", operatorToken)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        // The console-created credential works on the data plane — the owner-test flow.
        await LoginAsync(projectId, "ada@example.com");

        var detail = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/users/{user.GetProperty("id").GetString()}",
            operatorToken)));
        Assert.Equal("Ada", detail.GetProperty("user").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Console_revocation_kills_the_users_session_immediately()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (token, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;

        var sessions = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/users/{userId}/sessions", operatorToken)));
        Assert.Equal(1, sessions.GetProperty("total").GetInt32());
        var sessionId = sessions.GetProperty("sessions")[0].GetProperty("id").GetString()!;

        // Warm the 60s cache, revoke from the console, and the very next call is a 401.
        Assert.Equal(200, (int)(await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/account", projectId, token))).StatusCode);
        var revoke = await Client.SendAsync(Authed(
            HttpMethod.Delete, $"/v1/console/projects/{projectId}/users/{userId}/sessions/{sessionId}",
            operatorToken));
        Assert.Equal(204, (int)revoke.StatusCode);

        var after = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, token));
        await AssertError(after, 401, "general_unauthorized");
    }

    [Fact]
    public async Task Labels_flow_into_resolved_roles()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (token, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;

        var patch = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/users/{userId}/labels", operatorToken,
            new { labels = new[] { "vip", "beta" } }));
        Assert.Equal(200, (int)patch.StatusCode);

        // The label change invalidates cached sessions, so roles refresh at once.
        var roles = await RolesAsync(projectId, token);
        Assert.Contains("label:vip", roles);
        Assert.Contains("label:beta", roles);
    }

    [Fact]
    public async Task Teams_screenful_create_add_list_remove()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await SignupAsync(projectId, "ada@example.com");

        var team = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/teams", operatorToken, new { name = "Rocket" })));
        var teamId = team.GetProperty("id").GetString()!;

        var add = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/teams/{teamId}/memberships", operatorToken,
            new { email = "ada@example.com", roles = new[] { "owner" } }));
        var membership = await ReadJson(add);
        Assert.Equal(201, (int)add.StatusCode);
        Assert.True(membership.GetProperty("confirmed").GetBoolean());

        var listed = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/teams", operatorToken)));
        Assert.Equal(1, listed.GetProperty("teams")[0].GetProperty("memberCount").GetInt32());

        var remove = await Client.SendAsync(Authed(
            HttpMethod.Delete,
            $"/v1/console/projects/{projectId}/teams/{teamId}/memberships/{membership.GetProperty("id").GetString()}",
            operatorToken));
        Assert.Equal(204, (int)remove.StatusCode);

        var deleteTeam = await Client.SendAsync(Authed(
            HttpMethod.Delete, $"/v1/console/projects/{projectId}/teams/{teamId}", operatorToken));
        Assert.Equal(204, (int)deleteTeam.StatusCode);
    }

    [Fact]
    public async Task Auth_settings_secret_is_write_only()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var patch = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/auth-settings", operatorToken,
            new { googleEnabled = true, googleClientId = "cid", googleClientSecret = "super-secret" })));
        Assert.True(patch.GetProperty("googleClientSecretSet").GetBoolean());
        Assert.False(patch.TryGetProperty("googleClientSecret", out _));

        var get = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/auth-settings", operatorToken)));
        Assert.True(get.GetProperty("googleClientSecretSet").GetBoolean());
        Assert.False(get.TryGetProperty("googleClientSecret", out _));
    }

    [Fact]
    public async Task Guard_rails_console_project_404s_and_anonymous_401s()
    {
        var (operatorToken, _) = await SetupProjectAsync();

        var reserved = await Client.SendAsync(Authed(
            HttpMethod.Get, "/v1/console/projects/console/users", operatorToken));
        await AssertError(reserved, 404, "project_not_found");

        var unknown = await Client.SendAsync(Authed(
            HttpMethod.Get, "/v1/console/projects/does-not-exist/users", operatorToken));
        await AssertError(unknown, 404, "project_not_found");

        var anonymous = await Client.GetAsync("/v1/console/projects/anything/users");
        await AssertError(anonymous, 401, "general_unauthorized");
    }

    [Fact]
    public async Task Deleting_a_user_cascades_sessions_and_memberships()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (token, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;

        var delete = await Client.SendAsync(Authed(
            HttpMethod.Delete, $"/v1/console/projects/{projectId}/users/{userId}", operatorToken));
        Assert.Equal(204, (int)delete.StatusCode);

        var after = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, token));
        await AssertError(after, 401, "general_unauthorized");

        var list = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/users", operatorToken)));
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }
}
