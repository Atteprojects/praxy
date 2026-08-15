using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class AppAuthTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Signup_auto_creates_a_session_and_login_logout_roundtrips()
    {
        var (_, projectId) = await SetupProjectAsync();

        // Signup returns a live session (roadmap: session auto-created on signup).
        var (token, user) = await SignupAsync(projectId, "ada@example.com");
        Assert.Equal("ada@example.com", user.GetProperty("email").GetString());
        Assert.False(user.GetProperty("emailVerified").GetBoolean());

        var account = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, token));
        Assert.Equal(200, (int)account.StatusCode);

        // Logout, then the token is dead — instantly, despite the 60s session cache.
        var logout = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, "/v1/account/sessions/current", projectId, token));
        Assert.Equal(204, (int)logout.StatusCode);
        var after = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, token));
        await AssertError(after, 401, "general_unauthorized");

        // Login works and issues a fresh session.
        var newToken = await LoginAsync(projectId, "ada@example.com");
        var again = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, newToken));
        Assert.Equal(200, (int)again.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_duplicate_signup_fail_with_stable_types()
    {
        var (_, projectId) = await SetupProjectAsync();
        await SignupAsync(projectId, "ada@example.com");

        var bad = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/sessions/email", projectId,
            body: new { email = "ada@example.com", password = "wrong-password-here" }));
        await AssertError(bad, 401, "user_invalid_credentials");

        var missing = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/sessions/email", projectId,
            body: new { email = "nobody@example.com", password = "whatever-password" }));
        await AssertError(missing, 401, "user_invalid_credentials");

        var duplicate = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account", projectId,
            body: new { email = "ada@example.com", password = "another-password", name = "Dup" }));
        await AssertError(duplicate, 409, "user_already_exists");
    }

    [Fact]
    public async Task Sessions_are_project_scoped()
    {
        var (operatorToken, projectA) = await SetupProjectAsync();
        var createB = await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name = "Second" }));
        var projectB = (await ReadJson(createB)).GetProperty("id").GetString()!;

        var (token, _) = await SignupAsync(projectA, "ada@example.com");
        var crossProject = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectB, token));
        await AssertError(crossProject, 401, "general_unauthorized");
    }

    [Fact]
    public async Task Eleventh_session_evicts_the_first()
    {
        var (_, projectId) = await SetupProjectAsync();
        var (first, _) = await SignupAsync(projectId, "ada@example.com");

        var tokens = new List<string> { first };
        for (var i = 0; i < 10; i++)
            tokens.Add(await LoginAsync(projectId, "ada@example.com"));

        // 11 sessions were created against a cap of 10: the signup session is gone…
        var evicted = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, tokens[0]));
        await AssertError(evicted, 401, "general_unauthorized");

        // …the newest still works, and exactly 10 remain.
        var list = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account/sessions", projectId, tokens[^1]));
        var body = await ReadJson(list);
        Assert.Equal(200, (int)list.StatusCode);
        Assert.Equal(10, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Session_list_marks_current_and_revoking_another_session_kills_it_immediately()
    {
        var (_, projectId) = await SetupProjectAsync();
        var (tokenA, _) = await SignupAsync(projectId, "ada@example.com");
        var tokenB = await LoginAsync(projectId, "ada@example.com");

        var list = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/account/sessions", projectId, tokenA)));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var sessions = list.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Single(sessions, s => s.GetProperty("current").GetBoolean());
        var otherId = sessions.First(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;

        // Warm the cache for token B, then revoke it from session A.
        Assert.Equal(200, (int)(await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/account", projectId, tokenB))).StatusCode);
        var revoke = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/account/sessions/{otherId}", projectId, tokenA));
        Assert.Equal(204, (int)revoke.StatusCode);

        // The sessions.delete event invalidated the cache — B dies now, not in 60 seconds.
        var after = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, tokenB));
        await AssertError(after, 401, "general_unauthorized");
    }

    [Fact]
    public async Task Blocked_user_cannot_login_and_live_sessions_die()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (token, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;

        var block = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/users/{userId}/status", operatorToken,
            new { status = false }));
        Assert.Equal(200, (int)block.StatusCode);

        var login = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/sessions/email", projectId,
            body: new { email = "ada@example.com", password = "correct-horse-battery" }));
        await AssertError(login, 401, "user_blocked");

        var existing = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, token));
        await AssertError(existing, 401, "general_unauthorized");
    }

    [Fact]
    public async Task Disabling_email_password_turns_signup_and_login_off()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var patch = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/auth-settings", operatorToken,
            new { emailPassword = false }));
        Assert.Equal(200, (int)patch.StatusCode);

        var signup = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account", projectId,
            body: new { email = "ada@example.com", password = "correct-horse-battery" }));
        await AssertError(signup, 400, "project_auth_method_disabled");
    }

    [Fact]
    public async Task Password_policy_from_settings_is_enforced()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/auth-settings", operatorToken,
            new { passwordMinLength = 12 }));

        var tooShort = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account", projectId,
            body: new { email = "ada@example.com", password = "elevenchars" }));
        var body = await AssertError(tooShort, 400, "general_argument_invalid");
        Assert.Contains("12", body.GetProperty("fields").GetProperty("password")[0].GetString());
    }

    [Fact]
    public async Task Console_project_is_unreachable_from_the_app_auth_surface()
    {
        await ClaimAsync();
        var signup = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account", "console",
            body: new { email = "x@example.com", password = "correct-horse-battery" }));
        await AssertError(signup, 403, "project_reserved");
    }
}
