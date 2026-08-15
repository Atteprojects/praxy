using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Praxy.Auth.OAuth;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class OAuthFlowTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private readonly FakeOAuthProvider _provider = new();

    protected override Action<IServiceCollection>? TestServices => services =>
    {
        services.Replace(ServiceDescriptor.Singleton<Praxy.Auth.IEmailSender>(Email));
        services.RemoveAll<IOAuthProvider>();
        services.AddSingleton<IOAuthProvider>(_provider);
    };

    private async Task<(string ProjectId, string OperatorToken)> SetupOAuthProjectAsync()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var patch = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/auth-settings", operatorToken,
            new { githubEnabled = true, githubClientId = "cid", githubClientSecret = "csecret" }));
        Assert.Equal(200, (int)patch.StatusCode);
        return (projectId, operatorToken);
    }

    private static Dictionary<string, string> QueryOf(string url) =>
        new Uri(url).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task Full_token_flow_signs_the_user_in_via_the_exchange_endpoint()
    {
        var (projectId, _) = await SetupOAuthProjectAsync();

        // Start: 302 to the provider with state + PKCE, state cookie set.
        var start = await Client.GetAsync(
            $"/v1/account/sessions/oauth2/github?project={projectId}" +
            "&success=https%3A%2F%2Fapp.example.com%2Fok&failure=https%3A%2F%2Fapp.example.com%2Ffail");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = QueryOf(start.Headers.Location!.ToString());
        Assert.Equal("cid", authorize["client_id"]);
        Assert.NotEmpty(authorize["code_challenge"]);
        var stateCookie = start.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith($"praxy_oauth_{projectId}=", StringComparison.Ordinal))
            .Split(';')[0];

        // Callback: provider redirects back with the code; we land on success with userId + wrapped secret.
        var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/account/sessions/oauth2/callback/github/{projectId}?code=good-code&state={authorize["state"]}");
        callback.Headers.Add("Cookie", stateCookie);
        var callbackResponse = await Client.SendAsync(callback);
        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        var location = callbackResponse.Headers.Location!.ToString();
        Assert.StartsWith("https://app.example.com/ok", location);
        var result = QueryOf(location);

        // PKCE verifier made it to the provider's token exchange.
        Assert.NotNull(_provider.LastCodeVerifier);
        // The secret riding the redirect is a JWT wrap, not the raw token.
        Assert.Equal(2, result["secret"].Count(c => c == '.'));

        // Exchange at the universal converging point → session.
        var exchange = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/account/sessions/token", projectId,
            body: new { userId = result["userId"], secret = result["secret"] }));
        var session = await ReadJson(exchange);
        Assert.Equal(201, (int)exchange.StatusCode);
        Assert.Equal("github", session.GetProperty("session").GetProperty("provider").GetString());
        Assert.Equal("octo@example.com", session.GetProperty("user").GetProperty("email").GetString());
        Assert.True(session.GetProperty("user").GetProperty("emailVerified").GetBoolean());

        // The one-time token is consumed — replay fails.
        var replay = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/account/sessions/token", projectId,
            body: new { userId = result["userId"], secret = result["secret"] }));
        await AssertError(replay, 401, "user_invalid_token");
    }

    [Fact]
    public async Task Second_login_reuses_the_identity_instead_of_creating_a_user()
    {
        var (projectId, operatorToken) = await SetupOAuthProjectAsync();

        for (var i = 0; i < 2; i++)
        {
            var start = await Client.GetAsync(
                $"/v1/account/sessions/oauth2/github?project={projectId}" +
                "&success=https%3A%2F%2Fapp.example.com%2Fok&failure=https%3A%2F%2Fapp.example.com%2Ffail");
            var authorize = QueryOf(start.Headers.Location!.ToString());
            var stateCookie = start.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith($"praxy_oauth_{projectId}=", StringComparison.Ordinal)).Split(';')[0];
            var callback = new HttpRequestMessage(HttpMethod.Get,
                $"/v1/account/sessions/oauth2/callback/github/{projectId}?code=good-code&state={authorize["state"]}");
            callback.Headers.Add("Cookie", stateCookie);
            await Client.SendAsync(callback);
        }

        var users = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/users", operatorToken)));
        Assert.Equal(1, users.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Provider_error_lands_on_the_failure_url()
    {
        var (projectId, _) = await SetupOAuthProjectAsync();
        var start = await Client.GetAsync(
            $"/v1/account/sessions/oauth2/github?project={projectId}" +
            "&success=https%3A%2F%2Fapp.example.com%2Fok&failure=https%3A%2F%2Fapp.example.com%2Ffail");
        var authorize = QueryOf(start.Headers.Location!.ToString());
        var stateCookie = start.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith($"praxy_oauth_{projectId}=", StringComparison.Ordinal)).Split(';')[0];

        var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/account/sessions/oauth2/callback/github/{projectId}?code=bad-code&state={authorize["state"]}");
        callback.Headers.Add("Cookie", stateCookie);
        var response = await Client.SendAsync(callback);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://app.example.com/fail", location);
        Assert.Equal("user_oauth2_provider_error", QueryOf(location)["error"]);
    }

    [Fact]
    public async Task State_mismatch_is_refused_outright()
    {
        var (projectId, _) = await SetupOAuthProjectAsync();
        var start = await Client.GetAsync(
            $"/v1/account/sessions/oauth2/github?project={projectId}" +
            "&success=https%3A%2F%2Fapp.example.com%2Fok&failure=https%3A%2F%2Fapp.example.com%2Ffail");
        var stateCookie = start.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith($"praxy_oauth_{projectId}=", StringComparison.Ordinal)).Split(';')[0];

        var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/account/sessions/oauth2/callback/github/{projectId}?code=good-code&state=forged-state");
        callback.Headers.Add("Cookie", stateCookie);
        var response = await Client.SendAsync(callback);
        await AssertError(response, 401, "user_invalid_token");
    }

    [Fact]
    public async Task Disabled_provider_and_unlisted_redirects_never_start_the_flow()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");

        // GitHub not enabled for the project.
        var disabled = await Client.GetAsync(
            $"/v1/account/sessions/oauth2/github?project={projectId}" +
            "&success=https%3A%2F%2Fapp.example.com%2Fok&failure=https%3A%2F%2Fapp.example.com%2Ffail");
        await AssertError(disabled, 400, "project_provider_disabled");

        await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/auth-settings", operatorToken,
            new { githubEnabled = true, githubClientId = "cid", githubClientSecret = "csecret" }));

        // Success URL on an unregistered host: the redirect allowlist is the phishing guard.
        var badRedirect = await Client.GetAsync(
            $"/v1/account/sessions/oauth2/github?project={projectId}" +
            "&success=https%3A%2F%2Fevil.example.net%2Fok&failure=https%3A%2F%2Fapp.example.com%2Ffail");
        await AssertError(badRedirect, 400, "general_argument_invalid");
    }
}
