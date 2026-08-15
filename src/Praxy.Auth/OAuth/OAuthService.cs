using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth.OAuth;

/// <summary>
/// The OAuth token flow: browser-navigated start and callback endpoints plus the JWT wrap
/// that carries the one-time secret back to the app. The callback never opens a session
/// itself — the SDK exchanges the secret at <c>POST /v1/account/sessions/token</c>, which is
/// the same converging point every other token flow uses.
/// </summary>
public sealed class OAuthService(PraxyDb db, AppAuthService auth, IOAuthProviderRegistry providers, InstanceKey key)
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SecretJwtLifetime = TimeSpan.FromSeconds(60);

    public static string StateCookieName(string projectId) => $"praxy_oauth_{projectId}";

    public sealed record StartResult(string AuthorizeUrl, string StateCookieValue);

    public async Task<StartResult> StartAsync(
        Project project, string providerName, string successUrl, string failureUrl, string callbackUri,
        CancellationToken ct = default)
    {
        var provider = GetProvider(providerName);
        var settings = RequireProviderSettings(project, provider.Name);

        // Both redirect targets are attacker-suppliable query params — allowlist or refuse.
        await auth.ValidateRedirectUrlAsync(project, successUrl, "success", ct);
        await auth.ValidateRedirectUrlAsync(project, failureUrl, "failure", ct);

        var state = Secrets.Base64Url(RandomNumberGenerator.GetBytes(16));
        var verifier = Secrets.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Secrets.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // Everything the callback must trust rides in a signed, short-lived cookie — the
        // browser cannot tamper with success/failure or swap the PKCE verifier.
        var cookie = CompactJwt.Encode(key.SigningKey, new JsonObject
        {
            ["state"] = state,
            ["verifier"] = verifier,
            ["success"] = successUrl,
            ["failure"] = failureUrl,
            ["provider"] = provider.Name,
        }, StateLifetime);

        return new StartResult(
            provider.BuildAuthorizeUrl(settings.ClientId, callbackUri, state, challenge),
            cookie);
    }

    /// <summary>
    /// Returns the URL to redirect the browser to. Provider/user errors land on the failure
    /// URL with an <c>error</c> param; a broken or missing state cookie throws, because
    /// without it there is no trusted failure URL to land on.
    /// </summary>
    public async Task<string> HandleCallbackAsync(
        Project project, string providerName, string? code, string? state, string? providerError,
        string? stateCookieValue, string callbackUri, CancellationToken ct = default)
    {
        var provider = GetProvider(providerName);

        var claims = stateCookieValue is null ? null : CompactJwt.Decode(key.SigningKey, stateCookieValue);
        var expectedState = claims?["state"]?.GetValue<string>();
        var failureUrl = claims?["failure"]?.GetValue<string>();
        if (claims is null || failureUrl is null ||
            claims["provider"]?.GetValue<string>() != provider.Name ||
            expectedState is null || state is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedState), Encoding.UTF8.GetBytes(state)))
        {
            throw new PraxyException(401, ErrorTypes.UserInvalidToken,
                "OAuth state is missing, expired, or mismatched. Start the flow again.");
        }

        try
        {
            if (providerError is not null || code is null)
                throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError,
                    $"The provider reported an error: {providerError ?? "no authorization code returned"}.");

            var settings = RequireProviderSettings(project, provider.Name);
            var verifier = claims["verifier"]!.GetValue<string>();
            var token = await provider.ExchangeCodeAsync(
                settings.ClientId, settings.ClientSecret, code, callbackUri, verifier, ct);
            var profile = await provider.FetchProfileAsync(token.AccessToken, ct);

            var user = await ResolveUserAsync(project, provider.Name, profile, token, ct);
            var secret = await auth.CreateTokenAsync(
                user, AppAuthService.OAuthTokenTypePrefix + provider.Name, AppAuthService.OAuthTokenLifetime, ct);

            // The raw one-time secret never rides the redirect — a 60s JWT wraps it.
            var wrapped = CompactJwt.Encode(key.SigningKey, new JsonObject
            {
                ["secret"] = secret,
                ["provider"] = provider.Name,
            }, SecretJwtLifetime);

            var successUrl = claims["success"]!.GetValue<string>();
            var separator = successUrl.Contains('?') ? '&' : '?';
            return $"{successUrl}{separator}userId={Ids.Wire(user.Id)}&secret={Uri.EscapeDataString(wrapped)}";
        }
        catch (PraxyException ex)
        {
            var separator = failureUrl.Contains('?') ? '&' : '?';
            return $"{failureUrl}{separator}error={Uri.EscapeDataString(ex.Type)}";
        }
    }

    /// <summary>
    /// Token-exchange secrets may be JWT-wrapped (OAuth) or raw (future flows). Returns the
    /// inner secret either way; expired/invalid wraps fall through as-is and fail the DB check.
    /// </summary>
    public string UnwrapExchangeSecret(string secret)
    {
        if (secret.Count(c => c == '.') != 2)
            return secret;
        return CompactJwt.Decode(key.SigningKey, secret)?["secret"]?.GetValue<string>() ?? secret;
    }

    private IOAuthProvider GetProvider(string providerName) =>
        providers.Get(providerName)
        ?? throw PraxyException.ArgumentInvalid($"Unknown OAuth provider '{providerName}'.",
            new Dictionary<string, string[]> { ["provider"] = ["Supported providers: github."] });

    private static (string ClientId, string ClientSecret) RequireProviderSettings(Project project, string providerName)
    {
        // Only GitHub exists today; per-provider settings generalize when a second provider lands.
        var settings = ProjectAuthSettings.Parse(project.Settings);
        if (providerName != "github" || !settings.GitHubEnabled ||
            string.IsNullOrEmpty(settings.GitHubClientId) || string.IsNullOrEmpty(settings.GitHubClientSecret))
            throw new PraxyException(400, ErrorTypes.ProjectProviderDisabled,
                $"The {providerName} provider is not enabled for this project.");
        return (settings.GitHubClientId, settings.GitHubClientSecret);
    }

    /// <summary>
    /// Identity-first matching, then email linking — but an unverified provider email never
    /// links to an existing account (that would be an account takeover), and new users are
    /// only marked verified when the provider vouches for the address.
    /// </summary>
    private async Task<User> ResolveUserAsync(
        Project project, string providerName, OAuthProfile profile, OAuthTokenResult token, CancellationToken ct)
    {
        var identity = await db.Identities.FirstOrDefaultAsync(
            i => i.ProjectId == project.Id && i.Provider == providerName && i.ProviderUid == profile.Uid, ct);

        User user;
        if (identity is not null)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Id == identity.UserId, ct)
                ?? throw new PraxyException(401, ErrorTypes.UserNotFound, "The linked user no longer exists.");
        }
        else
        {
            if (string.IsNullOrEmpty(profile.Email))
                throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError,
                    "The provider account has no usable email address.");

            var normalized = AppAuthService.NormalizeEmail(profile.Email);
            var existing = await db.Users.FirstOrDefaultAsync(
                u => u.ProjectId == project.Id && u.Email == normalized, ct);
            if (existing is not null && !profile.EmailVerified)
                throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError,
                    "The provider email is unverified and already belongs to an account.");

            user = existing ?? await auth.CreateUserAsync(
                project, normalized, password: null, name: profile.Name ?? "", emailVerified: profile.EmailVerified, ct);

            identity = new Identity
            {
                Id = Ids.NewUuid(),
                ProjectId = project.Id,
                UserId = user.Id,
                Provider = providerName,
                ProviderUid = profile.Uid,
            };
            db.Identities.Add(identity);
        }

        if (!user.Status)
            throw new PraxyException(401, ErrorTypes.UserBlocked, "This account has been blocked.");

        identity.ProviderEmail = profile.Email;
        identity.AccessTokenEnc = key.Encrypt(token.AccessToken);
        identity.AccessTokenExpiresAt = token.ExpiresAt;
        identity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }
}
