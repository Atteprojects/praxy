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

        users.MapGet("", List);
        users.MapPost("", Create);
        users.MapGet("/{userId}", Get);
        users.MapDelete("/{userId}", Delete);
        users.MapPatch("/{userId}/status", UpdateStatus);
        users.MapPatch("/{userId}/labels", UpdateLabels);
        users.MapGet("/{userId}/sessions", ListSessions);
        users.MapDelete("/{userId}/sessions", DeleteAllSessions);
        users.MapDelete("/{userId}/sessions/{sessionId}", DeleteSession);
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
        return Results.Ok(new { total, users = page.Select(AppUserResponse.From) });
    }

    private static async Task<IResult> Create(
        ServerCreateUserRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await auth.CreateUserAsync(project, req.Email, req.Password, req.Name ?? "", emailVerified: false, ct);
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
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, project.Id,
            $"users.{Ids.Wire(user.Id)}.update.labels", [$"user:{Ids.Wire(user.Id)}"], null), ct);
        return Results.Ok(AppUserResponse.From(user));
    }

    private static async Task<IResult> ListSessions(string userId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersRead);
        var user = await FindUserAsync(http, db, userId, ct);
        var sessions = await db.Sessions
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new { total = sessions.Count, sessions = sessions.Select(s => SessionResponse.From(s)) });
    }

    private static async Task<IResult> DeleteAllSessions(
        string userId, HttpContext http, PraxyDb db, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.UsersWrite);
        var user = await FindUserAsync(http, db, userId, ct);
        await auth.DeleteAllSessionsAsync(project.Id, user, ct);
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
        return Results.NoContent();
    }

    // ---- helpers -----------------------------------------------------------------------------

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
