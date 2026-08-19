using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record ServerCreateUserRequest(string Email, string? Password, string? Name);

public sealed record UpdateUserStatusRequest(bool Status);

public sealed record UpdateUserLabelsRequest(string[] Labels);

public sealed record UpdateUserEmailRequest(string Email);

public sealed record UpdateUserNameRequest(string Name);

/// <summary>Operator-set password: no old password, because an operator has none to give.</summary>
public sealed record UpdateUserPasswordRequest(string Password);

public sealed record UpdateUserVerificationRequest(bool EmailVerified);

/// <summary>
/// The server-side users API (<c>/v1/users</c>): API-key callers only, gated per-endpoint on
/// <c>users.read</c> / <c>users.write</c> scopes. This is what backend SDKs use to manage a
/// project's users; the console has its own operator-authenticated equivalent.
/// </summary>
public static class UsersServerEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var users = api.MapGroup("/v1/users")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>();

        users.MapGet("", List).Produces<AppUserListResponse>();
        users.MapPost("", Create).Produces<AppUserResponse>(StatusCodes.Status201Created);
        users.MapGet("/{userId}", Get).Produces<AppUserResponse>();
        users.MapDelete("/{userId}", Delete).Produces(StatusCodes.Status204NoContent);
        users.MapPatch("/{userId}/status", UpdateStatus).Produces<AppUserResponse>();
        users.MapPatch("/{userId}/labels", UpdateLabels).Produces<AppUserResponse>();
        users.MapPatch("/{userId}/email", UpdateEmail).Produces<AppUserResponse>();
        users.MapPatch("/{userId}/name", UpdateName).Produces<AppUserResponse>();
        users.MapPatch("/{userId}/password", UpdatePassword).Produces<AppUserResponse>();
        users.MapPatch("/{userId}/verification", UpdateVerification).Produces<AppUserResponse>();
        users.MapGet("/{userId}/sessions", ListSessions).Produces<SessionListResponse>();
        users.MapDelete("/{userId}/sessions", DeleteAllSessions).Produces(StatusCodes.Status204NoContent);
        users.MapDelete("/{userId}/sessions/{sessionId}", DeleteSession).Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<IResult> List(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersRead);
        var (search, limit, offset) = ListParams(http);

        var query = db.Users.Where(u => u.ProjectId == project.Id);
        if (!string.IsNullOrEmpty(search))
            query = query.Where(u => u.Email.Contains(search) || u.Name.Contains(search));

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(u => u.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return Results.Ok(new AppUserListResponse(total, [.. page.Select(AppUserResponse.From)]));
    }

    private static async Task<IResult> Create(
        ServerCreateUserRequest req, HttpContext http, PraxyDb db, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await auth.CreateUserAsync(project, req.Email, req.Password, req.Name ?? "", emailVerified: false, ct);
        await AuditAsync(db, http, project.Id, "users.create", $"user/{Ids.Wire(user.Id)}", ct);
        return Results.Created($"/v1/users/{Ids.Wire(user.Id)}", AppUserResponse.From(user));
    }

    private static async Task<IResult> Get(string userId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersRead);
        return Results.Ok(AppUserResponse.From(await FindUserAsync(http, db, userId, ct)));
    }

    private static async Task<IResult> Delete(
        string userId, HttpContext http, PraxyDb db, IEventBus bus, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, http, project.Id, "users.delete", $"user/{Ids.Wire(user.Id)}", ct);
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, project.Id,
            $"users.{Ids.Wire(user.Id)}.delete", [$"user:{Ids.Wire(user.Id)}"], null), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateStatus(
        string userId, UpdateUserStatusRequest req, HttpContext http, PraxyDb db, IEventBus bus, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        user.Status = req.Status;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, http, project.Id, req.Status ? "users.unblock" : "users.block", $"user/{Ids.Wire(user.Id)}", ct);
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, project.Id,
            $"users.{Ids.Wire(user.Id)}.update.status", [$"user:{Ids.Wire(user.Id)}"], null), ct);
        return Results.Ok(AppUserResponse.From(user));
    }

    private static async Task<IResult> UpdateLabels(
        string userId, UpdateUserLabelsRequest req, HttpContext http, PraxyDb db, IEventBus bus, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        ValidateLabels(req.Labels);
        var user = await FindUserAsync(http, db, userId, ct);
        user.Labels = req.Labels;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, http, project.Id, "users.labels", $"user/{Ids.Wire(user.Id)}", ct);
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, project.Id,
            $"users.{Ids.Wire(user.Id)}.update.labels", [$"user:{Ids.Wire(user.Id)}"], null), ct);
        return Results.Ok(AppUserResponse.From(user));
    }

    /// <summary>
    /// Mirrors the console's change-email: the address moves and verified-ness resets with it.
    /// A collision inside the project is the existing <c>user_already_exists</c>, not a 500.
    /// </summary>
    private static async Task<IResult> UpdateEmail(
        string userId, UpdateUserEmailRequest req, HttpContext http, PraxyDb db, AppAuthService auth,
        CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        var updated = await auth.AdminUpdateEmailAsync(project, user, req.Email, ct);
        await AuditAsync(db, http, project.Id, "users.email.update", $"user/{Ids.Wire(user.Id)}", ct);
        return Results.Ok(AppUserResponse.From(updated));
    }

    private static async Task<IResult> UpdateName(
        string userId, UpdateUserNameRequest req, HttpContext http, PraxyDb db, AppAuthService auth,
        CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        var updated = await auth.UpdateNameAsync(project.Id, user, req.Name, ct);
        await AuditAsync(db, http, project.Id, "users.name.update", $"user/{Ids.Wire(user.Id)}", ct);
        return Results.Ok(AppUserResponse.From(updated));
    }

    /// <summary>Sets a password without the old one — and revokes every session, as the console does.</summary>
    private static async Task<IResult> UpdatePassword(
        string userId, UpdateUserPasswordRequest req, HttpContext http, PraxyDb db, AppAuthService auth,
        CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        var updated = await auth.AdminResetPasswordAsync(project, user, req.Password, ct);
        await AuditAsync(db, http, project.Id, "users.password.reset", $"user/{Ids.Wire(user.Id)}", ct);
        return Results.Ok(AppUserResponse.From(updated));
    }

    private static async Task<IResult> UpdateVerification(
        string userId, UpdateUserVerificationRequest req, HttpContext http, PraxyDb db, AppAuthService auth,
        CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        var updated = await auth.AdminSetEmailVerifiedAsync(project.Id, user, req.EmailVerified, ct);
        await AuditAsync(db, http, project.Id,
            req.EmailVerified ? "users.verification.grant" : "users.verification.revoke", $"user/{Ids.Wire(user.Id)}", ct);
        return Results.Ok(AppUserResponse.From(updated));
    }

    private static async Task<IResult> ListSessions(string userId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersRead);
        var user = await FindUserAsync(http, db, userId, ct);
        var sessions = await db.Sessions
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new SessionListResponse(sessions.Count, [.. sessions.Select(s => SessionResponse.From(s))]));
    }

    private static async Task<IResult> DeleteAllSessions(
        string userId, HttpContext http, PraxyDb db, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        await auth.DeleteAllSessionsAsync(project.Id, user, ct);
        await AuditAsync(db, http, project.Id, "sessions.delete_all", $"user/{Ids.Wire(user.Id)}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSession(
        string userId, string sessionId, HttpContext http, PraxyDb db, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        if (!Ids.TryParseWire(sessionId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.UserSessionNotFound, "Session not found.");
        await auth.DeleteSessionAsync(project.Id, user, parsed, ct);
        await AuditAsync(db, http, project.Id, "sessions.delete", $"session/{sessionId}", ct);
        return Results.NoContent();
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// The server surface writes audit entries now too — a <c>users.write</c> key can reset a
    /// password or change an email exactly like a console operator, and a read surface that silently
    /// covered only console actions would mislead by omission. Actor is <c>key:&lt;id&gt;</c>: every
    /// caller here has already passed <see cref="AppPrincipalFilter.RequireScope"/>, which only ever
    /// resolves a <see cref="RequestPrincipal.Key"/>.
    /// </summary>
    private static async Task AuditAsync(
        PraxyDb db, HttpContext http, string projectId, string action, string resource, CancellationToken ct)
    {
        if (AppPrincipalFilter.Current(http) is not RequestPrincipal.Key(var apiKey))
            throw new InvalidOperationException("Server user-write endpoints require a key principal.");
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Actor = $"key:{Ids.Wire(apiKey.Id)}",
            Action = action,
            Resource = resource,
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }

    internal static async Task<User> FindUserAsync(HttpContext http, PraxyDb db, string userId, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        if (!Ids.TryParseWire(userId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");
        return await db.Users.FirstOrDefaultAsync(u => u.Id == parsed && u.ProjectId == project.Id, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");
    }

    internal static void ValidateLabels(string[] labels)
    {
        if (labels.Length > 32 ||
            labels.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > 36 || !l.All(char.IsAsciiLetterOrDigit)))
            throw PraxyException.ArgumentInvalid("Invalid labels.",
                new Dictionary<string, string[]>
                {
                    ["labels"] = ["At most 32 alphanumeric labels of at most 36 characters."],
                });
    }

    internal static (string? Search, int Limit, int Offset) ListParams(HttpContext http)
    {
        var search = http.Request.Query["search"].FirstOrDefault();
        _ = int.TryParse(http.Request.Query["limit"].FirstOrDefault(), out var limit);
        _ = int.TryParse(http.Request.Query["offset"].FirstOrDefault(), out var offset);
        return (
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            limit is < 1 or > 100 ? 25 : limit,
            Math.Clamp(offset, 0, 100_000));
    }
}
