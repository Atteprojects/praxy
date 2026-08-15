using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Praxy.Auth;
using Praxy.Auth.OAuth;

namespace Praxy.Tests.Integration.Infrastructure;

/// <summary>Captures outbound email so tests can pull verification/recovery/invite links out of it.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>Query-string values of the last link in the newest email (?userId=…&amp;secret=…).</summary>
    public Dictionary<string, string> LastLinkParams()
    {
        Assert.False(Sent.IsEmpty);
        var body = Sent.Last().TextBody;
        var url = body.Split('\n').First(l => l.StartsWith("http", StringComparison.Ordinal)).Trim();
        var query = new Uri(url).Query.TrimStart('?');
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }
}

/// <summary>
/// Base for Phase 1 auth tests: relaxed default rate limits (individual tests re-tighten via
/// settings), a capturing email sender, and helpers for the claim → project → app-user flow.
/// </summary>
public abstract class AuthTestBase(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    protected CapturingEmailSender Email { get; } = new();

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>
    {
        // High enough that ordinary test traffic never trips; the rate-limit tests override.
        ["Praxy:RateLimits:Auth:PermitLimit"] = "10000",
        ["Praxy:RateLimits:AuthEmail:PermitLimit"] = "10000",
        ["PRAXY_SECRET_KEY"] = "integration-test-instance-key",
    };

    protected override Action<IServiceCollection>? TestServices => services =>
        services.Replace(ServiceDescriptor.Singleton<IEmailSender>(Email));

    /// <summary>Claims the instance and creates a project; the standard Phase 1 test scaffold.</summary>
    protected async Task<(string OperatorToken, string ProjectId)> SetupProjectAsync(string name = "Acme")
    {
        var (operatorToken, _) = await ClaimAsync();
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return (operatorToken, body.GetProperty("id").GetString()!);
    }

    /// <summary>A data-plane request: project header plus optional session token or API key.</summary>
    protected static HttpRequestMessage DataPlane(
        HttpMethod method, string url, string projectId, string? sessionToken = null, string? apiKey = null,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Praxy-Project", projectId);
        if (sessionToken is not null)
            request.Headers.Add("X-Praxy-Session", sessionToken);
        if (apiKey is not null)
            request.Headers.Add("X-Praxy-Key", apiKey);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    protected async Task<(string Token, JsonElement User)> SignupAsync(
        string projectId, string email, string password = "correct-horse-battery", string name = "Test User")
    {
        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/account", projectId, body: new { email, password, name }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return (body.GetProperty("token").GetString()!, body.GetProperty("user"));
    }

    protected async Task<string> LoginAsync(
        string projectId, string email, string password = "correct-horse-battery")
    {
        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/account/sessions/email", projectId, body: new { email, password }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>Registers a web platform so redirect URLs / origins on this hostname pass the allowlist.</summary>
    protected async Task AddPlatformAsync(string operatorToken, string projectId, string hostname)
    {
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/platforms", operatorToken,
            new { type = "web", name = hostname, hostname }));
        Assert.Equal(201, (int)response.StatusCode);
    }

    protected async Task<(string KeyId, string Secret)> CreateApiKeyAsync(
        string operatorToken, string projectId, params string[] scopes)
    {
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/keys", operatorToken,
            new { name = "test key", scopes }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return (body.GetProperty("key").GetProperty("id").GetString()!, body.GetProperty("secret").GetString()!);
    }

    protected async Task<string[]> RolesAsync(string projectId, string? sessionToken = null, string? apiKey = null)
    {
        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/account/roles", projectId, sessionToken, apiKey));
        var body = await ReadJson(response);
        Assert.Equal(200, (int)response.StatusCode);
        return [.. body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)];
    }
}

/// <summary>Deterministic in-process stand-in for Google: no network, canned profile.</summary>
public sealed class FakeOAuthProvider : IOAuthProvider
{
    public string Name => "google";

    public OAuthProfile Profile { get; set; } =
        new("google-uid-1", "ada@example.com", EmailVerified: true, "Ada Lovelace");

    public string? LastCodeVerifier { get; private set; }

    public string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string codeChallenge) =>
        $"https://fake.google.test/authorize?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}" +
        $"&code_challenge={codeChallenge}";

    public Task<OAuthTokenResult> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri, string codeVerifier,
        CancellationToken ct = default)
    {
        LastCodeVerifier = codeVerifier;
        if (code != "good-code")
            throw new Praxy.Core.Errors.PraxyException(
                401, Praxy.Core.Errors.ErrorTypes.UserOauth2ProviderError, "Bad code.");
        return Task.FromResult(new OAuthTokenResult("fake-access-token", null));
    }

    public Task<OAuthProfile> FetchProfileAsync(string accessToken, CancellationToken ct = default) =>
        Task.FromResult(Profile);
}
