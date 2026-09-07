using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth.OAuth;

/// <summary>Which door a callback resolved through — only meaningful for audit logging on success.</summary>
public enum ConsoleOAuthIntent { Login, Claim, InviteAccept }

/// <summary>
/// Operator Google sign-in: claim, ordinary login, and organization-invite accept, all through the
/// same provider abstraction (<see cref="IOAuthProvider"/>) <see cref="OAuthService"/> uses for app
/// users — but not <see cref="OAuthService"/> itself. That class's credential lookup
/// (<c>ProjectAuthSettings</c>) and redirect-safety story (<c>ValidateRedirectUrlAsync</c> against a
/// project's registered platforms) are both scoped to a developer project; operators have no
/// project to hang either on (docs/handoff/organizations-phase-3-prompt.md's "the one thing to
/// understand before writing code").
///
/// Two deliberate differences from the app-user flow:
///
/// 1. **No caller-supplied success/failure URL.** The console is always served from the same
///    origin a browser navigates to <see cref="StateCookieName"/>'s start endpoint from
///    (Program.cs's SPA fallback serves it at the API's own root), so the callback always redirects
///    to a fixed, same-origin relative path — never an attacker-suppliable one.
/// 2. **A claimed instance never auto-creates an operator from a Google profile.** App users get
///    open signup; operator accounts are created exactly two ways — claim (once) and an
///    organization invite (<see cref="OrganizationsService.InviteAsync"/>) — and Google sign-in
///    only ever resolves into one of those, never a third door
///    (<see cref="ErrorTypes.ConsoleOAuthAccountNotFound"/> otherwise).
///
/// An operator's Google identity resolves within the single <c>console</c> project id's own
/// <see cref="Identity"/> space — the same table app-user identities use, scoped by
/// <c>ProjectId == Ids.ConsoleProjectId</c> rather than a new table, since the existing
/// <c>(ProjectId, Provider, ProviderUid)</c> unique index already gives "at most one console
/// identity per Google account" for free.
/// </summary>
public sealed class ConsoleOAuthService(
    PraxyDb db, ConsoleAuthService consoleAuth, OrganizationsService orgs,
    IOAuthProviderRegistry providers, InstanceKey key, ConsoleOAuthOptions options)
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    public const string StateCookieName = "praxy_console_oauth";

    public sealed record StartResult(string AuthorizeUrl, string StateCookieValue);

    public sealed record CallbackResult(string RedirectPath, ConsoleSession? Session, ConsoleOAuthIntent Intent);

    /// <summary>
    /// <paramref name="setupToken"/> only matters if the instance turns out to be unclaimed at
    /// callback time; <paramref name="inviteOrganizationId"/>/<paramref name="inviteUserId"/>/
    /// <paramref name="inviteSecret"/>, when all three are present, mean "accept this invite via
    /// Google" rather than an ordinary login. All four ride the signed state cookie — the callback
    /// never trusts its own query string for them.
    /// </summary>
    public StartResult Start(
        string providerName, string? setupToken,
        string? inviteOrganizationId, string? inviteUserId, string? inviteSecret,
        string callbackUri)
    {
        var provider = GetProvider(providerName);
        RequireConfigured();

        var state = Secrets.Base64Url(RandomNumberGenerator.GetBytes(16));
        var verifier = Secrets.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Secrets.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var cookie = CompactJwt.Encode(key.SigningKey, new JsonObject
        {
            ["state"] = state,
            ["verifier"] = verifier,
            ["provider"] = provider.Name,
            ["setupToken"] = setupToken,
            ["inviteOrganizationId"] = inviteOrganizationId,
            ["inviteUserId"] = inviteUserId,
            ["inviteSecret"] = inviteSecret,
        }, StateLifetime);

        return new StartResult(
            provider.BuildAuthorizeUrl(options.GoogleClientId, callbackUri, state, challenge),
            cookie);
    }

    /// <summary>
    /// Never throws — always returns a same-origin relative redirect path: <c>/</c> on success,
    /// <c>/login</c> (claim/login) or <c>/accept-invite?...</c> (invite, so the invitee doesn't
    /// lose their invite link's context) with an <c>oauthError</c> param on failure.
    /// <paramref name="validateSetupToken"/> is a delegate rather than an injected
    /// <c>SetupTokenService</c> because that type lives in <c>Praxy.Api</c>, which this project
    /// (<c>Praxy.Auth</c>) cannot reference — the endpoint captures <c>setupTokens.Validate</c>.
    /// </summary>
    public async Task<CallbackResult> HandleCallbackAsync(
        string providerName, string? code, string? state, string? providerError, string? stateCookieValue,
        string callbackUri, Func<string?, bool> validateSetupToken, string? ip, string? userAgent,
        CancellationToken ct = default)
    {
        var provider = GetProvider(providerName);

        var claims = stateCookieValue is null ? null : CompactJwt.Decode(key.SigningKey, stateCookieValue);
        var expectedState = claims?["state"]?.GetValue<string>();
        if (claims is null ||
            claims["provider"]?.GetValue<string>() != provider.Name ||
            expectedState is null || state is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedState), Encoding.UTF8.GetBytes(state)))
        {
            // No trustworthy claims to recover an invite's context from — fall back to /login,
            // same as a from-scratch retry would need anyway.
            return Failure("/login", ErrorTypes.UserInvalidToken, ConsoleOAuthIntent.Login);
        }

        var setupToken = claims["setupToken"]?.GetValue<string>();
        var inviteOrganizationId = claims["inviteOrganizationId"]?.GetValue<string>();
        var inviteUserId = claims["inviteUserId"]?.GetValue<string>();
        var inviteSecret = claims["inviteSecret"]?.GetValue<string>();
        var isInvite = inviteOrganizationId is not null && inviteUserId is not null && inviteSecret is not null;

        var failurePath = isInvite
            ? $"/accept-invite?organizationId={Uri.EscapeDataString(inviteOrganizationId!)}" +
              $"&userId={Uri.EscapeDataString(inviteUserId!)}&secret={Uri.EscapeDataString(inviteSecret!)}"
            : "/login";
        var intent = isInvite ? ConsoleOAuthIntent.InviteAccept : ConsoleOAuthIntent.Login;

        try
        {
            if (providerError is not null || code is null)
                throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError,
                    $"The provider reported an error: {providerError ?? "no authorization code returned"}.");
            RequireConfigured();

            var verifier = claims["verifier"]!.GetValue<string>();
            var token = await provider.ExchangeCodeAsync(
                options.GoogleClientId, options.GoogleClientSecret, code, callbackUri, verifier, ct);
            var profile = await provider.FetchProfileAsync(token.AccessToken, ct);

            ConsoleSession session;
            if (isInvite)
            {
                session = await AcceptInviteAsync(
                    provider.Name, inviteOrganizationId!, inviteUserId!, inviteSecret!, profile, token, ip, userAgent, ct);
            }
            else if (!await consoleAuth.IsClaimedAsync(ct))
            {
                intent = ConsoleOAuthIntent.Claim;
                session = await ClaimAsync(provider.Name, setupToken, validateSetupToken, profile, token, ip, userAgent, ct);
            }
            else
            {
                session = await LoginAsync(provider.Name, profile, token, ip, userAgent, ct);
            }

            return new CallbackResult("/", session, intent);
        }
        catch (PraxyException ex)
        {
            return Failure(failurePath, ex.Type, intent);
        }
    }

    private static CallbackResult Failure(string path, string errorType, ConsoleOAuthIntent intent)
    {
        var separator = path.Contains('?') ? '&' : '?';
        return new CallbackResult($"{path}{separator}oauthError={Uri.EscapeDataString(errorType)}", null, intent);
    }

    // ---- claim ----------------------------------------------------------------------------------

    private async Task<ConsoleSession> ClaimAsync(
        string providerName, string? setupToken, Func<string?, bool> validateSetupToken,
        OAuthProfile profile, OAuthTokenResult token, string? ip, string? userAgent, CancellationToken ct)
    {
        if (!validateSetupToken(setupToken))
            throw new PraxyException(401, ErrorTypes.InstanceSetupTokenInvalid,
                "A valid setup token is required to claim this instance. It is printed in the server logs at startup.");
        if (string.IsNullOrEmpty(profile.Email))
            throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError,
                "The provider account has no usable email address.");

        var user = new User
        {
            Id = Ids.NewUuid(),
            ProjectId = Ids.ConsoleProjectId,
            Email = ConsoleAuthService.NormalizeEmail(profile.Email),
            PasswordHash = null,
            Name = profile.Name ?? "",
            // The very first operator, bootstrapping their own instance behind a setup token
            // already proving server access — same unconditional EmailVerified the password claim
            // path uses, not profile.EmailVerified, for the same reason.
            EmailVerified = true,
        };
        var identity = NewIdentity(providerName, user.Id, profile, token);

        // ClaimResolvedUserAsync re-checks IsClaimedAsync inside the advisory lock the password
        // door also uses — a second claim attempt via either door still gets InstanceAlreadyClaimed.
        return await consoleAuth.ClaimResolvedUserAsync(user, [identity], ip, userAgent, ct);
    }

    // ---- login ----------------------------------------------------------------------------------

    private async Task<ConsoleSession> LoginAsync(
        string providerName, OAuthProfile profile, OAuthTokenResult token, string? ip, string? userAgent,
        CancellationToken ct)
    {
        var identity = await db.Identities.FirstOrDefaultAsync(
            i => i.ProjectId == Ids.ConsoleProjectId && i.Provider == providerName && i.ProviderUid == profile.Uid, ct);

        User user;
        var isNewIdentity = identity is null;
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

            var normalized = ConsoleAuthService.NormalizeEmail(profile.Email);
            var existing = await db.Users.FirstOrDefaultAsync(
                u => u.ProjectId == Ids.ConsoleProjectId && u.Email == normalized, ct);

            // An unverified provider email never links to an existing account — same rule
            // OAuthService.ResolveUserAsync enforces for app users, for the same reason: an
            // account existing at this address proves nothing about who controls the Google
            // account claiming it. Checked ahead of the "no account" case below so the two
            // failures don't leak which one applies.
            if (existing is not null && !profile.EmailVerified)
                throw new PraxyException(401, ErrorTypes.UserOauth2ProviderError,
                    "The provider email is unverified and already belongs to an account.");

            // Unlike app users, a claimed instance never creates a new operator here — only claim
            // and organization-invite ever do. A still-pending (unaccepted) invite has no password
            // yet either, so it falls into this same "no account" refusal rather than letting a
            // plain Google login silently stand in for the invite-accept door.
            if (existing is null || existing.PasswordHash is null)
                throw new PraxyException(401, ErrorTypes.ConsoleOAuthAccountNotFound,
                    "No Praxy operator account uses this email. Ask an organization owner to invite you first.");

            user = existing;
            identity = NewIdentity(providerName, user.Id, profile, token);
            db.Identities.Add(identity);
        }

        if (!user.Status)
            throw new PraxyException(401, ErrorTypes.UserBlocked, "This account has been blocked.");

        // NewIdentity already stamped a fresh identity's fields above; only a *reused* one (the
        // identity-first branch) needs refreshing here.
        if (!isNewIdentity)
            UpdateIdentity(identity!, profile, token);
        await db.SaveChangesAsync(ct);
        return await consoleAuth.CreateOperatorSessionAsync(user, ip, userAgent, ct);
    }

    // ---- invite accept --------------------------------------------------------------------------

    private async Task<ConsoleSession> AcceptInviteAsync(
        string providerName, string organizationIdWire, string userIdWire, string secret,
        OAuthProfile profile, OAuthTokenResult token, string? ip, string? userAgent, CancellationToken ct)
    {
        if (!Ids.TryParseWire(organizationIdWire, out var organizationId) || !Ids.TryParseWire(userIdWire, out var userId))
            throw new PraxyException(401, ErrorTypes.OrganizationInviteInvalid, "Invalid or expired invitation.");

        var (member, user) = await orgs.ValidateInviteSecretAsync(organizationId, userId, secret, ct);

        // Possession of the secret already proves receipt of the invite email — the same trust the
        // password door relies on. This is an extra check on top, not a replacement: the invited
        // row has its own fixed email (set when InviteAsync created or resolved it), and linking a
        // Google account that doesn't match it — or whose email the provider won't vouch for —
        // isn't "accepting your own invitation" the same way setting a password on that exact row
        // is (docs/handoff/organizations-phase-3-prompt.md's own landmine on this).
        if (string.IsNullOrEmpty(profile.Email) || !profile.EmailVerified ||
            !string.Equals(ConsoleAuthService.NormalizeEmail(profile.Email), user.Email, StringComparison.Ordinal))
            throw new PraxyException(401, ErrorTypes.ConsoleOAuthEmailMismatch,
                "This Google account's email does not match the invited address.");

        var existingIdentity = await db.Identities.FirstOrDefaultAsync(
            i => i.ProjectId == Ids.ConsoleProjectId && i.Provider == providerName && i.ProviderUid == profile.Uid, ct);
        if (existingIdentity is not null && existingIdentity.UserId != user.Id)
            throw new PraxyException(409, ErrorTypes.ConsoleOAuthIdentityAlreadyLinked,
                "This Google account is already linked to a different Praxy operator account.");

        Identity identity;
        if (existingIdentity is null)
        {
            identity = NewIdentity(providerName, user.Id, profile, token);
            db.Identities.Add(identity);
        }
        else
        {
            identity = existingIdentity;
            UpdateIdentity(identity, profile, token);
        }

        // Accepting proves control of the invited mailbox, same as the password door — Google's
        // own verification on top is a bonus, not a substitute for that reasoning.
        user.EmailVerified = true;
        member.Confirmed = true;
        member.SecretHash = null;
        await db.SaveChangesAsync(ct);

        return await consoleAuth.CreateOperatorSessionAsync(user, ip, userAgent, ct);
    }

    // ---- shared -----------------------------------------------------------------------------------

    private void RequireConfigured()
    {
        if (!options.GoogleConfigured)
            throw new PraxyException(400, ErrorTypes.ConsoleOAuthNotConfigured,
                "Google sign-in is not configured for this instance.");
    }

    private IOAuthProvider GetProvider(string providerName) =>
        providers.Get(providerName)
        ?? throw PraxyException.ArgumentInvalid($"Unknown OAuth provider '{providerName}'.",
            new Dictionary<string, string[]> { ["provider"] = ["Supported providers: google."] });

    private Identity NewIdentity(string providerName, Guid userId, OAuthProfile profile, OAuthTokenResult token)
    {
        var identity = new Identity
        {
            Id = Ids.NewUuid(),
            ProjectId = Ids.ConsoleProjectId,
            UserId = userId,
            Provider = providerName,
            ProviderUid = profile.Uid,
        };
        UpdateIdentity(identity, profile, token);
        return identity;
    }

    private void UpdateIdentity(Identity identity, OAuthProfile profile, OAuthTokenResult token)
    {
        identity.ProviderEmail = profile.Email;
        identity.AccessTokenEnc = key.Encrypt(token.AccessToken);
        identity.AccessTokenExpiresAt = token.ExpiresAt;
        identity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
