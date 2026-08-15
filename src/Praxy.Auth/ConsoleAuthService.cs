using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth;

public sealed record ConsoleAccount(Guid Id, string Email, string Name, DateTimeOffset CreatedAt);

public sealed record ConsoleSession(string Token, DateTimeOffset ExpiresAt, ConsoleAccount Account);

/// <summary>
/// Operator accounts — the users of the reserved <c>console</c> project. Instance claim,
/// login, logout, and session resolution. App-user auth (Phase 1) is a separate surface.
/// </summary>
public sealed class ConsoleAuthService(PraxyDb db, IPasswordHasher hasher)
{
    /// <summary>Serializes concurrent claim attempts; distinct from the migration lock key.</summary>
    private const long ClaimLockKey = 0x5052415859 + 1;

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(365);

    public async Task<bool> IsClaimedAsync(CancellationToken ct = default) =>
        await db.Users.AnyAsync(u => u.ProjectId == Ids.ConsoleProjectId, ct);

    /// <summary>
    /// First account claims the instance: creates the operator user, silently creates the
    /// "Personal" organization with the operator as owner, and opens a session — atomically.
    /// </summary>
    public async Task<ConsoleSession> ClaimAsync(
        string email, string password, string name, string? ip, string? userAgent, CancellationToken ct = default)
    {
        ValidateCredentials(email, password);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Two racing claims both pass the AnyAsync check without this lock; it is
        // transaction-scoped, so it releases on commit/rollback.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", ct);

        if (await IsClaimedAsync(ct))
            throw new PraxyException(409, ErrorTypes.InstanceAlreadyClaimed,
                "This instance has already been claimed. Sign in instead.");

        var user = new User
        {
            Id = Ids.NewUuid(),
            ProjectId = Ids.ConsoleProjectId,
            Email = NormalizeEmail(email),
            PasswordHash = hasher.Hash(password),
            Name = name.Trim(),
            EmailVerified = true,
        };
        db.Users.Add(user);

        var org = new Organization { Id = Ids.NewUuid(), Name = "Personal" };
        db.Organizations.Add(org);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Role = "owner",
        });

        var session = NewSession(user.Id, ip, userAgent, out var token);
        db.Sessions.Add(session);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ConsoleSession(token, session.ExpiresAt, ToAccount(user));
    }

    public async Task<ConsoleSession> LoginAsync(
        string email, string password, string? ip, string? userAgent, CancellationToken ct = default)
    {
        ValidateCredentials(email, password);

        var normalized = NormalizeEmail(email);
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.ProjectId == Ids.ConsoleProjectId && u.Email == normalized, ct);

        // Always burn a hash so a missing account and a wrong password take the same time.
        var verified = hasher.Verify(password, user?.PasswordHash ?? DummyHash.Value);
        if (user is null || user.PasswordHash is null || !verified || !user.Status)
            throw new PraxyException(401, ErrorTypes.UserInvalidCredentials, "Invalid email or password.");

        var session = NewSession(user.Id, ip, userAgent, out var token);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        return new ConsoleSession(token, session.ExpiresAt, ToAccount(user));
    }

    /// <summary>Resolves a session token to the operator account, or null when invalid/expired.</summary>
    public async Task<(Session Session, ConsoleAccount Account)?> ResolveSessionAsync(
        string token, CancellationToken ct = default)
    {
        if (!SessionTokens.TryParse(token, out var sessionId, out var secretHash))
            return null;

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || !SessionTokens.HashEquals(session.SecretHash, secretHash))
            return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == session.UserId && u.ProjectId == Ids.ConsoleProjectId && u.Status, ct);
        return user is null ? null : (session, ToAccount(user));
    }

    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);

    private static Session NewSession(Guid userId, string? ip, string? userAgent, out string token)
    {
        var id = Ids.NewUuid();
        (token, var secretHash) = SessionTokens.Create(id);
        return new Session
        {
            Id = id,
            UserId = userId,
            SecretHash = secretHash,
            Provider = "email",
            Ip = ip,
            UserAgent = userAgent is { Length: > 512 } ? userAgent[..512] : userAgent,
            ExpiresAt = DateTimeOffset.UtcNow + SessionLifetime,
        };
    }

    private static void ValidateCredentials(string email, string password)
    {
        var fields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 320)
            fields["email"] = ["Must be a valid email address."];
        if (password.Length < 8 || password.Length > 256)
            fields["password"] = ["Must be between 8 and 256 characters."];
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid credentials payload.", fields);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static ConsoleAccount ToAccount(User u) => new(u.Id, u.Email, u.Name, u.CreatedAt);

    /// <summary>A syntactically valid PHC hash of an unguessable value, for timing-flattened login.</summary>
    private static class DummyHash
    {
        public static readonly string Value =
            new Argon2PasswordHasher(new Argon2Options()).Hash(Guid.NewGuid().ToString("n"));
    }
}
