namespace Praxy.Auth.OAuth;

public sealed record OAuthTokenResult(string AccessToken, DateTimeOffset? ExpiresAt);

/// <summary>What Praxy needs to know about the provider-side account.</summary>
public sealed record OAuthProfile(string Uid, string? Email, bool EmailVerified, string? Name);

/// <summary>
/// One OAuth2 provider (authorization-code + PKCE). GitHub is the only implementation until
/// the owner says otherwise; Google et al. slot in behind this interface with no API changes.
/// </summary>
public interface IOAuthProvider
{
    /// <summary>Lowercase wire name — appears in routes (<c>/sessions/oauth2/github</c>) and session provider fields.</summary>
    string Name { get; }

    string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string codeChallenge);

    Task<OAuthTokenResult> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri, string codeVerifier,
        CancellationToken ct = default);

    Task<OAuthProfile> FetchProfileAsync(string accessToken, CancellationToken ct = default);
}

public interface IOAuthProviderRegistry
{
    IOAuthProvider? Get(string name);
}

public sealed class OAuthProviderRegistry(IEnumerable<IOAuthProvider> providers) : IOAuthProviderRegistry
{
    private readonly Dictionary<string, IOAuthProvider> _byName =
        providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IOAuthProvider? Get(string name) => _byName.GetValueOrDefault(name);
}
