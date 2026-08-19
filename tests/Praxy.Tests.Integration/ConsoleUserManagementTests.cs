using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The operator's user-management surface: change an address, rename, set a password, and settle
/// verified-ness. The motivating case is a user who mistyped their email at signup — unable to
/// verify, unable to recover, and until this existed only deletable.
/// </summary>
public class ConsoleUserManagementTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private static string Users(string projectId) => $"/v1/console/projects/{projectId}/users";

    [Fact]
    public async Task Changing_an_email_moves_the_login_and_resets_verified()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (session, user) = await SignupAsync(projectId, "typo@example.com");
        var userId = user.GetProperty("id").GetString()!;

        // Start from verified, so the reset is visible rather than a coincidence of the default.
        var verify = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/verification", operatorToken,
            new { emailVerified = true }));
        Assert.Equal(200, (int)verify.StatusCode);

        var patch = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/email", operatorToken,
            new { email = "Ada@Example.com " })));
        Assert.Equal("ada@example.com", patch.GetProperty("email").GetString());
        Assert.False(patch.GetProperty("emailVerified").GetBoolean());

        // The new address is the login; the old one is gone.
        await LoginAsync(projectId, "ada@example.com");
        var old = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/account/sessions/email", projectId,
            body: new { email = "typo@example.com", password = "correct-horse-battery" }));
        await AssertError(old, 401, ErrorTypes.UserInvalidCredentials);

        // An email change is not a session revocation — the user stays signed in, but the
        // verified role they had is gone from the very next request.
        Assert.DoesNotContain("users/verified", await RolesAsync(projectId, session));
    }

    /// <summary>Re-submitting the address someone already has must not silently un-verify them.</summary>
    [Fact]
    public async Task Setting_the_same_email_leaves_verified_alone()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;

        await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/verification", operatorToken,
            new { emailVerified = true }));

        var patch = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/email", operatorToken,
            new { email = "ADA@example.com" })));
        Assert.True(patch.GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task Changing_to_an_address_already_in_the_project_is_a_conflict()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, ada) = await SignupAsync(projectId, "ada@example.com");
        await SignupAsync(projectId, "grace@example.com");

        var patch = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{ada.GetProperty("id").GetString()}/email", operatorToken,
            new { email = "grace@example.com" }));
        await AssertError(patch, 409, ErrorTypes.UserAlreadyExists);

        // The rejected write left nothing behind: the original address still logs in.
        await LoginAsync(projectId, "ada@example.com");
    }

    [Fact]
    public async Task An_invalid_address_is_a_field_error()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, user) = await SignupAsync(projectId, "ada@example.com");

        var patch = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{user.GetProperty("id").GetString()}/email", operatorToken,
            new { email = "not-an-address" }));
        var body = await AssertError(patch, 400, ErrorTypes.GeneralArgumentInvalid);
        Assert.True(body.GetProperty("fields").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Renaming_a_user_sticks()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, user) = await SignupAsync(projectId, "ada@example.com", name: "Ada");
        var userId = user.GetProperty("id").GetString()!;

        var patch = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/name", operatorToken,
            new { name = "Ada Lovelace" })));
        Assert.Equal("Ada Lovelace", patch.GetProperty("name").GetString());

        var detail = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"{Users(projectId)}/{userId}", operatorToken)));
        Assert.Equal("Ada Lovelace", detail.GetProperty("user").GetProperty("name").GetString());
    }

    /// <summary>
    /// The decision this endpoint encodes: an operator reset revokes every live session. An
    /// operator resets because the account is locked out or compromised, and in the second reading
    /// the sessions are exactly what an attacker is holding.
    /// </summary>
    [Fact]
    public async Task Operator_set_password_replaces_the_old_one_and_revokes_every_session()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (session, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;

        // Warm the session cache so revocation has to beat it, not wait it out.
        Assert.Equal(200, (int)(await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/account", projectId, session))).StatusCode);

        var patch = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/password", operatorToken,
            new { password = "brand-new-secret" }));
        Assert.Equal(200, (int)patch.StatusCode);

        await AssertError(
            await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, session)),
            401, ErrorTypes.GeneralUnauthorized);
        var sessions = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"{Users(projectId)}/{userId}/sessions", operatorToken)));
        Assert.Equal(0, sessions.GetProperty("total").GetInt32());

        await LoginAsync(projectId, "ada@example.com", "brand-new-secret");
        var old = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/account/sessions/email", projectId,
            body: new { email = "ada@example.com", password = "correct-horse-battery" }));
        await AssertError(old, 401, ErrorTypes.UserInvalidCredentials);
    }

    /// <summary>An operator has no old password to give, but the project's policy still applies.</summary>
    [Fact]
    public async Task Operator_set_password_still_honours_the_projects_minimum_length()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, user) = await SignupAsync(projectId, "ada@example.com");

        await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/auth-settings", operatorToken,
            new { passwordMinLength = 16 }));

        var patch = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{user.GetProperty("id").GetString()}/password", operatorToken,
            new { password = "short" }));
        var body = await AssertError(patch, 400, ErrorTypes.GeneralArgumentInvalid);
        Assert.True(body.GetProperty("fields").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Marking_verified_grants_the_verified_role_and_is_audited()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (session, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;

        Assert.DoesNotContain("users/verified", await RolesAsync(projectId, session));

        var patch = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/verification", operatorToken,
            new { emailVerified = true })));
        Assert.True(patch.GetProperty("emailVerified").GetBoolean());

        // Verified-ness is a permission role, so it has to reach the resolver immediately.
        Assert.Contains("users/verified", await RolesAsync(projectId, session));
        Assert.Contains($"user:{userId}/verified", await RolesAsync(projectId, session));

        Assert.Contains($"users.verification.grant|user/{userId}", await AuditRowsAsync());

        var revoke = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/verification", operatorToken,
            new { emailVerified = false })));
        Assert.False(revoke.GetProperty("emailVerified").GetBoolean());
        Assert.DoesNotContain("users/verified", await RolesAsync(projectId, session));
        Assert.Contains($"users.verification.revoke|user/{userId}", await AuditRowsAsync());
    }

    /// <summary>
    /// Security-relevant changes get their own audit action rather than folding into a generic
    /// one — the precedent <c>functions.execute.update</c> set.
    /// </summary>
    [Fact]
    public async Task Email_and_password_changes_get_their_own_audit_actions()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, user) = await SignupAsync(projectId, "typo@example.com");
        var userId = user.GetProperty("id").GetString()!;

        await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/email", operatorToken,
            new { email = "ada@example.com" }));
        await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/password", operatorToken,
            new { password = "brand-new-secret" }));
        await Client.SendAsync(Authed(
            HttpMethod.Patch, $"{Users(projectId)}/{userId}/name", operatorToken, new { name = "Ada" }));

        var rows = await AuditRowsAsync();
        Assert.Contains($"users.email.update|user/{userId}", rows);
        Assert.Contains($"users.password.reset|user/{userId}", rows);
        Assert.Contains($"users.name.update|user/{userId}", rows);
    }

    /// <summary>
    /// The resend takes the redirect URL as a request field: only the caller knows where the app
    /// handles verification, and the platform allowlist is what keeps that from minting a
    /// phishing link.
    /// </summary>
    [Fact]
    public async Task Resending_verification_mails_an_allowlisted_url_and_refuses_anything_else()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var (_, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;

        var offAllowlist = await Client.SendAsync(Authed(
            HttpMethod.Post, $"{Users(projectId)}/{userId}/verification", operatorToken,
            new { url = "https://evil.example.net/verify" }));
        var body = await AssertError(offAllowlist, 400, ErrorTypes.GeneralArgumentInvalid);
        Assert.True(body.GetProperty("fields").TryGetProperty("url", out _));
        Assert.True(Email.Sent.IsEmpty);

        var send = await Client.SendAsync(Authed(
            HttpMethod.Post, $"{Users(projectId)}/{userId}/verification", operatorToken,
            new { url = "https://app.example.com/verify" }));
        Assert.Equal(204, (int)send.StatusCode);
        Assert.Contains($"users.verification.send|user/{userId}", await AuditRowsAsync());

        // The mailed link completes the real flow, which is the whole point of resending it.
        var link = Email.LastLinkParams();
        Assert.Equal(userId, link["userId"]);
        var confirm = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Put, "/v1/account/verification", projectId,
            body: new { userId = link["userId"], secret = link["secret"] })));
        Assert.True(confirm.GetProperty("emailVerified").GetBoolean());

        // Already verified: nothing left to send.
        var again = await Client.SendAsync(Authed(
            HttpMethod.Post, $"{Users(projectId)}/{userId}/verification", operatorToken,
            new { url = "https://app.example.com/verify" }));
        await AssertError(again, 400, ErrorTypes.GeneralArgumentInvalid);
    }

    /// <summary>
    /// The <c>/v1/users</c> mirror. It exists because the server API already mirrors
    /// status/labels/sessions, so a backend script automating user administration would otherwise
    /// hit exactly the wall this feature just removed from the console.
    /// </summary>
    [Fact]
    public async Task The_server_api_mirrors_the_same_four_changes()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (session, user) = await SignupAsync(projectId, "typo@example.com");
        var userId = user.GetProperty("id").GetString()!;
        var (_, key) = await CreateApiKeyAsync(operatorToken, projectId, "users.write");

        var email = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/users/{userId}/email", projectId, apiKey: key,
            body: new { email = "ada@example.com" })));
        Assert.Equal("ada@example.com", email.GetProperty("email").GetString());
        Assert.False(email.GetProperty("emailVerified").GetBoolean());

        var named = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/users/{userId}/name", projectId, apiKey: key,
            body: new { name = "Ada Lovelace" })));
        Assert.Equal("Ada Lovelace", named.GetProperty("name").GetString());

        var verified = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/users/{userId}/verification", projectId, apiKey: key,
            body: new { emailVerified = true })));
        Assert.True(verified.GetProperty("emailVerified").GetBoolean());

        // Same revoke-everything stance as the console — one implementation, one behaviour.
        var password = await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/users/{userId}/password", projectId, apiKey: key,
            body: new { password = "brand-new-secret" }));
        Assert.Equal(200, (int)password.StatusCode);
        await AssertError(
            await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, session)),
            401, ErrorTypes.GeneralUnauthorized);
        await LoginAsync(projectId, "ada@example.com", "brand-new-secret");
    }

    [Fact]
    public async Task The_server_api_requires_users_write_for_all_four()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;
        var (_, readOnlyKey) = await CreateApiKeyAsync(operatorToken, projectId, "users.read");

        foreach (var (method, path, payload) in Surface(userId))
        {
            // The console-only resend has no server counterpart — see the summary's reasoning.
            if (method == HttpMethod.Post)
                continue;
            var response = await Client.SendAsync(DataPlane(
                method, $"/v1/users{path}", projectId, apiKey: readOnlyKey, body: payload));
            await AssertError(response, 401, ErrorTypes.GeneralUnauthorizedScope);
        }
    }

    /// <summary>The <see cref="Praxy.Api.Infrastructure.ConsoleProjectFilter"/> boundary, per route.</summary>
    [Fact]
    public async Task Another_operator_cannot_reach_this_projects_user()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;
        var (otherToken, _) = await CreateSecondOperatorAsync();

        foreach (var (method, path, payload) in Surface(userId))
        {
            var response = await Client.SendAsync(Authed(method, $"{Users(projectId)}{path}", otherToken, payload));
            await AssertError(response, 404, ErrorTypes.ProjectNotFound);
        }

        // Untouched: the original operator still sees the original address.
        var detail = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"{Users(projectId)}/{userId}", operatorToken)));
        Assert.Equal("ada@example.com", detail.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task The_reserved_console_project_refuses_the_whole_surface()
    {
        var (operatorToken, _) = await SetupProjectAsync();
        var userId = Praxy.Core.Ids.Wire(Guid.CreateVersion7());

        foreach (var (method, path, payload) in Surface(userId))
        {
            var response = await Client.SendAsync(Authed(method, $"{Users("console")}{path}", operatorToken, payload));
            await AssertError(response, 404, ErrorTypes.ProjectNotFound);
        }
    }

    /// <summary>Every route this feature adds, with a payload that would succeed if it got through.</summary>
    private static IEnumerable<(HttpMethod Method, string Path, object Payload)> Surface(string userId) =>
    [
        (HttpMethod.Patch, $"/{userId}/email", new { email = "taken@example.com" }),
        (HttpMethod.Patch, $"/{userId}/name", new { name = "Renamed" }),
        (HttpMethod.Patch, $"/{userId}/password", new { password = "brand-new-secret" }),
        (HttpMethod.Patch, $"/{userId}/verification", new { emailVerified = true }),
        (HttpMethod.Post, $"/{userId}/verification", new { url = "https://app.example.com/verify" }),
    ];

    /// <summary>Audit rows as <c>action|resource</c> — the log has no read surface yet (gap #3).</summary>
    private async Task<List<string>> AuditRowsAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT action, resource FROM praxy.audit_log", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}");
        return rows;
    }
}
