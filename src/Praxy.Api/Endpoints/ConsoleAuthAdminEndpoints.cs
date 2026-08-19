using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record ConsoleCreateUserRequest(string Email, string? Password, string? Name);

public sealed record ConsoleCreateTeamRequest(string Name);

public sealed record ConsoleAddMemberRequest(string? Email, string? UserId, string[]? Roles);

public sealed record UpdateAuthSettingsRequest(
    bool? EmailPassword, bool? GoogleEnabled, string? GoogleClientId, string? GoogleClientSecret,
    int? SessionLimit, int? PasswordMinLength);

public sealed record CreateApiKeyRequest(
    string Name, string[] Scopes, DateTimeOffset? ExpiresAt, bool? BypassRowPermissions);

public sealed record CreatePlatformRequest(string Type, string Name, string? Hostname);

/// <summary>
/// Operator-facing project administration: app users, teams, auth settings, API keys, and
/// platforms — everything the Phase 1 console screens sit on. Operator session + project
/// ownership enforced by the filter chain; the reserved console project can never appear here.
/// </summary>
/// <summary>One OAuth identity linked to an app user. Console-only detail.</summary>
public sealed record IdentityResponse(
    string Id, string Provider, string ProviderUid, string? ProviderEmail, DateTimeOffset CreatedAt);

/// <summary>A user row on the console's users table, with the activity column that list needs.</summary>
public sealed record ConsoleUserRow(AppUserResponse User, DateTimeOffset? LastActivityAt);

public sealed record ConsoleUserListResponse(int Total, IReadOnlyList<ConsoleUserRow> Users);

public sealed record ConsoleUserDetailResponse(
    AppUserResponse User, IReadOnlyList<IdentityResponse> Identities);

/// <summary>A membership plus its team's name, so the user-detail screen needs one request, not N.</summary>
public sealed record ConsoleMembershipRow(MembershipResponse Membership, string TeamName);

public sealed record ConsoleMembershipListResponse(
    int Total, IReadOnlyList<ConsoleMembershipRow> Memberships);

/// <summary>The secret appears exactly once, here — only its hash survives.</summary>
public sealed record CreatedApiKeyResponse(ApiKeyResponse Key, string Secret);

public static class ConsoleAuthAdminEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/v1/console/projects/{projectId}")
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>();

        admin.MapGet("/users", ListUsers).Produces<ConsoleUserListResponse>();
        admin.MapPost("/users", CreateUser).Produces<AppUserResponse>(StatusCodes.Status201Created);
        admin.MapGet("/users/{userId}", GetUser).Produces<ConsoleUserDetailResponse>();
        admin.MapDelete("/users/{userId}", DeleteUser).Produces(StatusCodes.Status204NoContent);
        admin.MapPatch("/users/{userId}/status", UpdateUserStatus).Produces<AppUserResponse>();
        admin.MapPatch("/users/{userId}/labels", UpdateUserLabels).Produces<AppUserResponse>();
        admin.MapGet("/users/{userId}/sessions", ListUserSessions).Produces<SessionListResponse>();
        admin.MapDelete("/users/{userId}/sessions", DeleteUserSessions).Produces(StatusCodes.Status204NoContent);
        admin.MapDelete("/users/{userId}/sessions/{sessionId}", DeleteUserSession)
            .Produces(StatusCodes.Status204NoContent);
        admin.MapGet("/users/{userId}/memberships", ListUserMemberships)
            .Produces<ConsoleMembershipListResponse>();

        admin.MapGet("/teams", ListTeams).Produces<TeamListResponse>();
        admin.MapPost("/teams", CreateTeam).Produces<TeamResponse>(StatusCodes.Status201Created);
        admin.MapGet("/teams/{teamId}", GetTeam).Produces<TeamResponse>();
        admin.MapDelete("/teams/{teamId}", DeleteTeam).Produces(StatusCodes.Status204NoContent);
        admin.MapGet("/teams/{teamId}/memberships", ListTeamMemberships).Produces<MembershipListResponse>();
        admin.MapPost("/teams/{teamId}/memberships", AddTeamMember)
            .Produces<MembershipResponse>(StatusCodes.Status201Created);
        admin.MapDelete("/teams/{teamId}/memberships/{membershipId}", DeleteTeamMembership)
            .Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/auth-settings", GetAuthSettings).Produces<AuthSettingsResponse>();
        admin.MapPatch("/auth-settings", UpdateAuthSettings).Produces<AuthSettingsResponse>();

        admin.MapGet("/keys", ListKeys).Produces<ApiKeyListResponse>();
        admin.MapPost("/keys", CreateKey).Produces<CreatedApiKeyResponse>(StatusCodes.Status201Created);
        admin.MapDelete("/keys/{keyId}", DeleteKey).Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/platforms", ListPlatforms).Produces<PlatformListResponse>();
        admin.MapPost("/platforms", CreatePlatform).Produces<PlatformResponse>(StatusCodes.Status201Created);
        admin.MapDelete("/platforms/{platformId}", DeletePlatform).Produces(StatusCodes.Status204NoContent);
    }

    // ---- users --------------------------------------------------------------------------------

    private static async Task<IResult> ListUsers(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var (search, limit, offset) = UsersServerEndpoints.ListParams(http);

        var query = db.Users.Where(u => u.ProjectId == project.Id);
        if (!string.IsNullOrEmpty(search))
            query = query.Where(u => u.Email.Contains(search) || u.Name.Contains(search));

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(u => u.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);

        // Last activity = newest session per user on this page; one grouped query, no N+1.
        var ids = page.Select(u => u.Id).ToArray();
        var lastActivity = await db.Sessions
            .Where(s => ids.Contains(s.UserId))
            .GroupBy(s => s.UserId)
            .Select(g => new { g.Key, Last = g.Max(s => s.CreatedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.Last, ct);

        return Results.Ok(new ConsoleUserListResponse(total, [.. page.Select(u => new ConsoleUserRow(
            AppUserResponse.From(u),
            lastActivity.TryGetValue(u.Id, out var at) ? at : null))]));
    }

    private static async Task<IResult> CreateUser(
        ConsoleCreateUserRequest req, HttpContext http, AppAuthService auth, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var user = await auth.CreateUserAsync(project, req.Email, req.Password, req.Name ?? "", emailVerified: false, ct);
        await AuditAsync(db, http, project.Id, "users.create", $"user/{Ids.Wire(user.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/users/{Ids.Wire(user.Id)}", AppUserResponse.From(user));
    }

    private static async Task<IResult> GetUser(string userId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var user = await FindUserAsync(db, project.Id, userId, ct);
        var identities = await db.Identities.Where(i => i.UserId == user.Id).ToListAsync(ct);
        return Results.Ok(new ConsoleUserDetailResponse(
            AppUserResponse.From(user),
            [.. identities.Select(i => new IdentityResponse(
                Ids.Wire(i.Id), i.Provider, i.ProviderUid, i.ProviderEmail, i.CreatedAt))]));
    }

    private static async Task<IResult> DeleteUser(
        string userId, HttpContext http, PraxyDb db, IEventBus bus, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var user = await FindUserAsync(db, project.Id, userId, ct);
        db.Users.Remove(user);
        await AuditAsync(db, http, project.Id, "users.delete", $"user/{Ids.Wire(user.Id)}", ct);
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, project.Id,
            $"users.{Ids.Wire(user.Id)}.delete", [$"user:{Ids.Wire(user.Id)}"], null), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateUserStatus(
        string userId, UpdateUserStatusRequest req, HttpContext http, PraxyDb db, IEventBus bus, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var user = await FindUserAsync(db, project.Id, userId, ct);
        user.Status = req.Status;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await AuditAsync(db, http, project.Id,
            req.Status ? "users.unblock" : "users.block", $"user/{Ids.Wire(user.Id)}", ct);
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, project.Id,
            $"users.{Ids.Wire(user.Id)}.update.status", [$"user:{Ids.Wire(user.Id)}"], null), ct);
        return Results.Ok(AppUserResponse.From(user));
    }

    private static async Task<IResult> UpdateUserLabels(
        string userId, UpdateUserLabelsRequest req, HttpContext http, PraxyDb db, IEventBus bus, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        UsersServerEndpoints.ValidateLabels(req.Labels);
        var user = await FindUserAsync(db, project.Id, userId, ct);
        user.Labels = req.Labels;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await AuditAsync(db, http, project.Id, "users.labels", $"user/{Ids.Wire(user.Id)}", ct);
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, project.Id,
            $"users.{Ids.Wire(user.Id)}.update.labels", [$"user:{Ids.Wire(user.Id)}"], null), ct);
        return Results.Ok(AppUserResponse.From(user));
    }

    private static async Task<IResult> ListUserSessions(
        string userId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var user = await FindUserAsync(db, project.Id, userId, ct);
        var sessions = await db.Sessions
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new SessionListResponse(sessions.Count, [.. sessions.Select(s => SessionResponse.From(s))]));
    }

    private static async Task<IResult> DeleteUserSessions(
        string userId, HttpContext http, PraxyDb db, AppAuthService auth, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var user = await FindUserAsync(db, project.Id, userId, ct);
        await auth.DeleteAllSessionsAsync(project.Id, user, ct);
        await AuditAsync(db, http, project.Id, "sessions.delete_all", $"user/{Ids.Wire(user.Id)}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteUserSession(
        string userId, string sessionId, HttpContext http, PraxyDb db, AppAuthService auth, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var user = await FindUserAsync(db, project.Id, userId, ct);
        if (!Ids.TryParseWire(sessionId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.UserSessionNotFound, "Session not found.");
        await auth.DeleteSessionAsync(project.Id, user, parsed, ct);
        await AuditAsync(db, http, project.Id, "sessions.delete", $"session/{sessionId}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListUserMemberships(
        string userId, HttpContext http, PraxyDb db, TeamsService teams, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var user = await FindUserAsync(db, project.Id, userId, ct);
        var memberships = await teams.ListUserMembershipsAsync(user.Id, ct);
        var teamNames = await db.Teams
            .Where(t => memberships.Select(m => m.Membership.TeamId).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return Results.Ok(new ConsoleMembershipListResponse(
            memberships.Count,
            [.. memberships.Select(m => new ConsoleMembershipRow(
                MembershipResponse.From(m),
                teamNames.GetValueOrDefault(m.Membership.TeamId, "")))]));
    }

    // ---- teams --------------------------------------------------------------------------------

    private static async Task<IResult> ListTeams(HttpContext http, PraxyDb db, TeamsService teams, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await teams.ListTeamsAsync(project.Id, null, ct);
        var counts = await db.Memberships
            .Where(m => list.Select(t => t.Id).Contains(m.TeamId) && m.Confirmed)
            .GroupBy(m => m.TeamId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return Results.Ok(new TeamListResponse(
            list.Count, [.. list.Select(t => TeamResponse.From(t, counts.GetValueOrDefault(t.Id)))]));
    }

    private static async Task<IResult> CreateTeam(
        ConsoleCreateTeamRequest req, HttpContext http, PraxyDb db, TeamsService teams, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var team = await teams.CreateTeamAsync(project, creator: null, req.Name, [], ct);
        await AuditAsync(db, http, project.Id, "teams.create", $"team/{Ids.Wire(team.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/teams/{Ids.Wire(team.Id)}", TeamResponse.From(team, 0));
    }

    private static async Task<IResult> GetTeam(
        string teamId, HttpContext http, PraxyDb db, TeamsService teams, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var team = await FindTeamAsync(teams, project.Id, teamId, ct);
        var count = await db.Memberships.CountAsync(m => m.TeamId == team.Id && m.Confirmed, ct);
        return Results.Ok(TeamResponse.From(team, count));
    }

    private static async Task<IResult> DeleteTeam(
        string teamId, HttpContext http, PraxyDb db, TeamsService teams, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var team = await FindTeamAsync(teams, project.Id, teamId, ct);
        await teams.DeleteTeamAsync(team, ct);
        await AuditAsync(db, http, project.Id, "teams.delete", $"team/{Ids.Wire(team.Id)}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListTeamMemberships(
        string teamId, HttpContext http, TeamsService teams, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var team = await FindTeamAsync(teams, project.Id, teamId, ct);
        var memberships = await teams.ListMembershipsAsync(team.Id, ct);
        return Results.Ok(new MembershipListResponse(
            memberships.Count, [.. memberships.Select(MembershipResponse.From)]));
    }

    /// <summary>Console adds are server semantics: the member lands confirmed, no invite email.</summary>
    private static async Task<IResult> AddTeamMember(
        string teamId, ConsoleAddMemberRequest req, HttpContext http, PraxyDb db, TeamsService teams,
        CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var team = await FindTeamAsync(teams, project.Id, teamId, ct);

        Guid? targetUserId = null;
        if (req.UserId is not null)
        {
            if (!Ids.TryParseWire(req.UserId, out var parsed))
                throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");
            targetUserId = parsed;
        }

        var membership = await teams.AddMemberAsync(project, team, req.Email, targetUserId, req.Roles ?? [], ct);
        await AuditAsync(db, http, project.Id, "memberships.create",
            $"team/{Ids.Wire(team.Id)}/membership/{Ids.Wire(membership.Membership.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/teams/{Ids.Wire(team.Id)}/memberships/{Ids.Wire(membership.Membership.Id)}",
            MembershipResponse.From(membership));
    }

    private static async Task<IResult> DeleteTeamMembership(
        string teamId, string membershipId, HttpContext http, PraxyDb db, TeamsService teams, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var team = await FindTeamAsync(teams, project.Id, teamId, ct);
        if (!Ids.TryParseWire(membershipId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.MembershipNotFound, "Membership not found.");
        var membership = await teams.GetMembershipAsync(team.Id, parsed, ct);
        await teams.DeleteMembershipAsync(project.Id, membership, ct);
        await AuditAsync(db, http, project.Id, "memberships.delete",
            $"team/{Ids.Wire(team.Id)}/membership/{membershipId}", ct);
        return Results.NoContent();
    }

    // ---- auth settings ------------------------------------------------------------------------

    private static IResult GetAuthSettings(HttpContext http)
    {
        var project = ConsoleProjectFilter.Current(http);
        return Results.Ok(AuthSettingsResponse.From(ProjectAuthSettings.Parse(project.Settings)));
    }

    private static async Task<IResult> UpdateAuthSettings(
        UpdateAuthSettingsRequest req, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var current = ProjectAuthSettings.Parse(project.Settings);

        if (req.SessionLimit is < 1 or > ProjectAuthSettings.MaxSessionLimit)
            throw PraxyException.ArgumentInvalid("Invalid session limit.",
                new Dictionary<string, string[]>
                {
                    ["sessionLimit"] = [$"Must be between 1 and {ProjectAuthSettings.MaxSessionLimit}."],
                });
        if (req.PasswordMinLength is < 8 or > 128)
            throw PraxyException.ArgumentInvalid("Invalid password policy.",
                new Dictionary<string, string[]> { ["passwordMinLength"] = ["Must be between 8 and 128."] });

        var updated = current with
        {
            EmailPassword = req.EmailPassword ?? current.EmailPassword,
            GoogleEnabled = req.GoogleEnabled ?? current.GoogleEnabled,
            GoogleClientId = req.GoogleClientId ?? current.GoogleClientId,
            // Secret is write-only: null means "keep", empty string means "clear".
            GoogleClientSecret = req.GoogleClientSecret switch
            {
                null => current.GoogleClientSecret,
                "" => null,
                var s => s,
            },
            SessionLimit = req.SessionLimit ?? current.SessionLimit,
            PasswordMinLength = req.PasswordMinLength ?? current.PasswordMinLength,
        };

        project.Settings = updated.ApplyTo(project.Settings);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        db.Projects.Update(project);
        await AuditAsync(db, http, project.Id, "auth_settings.update", $"project/{project.Id}", ct);
        return Results.Ok(AuthSettingsResponse.From(updated));
    }

    // ---- API keys -----------------------------------------------------------------------------

    private static async Task<IResult> ListKeys(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var keys = await db.ApiKeys
            .Where(k => k.ProjectId == project.Id)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new ApiKeyListResponse(keys.Count, [.. keys.Select(ApiKeyResponse.From)]));
    }

    private static async Task<IResult> CreateKey(
        CreateApiKeyRequest req, HttpContext http, PraxyDb db, ApiKeyService apiKeys, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var (key, secret) = await apiKeys.CreateAsync(
            project.Id, req.Name, req.Scopes, req.ExpiresAt, req.BypassRowPermissions ?? false, ct);
        await AuditAsync(db, http, project.Id, "keys.create", $"key/{Ids.Wire(key.Id)}", ct);
        // The secret appears exactly once — here. Only its hash survives.
        return Results.Created(
            $"/v1/console/projects/{project.Id}/keys/{Ids.Wire(key.Id)}",
            new CreatedApiKeyResponse(ApiKeyResponse.From(key), secret));
    }

    private static async Task<IResult> DeleteKey(
        string keyId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        if (!Ids.TryParseWire(keyId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.ApiKeyNotFound, "API key not found.");
        var deleted = await db.ApiKeys
            .Where(k => k.Id == parsed && k.ProjectId == project.Id)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw PraxyException.NotFound(ErrorTypes.ApiKeyNotFound, "API key not found.");
        await AuditAsync(db, http, project.Id, "keys.delete", $"key/{keyId}", ct);
        return Results.NoContent();
    }

    // ---- platforms ----------------------------------------------------------------------------

    private static readonly string[] PlatformTypes = ["web", "flutter-android", "flutter-ios"];

    private static async Task<IResult> ListPlatforms(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var platforms = await db.Platforms
            .Where(p => p.ProjectId == project.Id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new PlatformListResponse(platforms.Count, [.. platforms.Select(PlatformResponse.From)]));
    }

    private static async Task<IResult> CreatePlatform(
        CreatePlatformRequest req, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fields = new Dictionary<string, string[]>();
        if (!PlatformTypes.Contains(req.Type))
            fields["type"] = [$"Must be one of: {string.Join(", ", PlatformTypes)}."];
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];
        // Web platforms feed the CORS/redirect allowlist, so the hostname is required there.
        var hostname = req.Hostname?.Trim().ToLowerInvariant();
        if (req.Type == "web" && string.IsNullOrWhiteSpace(hostname))
            fields["hostname"] = ["Required for web platforms."];
        if (hostname is { Length: > 256 } ||
            (hostname is not null && Uri.CheckHostName(hostname.StartsWith("*.") ? hostname[2..] : hostname)
                == UriHostNameType.Unknown))
            fields["hostname"] = ["Must be a valid hostname (a leading '*.' wildcard is allowed)."];
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid platform payload.", fields);

        var platform = new Platform
        {
            Id = Ids.NewUuid(),
            ProjectId = project.Id,
            Type = req.Type,
            Name = req.Name.Trim(),
            Hostname = hostname,
        };
        db.Platforms.Add(platform);
        await AuditAsync(db, http, project.Id, "platforms.create", $"platform/{Ids.Wire(platform.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/platforms/{Ids.Wire(platform.Id)}",
            PlatformResponse.From(platform));
    }

    private static async Task<IResult> DeletePlatform(
        string platformId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        if (!Ids.TryParseWire(platformId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.PlatformNotFound, "Platform not found.");
        var deleted = await db.Platforms
            .Where(p => p.Id == parsed && p.ProjectId == project.Id)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw PraxyException.NotFound(ErrorTypes.PlatformNotFound, "Platform not found.");
        await AuditAsync(db, http, project.Id, "platforms.delete", $"platform/{platformId}", ct);
        return Results.NoContent();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<User> FindUserAsync(PraxyDb db, string projectId, string userId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(userId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");
        return await db.Users.FirstOrDefaultAsync(u => u.Id == parsed && u.ProjectId == projectId, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");
    }

    private static async Task<Team> FindTeamAsync(
        TeamsService teams, string projectId, string teamId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(teamId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.TeamNotFound, "Team not found.");
        return await teams.GetTeamAsync(projectId, parsed, ct);
    }

    /// <summary>Console admin actions are the audit log's bread and butter — record them all.</summary>
    private static async Task AuditAsync(
        PraxyDb db, HttpContext http, string projectId, string action, string resource, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Actor = $"admin:{op.Account.Id}",
            Action = action,
            Resource = resource,
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }
}
