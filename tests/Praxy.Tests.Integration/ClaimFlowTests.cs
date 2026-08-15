using System.Net.Http.Json;
using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class ClaimFlowTests(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task First_account_claims_then_signup_closes()
    {
        // Fresh instance: unclaimed, no setup token required (no PRAXY_PUBLIC_URL).
        var caps = await ReadJson(await Client.GetAsync("/v1/console/capabilities"));
        Assert.False(caps.GetProperty("claimed").GetBoolean());
        Assert.False(caps.GetProperty("setupTokenRequired").GetBoolean());

        var (token, account) = await ClaimAsync();
        Assert.Equal("owner@praxy.test", account.GetProperty("email").GetString());

        // Claimed now — and the second claim is refused by the API, not just hidden in UI.
        caps = await ReadJson(await Client.GetAsync("/v1/console/capabilities"));
        Assert.True(caps.GetProperty("claimed").GetBoolean());

        var second = await Client.PostAsJsonAsync("/v1/console/claim",
            new { email = "intruder@praxy.test", password = "hunter2hunter2" });
        await AssertError(second, 409, ErrorTypes.InstanceAlreadyClaimed);

        // The claim opened a session usable via header auth.
        var me = await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/account", token));
        var body = await ReadJson(me);
        Assert.Equal("owner@praxy.test", body.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Claim_silently_creates_personal_org_with_owner_membership()
    {
        var (_, account) = await ClaimAsync();
        var userId = Guid.Parse(account.GetProperty("id").GetString()!);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT o.name, m.role
            FROM praxy.organizations o
            JOIN praxy.organization_members m ON m.organization_id = o.id
            WHERE m.user_id = $1
            """, conn);
        cmd.Parameters.AddWithValue(userId);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("Personal", reader.GetString(0));
        Assert.Equal("owner", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Login_logout_lifecycle()
    {
        await ClaimAsync();

        var wrong = await Client.PostAsJsonAsync("/v1/console/sessions",
            new { email = "owner@praxy.test", password = "wrong-password" });
        await AssertError(wrong, 401, ErrorTypes.UserInvalidCredentials);

        var unknown = await Client.PostAsJsonAsync("/v1/console/sessions",
            new { email = "nobody@praxy.test", password = "hunter2hunter2" });
        await AssertError(unknown, 401, ErrorTypes.UserInvalidCredentials);

        var login = await Client.PostAsJsonAsync("/v1/console/sessions",
            new { email = "owner@praxy.test", password = "hunter2hunter2" });
        Assert.Equal(201, (int)login.StatusCode);

        // Browser transport: session cookie set httpOnly.
        var setCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("praxy_session_console="));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);

        var token = (await ReadJson(login)).GetProperty("session").GetProperty("token").GetString()!;

        var logout = await Client.SendAsync(Authed(HttpMethod.Delete, "/v1/console/sessions/current", token));
        Assert.Equal(204, (int)logout.StatusCode);

        // The deleted session no longer authenticates.
        var after = await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/account", token));
        await AssertError(after, 401, ErrorTypes.GeneralUnauthorized);
    }

    [Fact]
    public async Task Claim_validates_email_and_password_with_field_errors()
    {
        var response = await Client.PostAsJsonAsync("/v1/console/claim",
            new { email = "not-an-email", password = "short" });
        var body = await AssertError(response, 400, ErrorTypes.GeneralArgumentInvalid);
        var fields = body.GetProperty("fields");
        Assert.True(fields.TryGetProperty("email", out _));
        Assert.True(fields.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Unauthenticated_console_requests_get_401()
    {
        await ClaimAsync();
        var response = await Client.GetAsync("/v1/console/account");
        await AssertError(response, 401, ErrorTypes.GeneralUnauthorized);
    }
}
