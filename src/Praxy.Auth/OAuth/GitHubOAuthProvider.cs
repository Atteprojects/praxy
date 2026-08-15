using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Praxy.Core.Errors;

namespace Praxy.Auth.OAuth;

/// <summary>
/// GitHub OAuth (web application flow). PKCE parameters are sent even though GitHub currently
/// ignores them — the abstraction mandates PKCE and providers that honor it get it for free.
/// </summary>
public sealed class GitHubOAuthProvider(HttpClient http) : IOAuthProvider
{
    public string Name => "github";

    public string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string codeChallenge) =>
        "https://github.com/login/oauth/authorize" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        "&scope=read%3Auser%20user%3Aemail" +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
        "&code_challenge_method=S256";

    public async Task<OAuthTokenResult> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri, string codeVerifier,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonObject>(ct) : null;
        var accessToken = body?["access_token"]?.GetValue<string>();
        if (accessToken is null)
            throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError,
                $"GitHub rejected the authorization code ({body?["error"]?.GetValue<string>() ?? response.StatusCode.ToString()}).");

        DateTimeOffset? expiresAt = body?["expires_in"] is JsonValue v && v.TryGetValue<int>(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;
        return new OAuthTokenResult(accessToken, expiresAt);
    }

    public async Task<OAuthProfile> FetchProfileAsync(string accessToken, CancellationToken ct = default)
    {
        var user = await GetApiAsync("https://api.github.com/user", accessToken, ct)
            ?? throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError, "GitHub profile request failed.");

        var uid = user["id"]?.GetValue<long>().ToString()
            ?? throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError, "GitHub profile has no id.");
        var name = user["name"]?.GetValue<string>() ?? user["login"]?.GetValue<string>();

        // The profile email is often null (private); the emails endpoint carries the verified flag.
        string? emailAddress = null;
        var emailVerified = false;
        if (await GetApiAsync("https://api.github.com/user/emails", accessToken, ct) is JsonArray emails)
        {
            var primary = emails.OfType<JsonObject>()
                .FirstOrDefault(e => e["primary"]?.GetValue<bool>() == true)
                ?? emails.OfType<JsonObject>().FirstOrDefault();
            if (primary is not null)
            {
                emailAddress = primary["email"]?.GetValue<string>();
                emailVerified = primary["verified"]?.GetValue<bool>() == true;
            }
        }
        emailAddress ??= user["email"]?.GetValue<string>();

        return new OAuthProfile(uid, emailAddress, emailVerified, name);
    }

    private async Task<JsonNode?> GetApiAsync(string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Praxy", Praxy.Core.PraxyVersion.Current));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
