using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Praxy.Auth.OAuth;
using Praxy.Persistence;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Operator Google sign-in — claim, login, and organization-invite accept, all through
/// <see cref="ConsoleOAuthService"/>. Fresh instance per test class (<see cref="ApiTestBase"/>), so
/// the claim-via-Google tests don't need a second factory the way <see cref="OAuthFlowTests"/>
/// (app users, always inside an already-claimed project) doesn't have to worry about.
/// </summary>
public class ConsoleOAuthFlowTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private readonly FakeOAuthProvider _provider = new();

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(base.ExtraSettings!)
    {
        ["Praxy:ConsoleAuth:Google:ClientId"] = "console-cid",
        ["Praxy:ConsoleAuth:Google:ClientSecret"] = "console-csecret",
    };

    protected override Action<IServiceCollection>? TestServices => services =>
    {
        base.TestServices!(services);
        services.RemoveAll<IOAuthProvider>();
        services.AddSingleton<IOAuthProvider>(_provider);
    };

    private static Dictionary<string, string> QueryOf(string url) =>
        new Uri(url, UriKind.RelativeOrAbsolute) is { IsAbsoluteUri: true } abs
            ? SplitQuery(abs.Query)
            : SplitQuery(new Uri("http://x" + url).Query);

    private static Dictionary<string, string> SplitQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));

    private async Task<(HttpResponseMessage Response, string? SessionToken)> RunOAuthRoundTripAsync(string startQuery, string code = "good-code")
    {
        var start = await Client.GetAsync($"/v1/console/sessions/oauth2/google{startQuery}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = QueryOf(start.Headers.Location!.ToString());
        var stateCookie = start.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("praxy_console_oauth=", StringComparison.Ordinal)).Split(';')[0];

        var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/console/sessions/oauth2/callback/google?code={code}&state={authorize["state"]}");
        callback.Headers.Add("Cookie", stateCookie);
        var response = await Client.SendAsync(callback);

        var sessionCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(c => c.StartsWith("praxy_session_console=", StringComparison.Ordinal))
            : null;
        var token = sessionCookie?.Split(';')[0].Split('=', 2)[1];
        return (response, token);
    }

    [Fact]
    public async Task Claiming_the_instance_via_google_creates_the_operator_links_identity_and_creates_personal()
    {
        _provider.Profile = new("google-uid-claim", "ada@example.com", EmailVerified: true, "Ada Lovelace");

        var (response, token) = await RunOAuthRoundTripAsync("");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.False(string.IsNullOrEmpty(token));

        var account = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/account", token!)));
        Assert.Equal("ada@example.com", account.GetProperty("email").GetString());

        var orgs = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/organizations", token!)));
        Assert.Equal(1, orgs.GetProperty("total").GetInt32());
        var org = orgs.GetProperty("organizations")[0];
        Assert.Equal("Personal", org.GetProperty("name").GetString());
        Assert.Equal("owner", org.GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_second_claim_attempt_via_google_is_refused_once_the_instance_is_claimed()
    {
        _provider.Profile = new("google-uid-first", "first@example.com", EmailVerified: true, "First Owner");
        var (first, _) = await RunOAuthRoundTripAsync("");
        Assert.Equal("/", first.Headers.Location!.ToString());

        // A second, wholly unrelated Google identity attempting the same door lands on the
        // ordinary-login branch now (the instance is claimed) — never a second claim/Personal org.
        _provider.Profile = new("google-uid-second", "second@example.com", EmailVerified: true, "Second Person");
        var (second, secondToken) = await RunOAuthRoundTripAsync("");
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith("/login?oauthError=console_oauth_account_not_found", second.Headers.Location!.ToString());
        Assert.Null(secondToken);

        // The password door is refused too — the "closed forever after" property doesn't get a
        // second door, and neither Google attempt above created a second Personal organization.
        var passwordClaim = await Client.PostAsJsonAsync("/v1/console/claim",
            new { email = "someone-else@praxy.test", password = "hunter2hunter2" });
        await AssertError(passwordClaim, 409, Praxy.Core.Errors.ErrorTypes.InstanceAlreadyClaimed);

        using var scope = OpenScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        Assert.Equal(1, await db.Organizations.CountAsync());
    }

    [Fact]
    public async Task Signing_in_via_google_resolves_to_the_same_operator_a_password_login_does()
    {
        var (operatorToken, _) = await ClaimAsync(email: "owner@praxy.test", password: "hunter2hunter2");
        var account = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/account", operatorToken)));
        var accountId = account.GetProperty("id").GetString();

        _provider.Profile = new("google-uid-owner", "owner@praxy.test", EmailVerified: true, "Owner");
        var (response, googleToken) = await RunOAuthRoundTripAsync("");
        Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.False(string.IsNullOrEmpty(googleToken));

        var googleAccount = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/account", googleToken!)));
        Assert.Equal(accountId, googleAccount.GetProperty("id").GetString());

        // No duplicate operator or identity: exactly one user row, one identity row.
        using var scope = OpenScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        Assert.Equal(1, await db.Users.CountAsync(u => u.ProjectId == "console"));
        Assert.Equal(1, await db.Identities.CountAsync(i => i.ProjectId == "console" && i.Provider == "google"));
    }

    [Fact]
    public async Task Google_login_reuses_the_linked_identity_on_a_later_attempt_instead_of_relinking()
    {
        _provider.Profile = new("google-uid-repeat", "ada@example.com", EmailVerified: true, "Ada");
        await RunOAuthRoundTripAsync(""); // claim
        var (second, token) = await RunOAuthRoundTripAsync(""); // ordinary login, same uid
        Assert.Equal("/", second.Headers.Location!.ToString());
        Assert.False(string.IsNullOrEmpty(token));

        using var scope = OpenScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        Assert.Equal(1, await db.Users.CountAsync(u => u.ProjectId == "console"));
        Assert.Equal(1, await db.Identities.CountAsync(i => i.ProjectId == "console"));
    }

    [Fact]
    public async Task Unverified_google_email_does_not_silently_link_to_a_different_existing_account()
    {
        await ClaimAsync(email: "owner@praxy.test", password: "hunter2hunter2");

        _provider.Profile = new("google-uid-imposter", "owner@praxy.test", EmailVerified: false, "Imposter");
        var (response, token) = await RunOAuthRoundTripAsync("");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login?oauthError=user_oauth2_provider_error", response.Headers.Location!.ToString());
        Assert.Null(token);

        using var scope = OpenScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        Assert.Equal(0, await db.Identities.CountAsync(i => i.ProjectId == "console"));
    }

    [Fact]
    public async Task An_unaccepted_invite_does_not_let_a_plain_google_login_stand_in_for_the_accept_door()
    {
        var (operatorToken, organizationId) = await ClaimAndOrgAsync();
        await InviteAsync(operatorToken, organizationId, "colleague@test.local");

        _provider.Profile = new("google-uid-colleague", "colleague@test.local", EmailVerified: true, "Colleague");
        var (response, token) = await RunOAuthRoundTripAsync("");
        Assert.StartsWith("/login?oauthError=console_oauth_account_not_found", response.Headers.Location!.ToString());
        Assert.Null(token);
    }

    [Fact]
    public async Task Accepting_an_organization_invite_via_google_links_identity_without_a_password_and_confirms_once()
    {
        var (operatorToken, organizationId) = await ClaimAndOrgAsync();
        var link = await InviteAsync(operatorToken, organizationId, "colleague@test.local");

        _provider.Profile = new("google-uid-colleague", "colleague@test.local", EmailVerified: true, "Colleague");
        var query = $"?organizationId={link["organizationId"]}&userId={link["userId"]}&secret={Uri.EscapeDataString(link["secret"])}";
        var (response, token) = await RunOAuthRoundTripAsync(query);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.False(string.IsNullOrEmpty(token));

        var members = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{organizationId}/members", operatorToken)));
        var colleague = members.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "colleague@test.local");
        Assert.True(colleague.GetProperty("confirmed").GetBoolean());

        // No password was ever set on the accepted account.
        using var scope = OpenScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var user = await db.Users.SingleAsync(u => u.ProjectId == "console" && u.Email == "colleague@test.local");
        Assert.Null(user.PasswordHash);

        // Accepting a second time (the same invite door) is rejected, not silently reconfirmed.
        var (replay, replayToken) = await RunOAuthRoundTripAsync(query);
        Assert.StartsWith(
            "/accept-invite?organizationId=", replay.Headers.Location!.ToString());
        Assert.Contains("oauthError=organization_invite_already_accepted", replay.Headers.Location!.ToString());
        Assert.Null(replayToken);
    }

    [Fact]
    public async Task Accepting_an_invite_via_google_refuses_a_mismatched_email()
    {
        var (operatorToken, organizationId) = await ClaimAndOrgAsync();
        var link = await InviteAsync(operatorToken, organizationId, "colleague@test.local");

        _provider.Profile = new("google-uid-someone-else", "someone-else@example.com", EmailVerified: true, "Someone Else");
        var query = $"?organizationId={link["organizationId"]}&userId={link["userId"]}&secret={Uri.EscapeDataString(link["secret"])}";
        var (response, token) = await RunOAuthRoundTripAsync(query);
        Assert.Contains("oauthError=console_oauth_email_mismatch", response.Headers.Location!.ToString());
        Assert.Null(token);
    }

    private async Task<(string OperatorToken, string OrganizationId)> ClaimAndOrgAsync()
    {
        var (operatorToken, _) = await ClaimAsync(email: "owner@praxy.test", password: "hunter2hunter2");
        var orgs = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/organizations", operatorToken)));
        var organizationId = orgs.GetProperty("organizations")[0].GetProperty("id").GetString()!;
        return (operatorToken, organizationId);
    }

    private async Task<Dictionary<string, string>> InviteAsync(string operatorToken, string organizationId, string email)
    {
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/organizations/{organizationId}/members", operatorToken,
            new { email, role = "member", url = "https://console.example.com/accept-invite" }));
        Assert.Equal(201, (int)response.StatusCode);
        return Email.LastLinkParams();
    }

    private IServiceScope OpenScope() => Factory.Services.CreateScope();
}
