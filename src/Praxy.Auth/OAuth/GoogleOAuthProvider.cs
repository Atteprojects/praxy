using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Praxy.Core.Errors;

namespace Praxy.Auth.OAuth;

/// <summary>
/// Google OAuth (web application flow). Unlike GitHub, Google actually validates the PKCE
/// code_verifier against the code_challenge it was issued — the abstraction mandates PKCE and
/// this is the provider that enforces it.
/// </summary>
public sealed class GoogleOAuthProvider(HttpClient http) : IOAuthProvider
{
    public string Name => "google";

    public string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string codeChallenge) =>
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        "&response_type=code" +
        $"&state={Uri.EscapeDataString(state)}" +
        "&scope=openid%20email%20profile" +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
        "&code_challenge_method=S256";

    public async Task<OAuthTokenResult> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri, string codeVerifier,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var accessToken = response.IsSuccessStatusCode ? body?["access_token"]?.GetValue<string>() : null;
        if (accessToken is null)
            throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError,
                $"Google rejected the authorization code ({body?["error"]?.GetValue<string>() ?? response.StatusCode.ToString()}).");

        DateTimeOffset? expiresAt = body?["expires_in"] is JsonValue v && v.TryGetValue<int>(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;
        return new OAuthTokenResult(accessToken, expiresAt);
    }

    public async Task<OAuthProfile> FetchProfileAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError, "Google profile request failed.");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject
            ?? throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError, "Google profile response was not valid JSON.");

        var uid = body["sub"]?.GetValue<string>()
            ?? throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError, "Google profile has no sub.");
        var email = body["email"]?.GetValue<string>();
        var name = body["name"]?.GetValue<string>();

        // email_verified has shipped as a JSON bool in some contexts and a JSON string ("true")
        // in others — parse defensively rather than trusting either shape.
        var emailVerified = body["email_verified"] switch
        {
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<string>(out var s) => bool.TryParse(s, out var b) && b,
            _ => false,
        };

        return new OAuthProfile(uid, email, emailVerified, name);
    }
}
