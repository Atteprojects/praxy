using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth;

public sealed record AppSession(Session Session, string Token, User User);

/// <summary>
/// App-user authentication for real (non-console) projects: signup with auto-created session,
/// login, session lifecycle with the per-user cap, token→session exchange, email verification,
/// and password recovery. Console operators never pass through here.
/// </summary>
public sealed class AppAuthService(
    PraxyDb db, IPasswordHasher hasher, ISessionCache cache, IEventBus bus, IAuthEmailSender email)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(365);
    public static readonly TimeSpan VerificationTokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RecoveryTokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan OAuthTokenLifetime = TimeSpan.FromSeconds(60);

    public const string OAuthTokenTypePrefix = "oauth2:";

    // ---- users ----------------------------------------------------------------------------

    /// <summary>Signup: creates the user and — per the roadmap — a session in one stroke.</summary>
    public async Task<AppSession> SignupAsync(
        Project project, string emailAddress, string password, string name, string? ip, string? userAgent,
        CancellationToken ct = default)
    {
        var settings = ProjectAuthSettings.Parse(project.Settings);
        if (!settings.EmailPassword)
            throw new PraxyException(400, ErrorTypes.ProjectAuthMethodDisabled,
                "Email/password authentication is disabled for this project.");

        var user = await CreateUserAsync(project, emailAddress, password, name, emailVerified: false, ct);
        return await CreateSessionAsync(project, user, "email", ip, userAgent, ct);
    }

    /// <summary>Shared by signup and console user creation. Password is optional (OAuth-only users).</summary>
    public async Task<User> CreateUserAsync(
        Project project, string emailAddress, string? password, string name, bool emailVerified,
        CancellationToken ct = default)
    {
        var settings = ProjectAuthSettings.Parse(project.Settings);
        ValidateEmail(emailAddress);
        if (password is not null)
            ValidatePassword(password, settings);
        if (name.Length > 128)
            throw PraxyException.ArgumentInvalid("Invalid user payload.",
                new Dictionary<string, string[]> { ["name"] = ["Must be at most 128 characters."] });

        var user = new User
        {
            Id = Ids.NewUuid(),
            ProjectId = project.Id,
            Email = NormalizeEmail(emailAddress),
            PasswordHash = password is null ? null : hasher.Hash(password),
            Name = name.Trim(),
            EmailVerified = emailVerified,
        };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            db.Entry(user).State = EntityState.Detached;
            throw new PraxyException(409, ErrorTypes.UserAlreadyExists,
                "A user with this email already exists in this project.");
        }

        await PublishAsync(project.Id, $"users.{Ids.Wire(user.Id)}.create", user.Id, ct);
        return user;
    }

    public async Task<AppSession> LoginAsync(
        Project project, string emailAddress, string password, string? ip, string? userAgent,
        CancellationToken ct = default)
    {
        var settings = ProjectAuthSettings.Parse(project.Settings);
        if (!settings.EmailPassword)
            throw new PraxyException(400, ErrorTypes.ProjectAuthMethodDisabled,
                "Email/password authentication is disabled for this project.");

        var normalized = NormalizeEmail(emailAddress);
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.ProjectId == project.Id && u.Email == normalized, ct);

        // Burn a hash either way so a missing account and a wrong password take the same time.
        var verified = hasher.Verify(password, user?.PasswordHash ?? DummyHash.Value);
        if (user is null || user.PasswordHash is null || !verified)
            throw new PraxyException(401, ErrorTypes.UserInvalidCredentials, "Invalid email or password.");
        if (!user.Status)
            throw new PraxyException(401, ErrorTypes.UserBlocked, "This account has been blocked.");

        return await CreateSessionAsync(project, user, "email", ip, userAgent, ct);
    }

    // ---- sessions -------------------------------------------------------------------------

    /// <summary>Creates a session, enforcing the per-user cap by evicting the oldest sessions.</summary>
    public async Task<AppSession> CreateSessionAsync(
        Project project, User user, string provider, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var settings = ProjectAuthSettings.Parse(project.Settings);

        var sessionId = Ids.NewUuid();
        (var token, var secretHash) = SessionTokens.Create(sessionId);
        var session = new Session
        {
            Id = sessionId,
            UserId = user.Id,
            SecretHash = secretHash,
            Provider = provider,
            Ip = ip,
            UserAgent = userAgent is { Length: > 512 } ? userAgent[..512] : userAgent,
            ExpiresAt = DateTimeOffset.UtcNow + SessionLifetime,
        };
        db.Sessions.Add(session);

        // Cap: the new session counts, so keep (limit - 1) existing ones, oldest out first.
        var evicted = await db.Sessions
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.CreatedAt)
            .Skip(settings.SessionLimit - 1)
            .ToListAsync(ct);
        db.Sessions.RemoveRange(evicted);

        await db.SaveChangesAsync(ct);

        await PublishAsync(project.Id, $"users.{Ids.Wire(user.Id)}.sessions.{Ids.Wire(session.Id)}.create", user.Id, ct);
        foreach (var old in evicted)
            await PublishSessionDeletedAsync(project.Id, user.Id, old.Id, ct);

        return new AppSession(session, token, user);
    }

    /// <summary>
    /// Session token → (session, user), through the 60s cache. Null when invalid, expired,
    /// blocked, or belonging to a different project.
    /// </summary>
    public async Task<CachedSession?> ResolveSessionAsync(string projectId, string token, CancellationToken ct = default)
    {
        if (!SessionTokens.TryParse(token, out var sessionId, out var secretHash))
            return null;

        if (!cache.TryGet(sessionId, out var entry))
        {
            var pair = await (
                from s in db.Sessions
                join u in db.Users on s.UserId equals u.Id
                where s.Id == sessionId
                select new { Session = s, User = u }).FirstOrDefaultAsync(ct);
            if (pair is null)
                return null;
            entry = new CachedSession(pair.Session, pair.User);
            cache.Set(entry);
        }

        if (!SessionTokens.HashEquals(entry.Session.SecretHash, secretHash))
            return null;
        if (entry.Session.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;
        if (entry.User.ProjectId != projectId || !entry.User.Status)
            return null;
        return entry;
    }

    public async Task DeleteSessionAsync(string projectId, User user, Guid sessionId, CancellationToken ct = default)
    {
        var deleted = await db.Sessions
            .Where(s => s.Id == sessionId && s.UserId == user.Id)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw PraxyException.NotFound(ErrorTypes.UserSessionNotFound, "Session not found.");
        await PublishSessionDeletedAsync(projectId, user.Id, sessionId, ct);
    }

    public async Task DeleteAllSessionsAsync(string projectId, User user, CancellationToken ct = default)
    {
        var ids = await db.Sessions.Where(s => s.UserId == user.Id).Select(s => s.Id).ToListAsync(ct);
        await db.Sessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync(ct);
        foreach (var id in ids)
            await PublishSessionDeletedAsync(projectId, user.Id, id, ct);
    }

    // ---- token → session exchange (OAuth, and any future magic-url/OTP flow) ----------------

    /// <summary>Mints a short-lived token row for <paramref name="user"/>; the secret goes to the caller once.</summary>
    public async Task<string> CreateTokenAsync(User user, string type, TimeSpan lifetime, CancellationToken ct = default)
    {
        var (secret, hash) = Secrets.Generate();
        db.Tokens.Add(new Token
        {
            Id = Ids.NewUuid(),
            UserId = user.Id,
            Type = type,
            SecretHash = hash,
            ExpiresAt = DateTimeOffset.UtcNow + lifetime,
        });
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// The universal converging point (<c>POST /v1/account/sessions/token</c>): consumes a
    /// one-time token and opens a session. Uniform 401 whether the user or the secret is wrong.
    /// </summary>
    public async Task<AppSession> ExchangeTokenAsync(
        Project project, Guid userId, string secret, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && u.ProjectId == project.Id, ct);

        var token = await ConsumeTokenAsync(user, secret, OAuthTokenTypePrefix, ct)
            ?? throw new PraxyException(401, ErrorTypes.UserInvalidToken, "Invalid or expired token.");
        if (!user!.Status)
            throw new PraxyException(401, ErrorTypes.UserBlocked, "This account has been blocked.");

        var provider = token.Type.StartsWith(OAuthTokenTypePrefix, StringComparison.Ordinal)
            ? token.Type[OAuthTokenTypePrefix.Length..]
            : "token";
        return await CreateSessionAsync(project, user, provider, ip, userAgent, ct);
    }

    /// <summary>
    /// Constant-time token consumption: fetches the user's live tokens of the given type
    /// (prefix match) and compares hashes in memory. Deletes the token on success.
    /// </summary>
    public async Task<Token?> ConsumeTokenAsync(User? user, string secret, string typePrefix, CancellationToken ct = default)
    {
        if (user is null)
        {
            // Burn a comparison anyway to keep "user missing" and "bad secret" uniform.
            Secrets.HashEquals(Secrets.Hash("praxy-dummy"), Secrets.Hash(secret));
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var candidateHash = Secrets.Hash(secret);
        var tokens = await db.Tokens
            .Where(t => t.UserId == user.Id && t.ExpiresAt > now && t.Type.StartsWith(typePrefix))
            .ToListAsync(ct);

        Token? match = null;
        foreach (var t in tokens)
            if (Secrets.HashEquals(t.SecretHash, candidateHash))
                match = t;
        if (match is null)
            return null;

        db.Tokens.Remove(match);
        await db.SaveChangesAsync(ct);
        return match;
    }

    // ---- email verification ----------------------------------------------------------------

    public async Task SendVerificationAsync(Project project, User user, string url, CancellationToken ct = default)
    {
        await ValidateRedirectUrlAsync(project, url, "url", ct);
        if (user.EmailVerified)
            throw PraxyException.ArgumentInvalid("This email address is already verified.");

        var secret = await CreateTokenAsync(user, "verification", VerificationTokenLifetime, ct);
        await email.SendAsync(project, AuthEmailTemplateKeys.Verification, user.Email, new Dictionary<string, string>
        {
            ["url"] = AppendTokenParams(url, user.Id, secret),
            ["project"] = project.Name,
            ["expiryMinutes"] = VerificationTokenLifetime.TotalMinutes.ToString("0"),
        }, ct);
    }

    public async Task<User> ConfirmVerificationAsync(Project project, Guid userId, string secret, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.ProjectId == project.Id, ct);
        _ = await ConsumeTokenAsync(user, secret, "verification", ct)
            ?? throw new PraxyException(401, ErrorTypes.UserInvalidToken, "Invalid or expired token.");

        user!.EmailVerified = true;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await PublishAsync(project.Id, $"users.{Ids.Wire(user.Id)}.update.verification", user.Id, ct);
        return user;
    }

    // ---- password recovery -----------------------------------------------------------------

    /// <summary>Uniform 204 whether or not the email exists — recovery must not enumerate accounts.</summary>
    public async Task SendRecoveryAsync(Project project, string emailAddress, string url, CancellationToken ct = default)
    {
        await ValidateRedirectUrlAsync(project, url, "url", ct);
        ValidateEmail(emailAddress);

        var normalized = NormalizeEmail(emailAddress);
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.ProjectId == project.Id && u.Email == normalized && u.Status, ct);
        if (user is null)
            return;

        var secret = await CreateTokenAsync(user, "recovery", RecoveryTokenLifetime, ct);
        await email.SendAsync(project, AuthEmailTemplateKeys.Recovery, user.Email, new Dictionary<string, string>
        {
            ["url"] = AppendTokenParams(url, user.Id, secret),
            ["project"] = project.Name,
            ["expiryMinutes"] = RecoveryTokenLifetime.TotalMinutes.ToString("0"),
        }, ct);
    }

    /// <summary>Resets the password and revokes every session — recovery implies the account was at risk.</summary>
    public async Task<User> ConfirmRecoveryAsync(
        Project project, Guid userId, string secret, string newPassword, CancellationToken ct = default)
    {
        var settings = ProjectAuthSettings.Parse(project.Settings);
        ValidatePassword(newPassword, settings);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.ProjectId == project.Id, ct);
        _ = await ConsumeTokenAsync(user, secret, "recovery", ct)
            ?? throw new PraxyException(401, ErrorTypes.UserInvalidToken, "Invalid or expired token.");

        user!.PasswordHash = hasher.Hash(newPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await DeleteAllSessionsAsync(project.Id, user, ct);
        await PublishAsync(project.Id, $"users.{Ids.Wire(user.Id)}.update.password", user.Id, ct);
        return user;
    }

    // ---- account updates ---------------------------------------------------------------------

    public async Task<User> UpdateNameAsync(string projectId, User user, string name, CancellationToken ct = default)
    {
        if (name.Length is < 1 or > 128)
            throw PraxyException.ArgumentInvalid("Invalid name.",
                new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });
        user.Name = name.Trim();
        user.UpdatedAt = DateTimeOffset.UtcNow;
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
        await PublishAsync(projectId, $"users.{Ids.Wire(user.Id)}.update.name", user.Id, ct);
        return user;
    }

    public async Task<User> UpdatePasswordAsync(
        Project project, User user, string newPassword, string? oldPassword, CancellationToken ct = default)
    {
        var settings = ProjectAuthSettings.Parse(project.Settings);
        ValidatePassword(newPassword, settings);
        // A user who signed up through OAuth has no password yet; only then is old-password optional.
        if (user.PasswordHash is not null &&
            (oldPassword is null || !hasher.Verify(oldPassword, user.PasswordHash)))
            throw new PraxyException(401, ErrorTypes.UserInvalidCredentials, "Invalid current password.");

        user.PasswordHash = hasher.Hash(newPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
        await PublishAsync(project.Id, $"users.{Ids.Wire(user.Id)}.update.password", user.Id, ct);
        return user;
    }

    public async Task<User> UpdatePrefsAsync(string projectId, User user, JsonObject prefs, CancellationToken ct = default)
    {
        user.Prefs = prefs.ToJsonString();
        user.UpdatedAt = DateTimeOffset.UtcNow;
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
        await PublishAsync(projectId, $"users.{Ids.Wire(user.Id)}.update.prefs", user.Id, ct);
        return user;
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// Redirect URLs (verification, recovery, invites, OAuth success/failure) must land on an
    /// allowlisted platform hostname — otherwise a crafted request turns our emails into
    /// phishing links. Security-critical; see architecture.md §11.
    /// </summary>
    public async Task ValidateRedirectUrlAsync(Project project, string url, string field, CancellationToken ct = default)
    {
        var hostnames = await db.Platforms
            .Where(p => p.ProjectId == project.Id && p.Hostname != null)
            .Select(p => p.Hostname!)
            .ToListAsync(ct);
        if (!PlatformAllowlist.RedirectAllowed(hostnames, url))
            throw PraxyException.ArgumentInvalid(
                "Redirect URL is not allowed. Register its hostname as a platform for this project first.",
                new Dictionary<string, string[]> { [field] = ["Hostname is not on the project's platform allowlist."] });
    }

    public static string AppendTokenParams(string url, Guid userId, string secret)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}userId={Ids.Wire(userId)}&secret={Uri.EscapeDataString(secret)}";
    }

    private async ValueTask PublishSessionDeletedAsync(string projectId, Guid userId, Guid sessionId, CancellationToken ct)
    {
        cache.Invalidate(sessionId);
        await PublishAsync(projectId, $"users.{Ids.Wire(userId)}.sessions.{Ids.Wire(sessionId)}.delete", userId, ct);
    }

    private async ValueTask PublishAsync(string projectId, string type, Guid userId, CancellationToken ct)
    {
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, projectId, type,
            [$"user:{Ids.Wire(userId)}"], null), ct);
    }

    private static void ValidateEmail(string emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress) || !emailAddress.Contains('@') || emailAddress.Length > 320)
            throw PraxyException.ArgumentInvalid("Invalid email address.",
                new Dictionary<string, string[]> { ["email"] = ["Must be a valid email address."] });
    }

    private static void ValidatePassword(string password, ProjectAuthSettings settings)
    {
        if (password.Length < settings.PasswordMinLength || password.Length > 256)
            throw PraxyException.ArgumentInvalid("Invalid password.",
                new Dictionary<string, string[]>
                {
                    ["password"] = [$"Must be between {settings.PasswordMinLength} and 256 characters."],
                });
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>A syntactically valid PHC hash of an unguessable value, for timing-flattened login.</summary>
    private static class DummyHash
    {
        public static readonly string Value =
            new Argon2PasswordHasher(new Argon2Options()).Hash(Guid.NewGuid().ToString("n"));
    }
}
