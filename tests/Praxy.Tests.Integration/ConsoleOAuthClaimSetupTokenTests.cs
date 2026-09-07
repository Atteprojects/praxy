using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Praxy.Api.Infrastructure;
using Praxy.Auth.OAuth;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>When PRAXY_PUBLIC_URL is set, claiming via Google needs the setup token too — same gate as the password door (SetupTokenTests), just riding the state cookie instead of a request body.</summary>
public class ConsoleOAuthClaimSetupTokenTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private readonly FakeOAuthProvider _provider = new();

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(base.ExtraSettings!)
    {
        ["Praxy:ConsoleAuth:Google:ClientId"] = "console-cid",
        ["Praxy:ConsoleAuth:Google:ClientSecret"] = "console-csecret",
        ["PRAXY_PUBLIC_URL"] = "https://praxy.example.com",
    };

    protected override Action<IServiceCollection>? TestServices => services =>
    {
        base.TestServices!(services);
        services.RemoveAll<IOAuthProvider>();
        services.AddSingleton<IOAuthProvider>(_provider);
    };

    private async Task<(HttpResponseMessage Response, string? Token)> RunOAuthRoundTripAsync(string? setupToken)
    {
        var query = setupToken is null ? "" : $"?setupToken={Uri.EscapeDataString(setupToken)}";
        var start = await Client.GetAsync($"/v1/console/sessions/oauth2/google{query}");
        var authorizeLocation = start.Headers.Location!.ToString();
        var state = new Uri(authorizeLocation).Query.TrimStart('?')
            .Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]))["state"];
        var stateCookie = start.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("praxy_console_oauth=", StringComparison.Ordinal)).Split(';')[0];

        var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/console/sessions/oauth2/callback/google?code=good-code&state={state}");
        callback.Headers.Add("Cookie", stateCookie);
        var response = await Client.SendAsync(callback);

        var token = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(c => c.StartsWith("praxy_session_console=", StringComparison.Ordinal))?.Split(';')[0].Split('=', 2)[1]
            : null;
        return (response, token);
    }

    [Fact]
    public async Task Claiming_via_google_requires_the_setup_token_when_public_url_is_set()
    {
        var (withoutToken, noToken) = await RunOAuthRoundTripAsync(null);
        Assert.Equal(HttpStatusCode.Redirect, withoutToken.StatusCode);
        Assert.StartsWith("/login?oauthError=instance_setup_token_invalid", withoutToken.Headers.Location!.ToString());
        Assert.Null(noToken);

        var (wrongToken, stillNoToken) = await RunOAuthRoundTripAsync("0000000000000000");
        Assert.StartsWith("/login?oauthError=instance_setup_token_invalid", wrongToken.Headers.Location!.ToString());
        Assert.Null(stillNoToken);

        var realToken = Factory.Services.GetRequiredService<SetupTokenService>().Token;
        Assert.False(string.IsNullOrEmpty(realToken));

        var (success, token) = await RunOAuthRoundTripAsync(realToken);
        Assert.Equal("/", success.Headers.Location!.ToString());
        Assert.False(string.IsNullOrEmpty(token));
    }
}
