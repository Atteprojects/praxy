using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record CreateTeamRequest(string Name, string[]? Roles);

public sealed record UpdateTeamRequest(string Name);

public sealed record CreateMembershipRequest(string? Email, string? UserId, string[]? Roles, string? Url);

public sealed record UpdateMembershipRolesRequest(string[] Roles);

public sealed record AcceptMembershipRequest(string UserId, string Secret);

/// <summary>
/// Data-plane teams API. Sessions act with membership-based authorization; API keys act with
/// scopes. Membership creation splits on the caller per Appwrite semantics: sessions email an
/// invitation, keys add the member directly.
/// </summary>
public static class TeamEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var teams = api.MapGroup("/v1/teams")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>();

        teams.MapPost("", Create).Produces<TeamResponse>(StatusCodes.Status201Created);
        teams.MapGet("", List).Produces<TeamListResponse>();
        teams.MapGet("/{teamId}", Get).Produces<TeamResponse>();
        teams.MapPatch("/{teamId}", Update).Produces<TeamResponse>();
        teams.MapDelete("/{teamId}", Delete).Produces(StatusCodes.Status204NoContent);

        teams.MapPost("/{teamId}/memberships", CreateMembership)
            .Produces<MembershipResponse>(StatusCodes.Status201Created);
        teams.MapGet("/{teamId}/memberships", ListMemberships).Produces<MembershipListResponse>();
        teams.MapPatch("/{teamId}/memberships/{membershipId}", UpdateMembershipRoles)
            .Produces<MembershipResponse>();
        teams.MapPatch("/{teamId}/memberships/{membershipId}/status", AcceptMembership)
            .RequireRateLimiting("auth")
            .Produces<AcceptedMembershipResponse>();
        teams.MapDelete("/{teamId}/memberships/{membershipId}", DeleteMembership)
            .Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<IResult> Create(
        CreateTeamRequest req, HttpContext http, TeamsService teams, PraxyDb db, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var (user, _) = AppPrincipalFilter.RequireUserOrScope(http, ApiKeyScopes.TeamsWrite);
        var team = await teams.CreateTeamAsync(project, user?.User, req.Name, req.Roles ?? [], ct);
        return Results.Created($"/v1/teams/{Ids.Wire(team.Id)}",
            TeamResponse.From(team, user is null ? 0 : 1));
    }

    private static async Task<IResult> List(HttpContext http, TeamsService teams, PraxyDb db, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var (user, _) = AppPrincipalFilter.RequireUserOrScope(http, ApiKeyScopes.TeamsRead);
        var list = await teams.ListTeamsAsync(project.Id, user?.User.Id, ct);
        var counts = await MemberCountsAsync(db, list.Select(t => t.Id), ct);
        return Results.Ok(new TeamListResponse(
            list.Count, [.. list.Select(t => TeamResponse.From(t, counts.GetValueOrDefault(t.Id)))]));
    }

    private static async Task<IResult> Get(
        string teamId, HttpContext http, TeamsService teams, PraxyDb db, CancellationToken ct)
    {
        var (team, _) = await RequireTeamAccessAsync(teamId, http, teams, ApiKeyScopes.TeamsRead, ct);
        var counts = await MemberCountsAsync(db, [team.Id], ct);
        return Results.Ok(TeamResponse.From(team, counts.GetValueOrDefault(team.Id)));
    }

    private static async Task<IResult> Update(
        string teamId, UpdateTeamRequest req, HttpContext http, TeamsService teams, PraxyDb db, CancellationToken ct)
    {
        var (team, _) = await RequireTeamAccessAsync(teamId, http, teams, ApiKeyScopes.TeamsWrite, ct, ownerOnly: true);
        await teams.UpdateTeamNameAsync(team, req.Name, ct);
        var counts = await MemberCountsAsync(db, [team.Id], ct);
        return Results.Ok(TeamResponse.From(team, counts.GetValueOrDefault(team.Id)));
    }

    private static async Task<IResult> Delete(
        string teamId, HttpContext http, TeamsService teams, CancellationToken ct)
    {
        var (team, _) = await RequireTeamAccessAsync(teamId, http, teams, ApiKeyScopes.TeamsWrite, ct, ownerOnly: true);
        await teams.DeleteTeamAsync(team, ct);
        return Results.NoContent();
    }

    // ---- memberships ---------------------------------------------------------------------------

    private static async Task<IResult> CreateMembership(
        string teamId, CreateMembershipRequest req, HttpContext http, TeamsService teams, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var (team, user) = await RequireTeamAccessAsync(teamId, http, teams, ApiKeyScopes.TeamsWrite, ct, ownerOnly: true);

        Guid? targetUserId = null;
        if (req.UserId is not null)
        {
            if (!Ids.TryParseWire(req.UserId, out var parsed))
                throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");
            targetUserId = parsed;
        }

        MembershipWithUser membership;
        if (user is not null)
        {
            // Client semantics: the invite must carry a redirect URL for the acceptance email.
            if (string.IsNullOrEmpty(req.Url))
                throw PraxyException.ArgumentInvalid("The 'url' field is required when inviting via a session.",
                    new Dictionary<string, string[]> { ["url"] = ["Required for client-side invitations."] });
            membership = await teams.InviteMemberAsync(
                project, team, req.Email, targetUserId, req.Roles ?? [], req.Url, ct);
        }
        else
        {
            membership = await teams.AddMemberAsync(project, team, req.Email, targetUserId, req.Roles ?? [], ct);
        }

        return Results.Created(
            $"/v1/teams/{Ids.Wire(team.Id)}/memberships/{Ids.Wire(membership.Membership.Id)}",
            MembershipResponse.From(membership));
    }

    private static async Task<IResult> ListMemberships(
        string teamId, HttpContext http, TeamsService teams, CancellationToken ct)
    {
        var (team, _) = await RequireTeamAccessAsync(teamId, http, teams, ApiKeyScopes.TeamsRead, ct);
        var memberships = await teams.ListMembershipsAsync(team.Id, ct);
        return Results.Ok(new MembershipListResponse(
            memberships.Count, [.. memberships.Select(MembershipResponse.From)]));
    }

    private static async Task<IResult> UpdateMembershipRoles(
        string teamId, string membershipId, UpdateMembershipRolesRequest req, HttpContext http,
        TeamsService teams, PraxyDb db, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var (team, _) = await RequireTeamAccessAsync(teamId, http, teams, ApiKeyScopes.TeamsWrite, ct, ownerOnly: true);
        var membership = await GetMembershipAsync(teams, team.Id, membershipId, ct);
        await teams.UpdateMembershipRolesAsync(project.Id, team.Id, membership, req.Roles, ct);
        var user = await db.Users.FirstAsync(u => u.Id == membership.UserId, ct);
        return Results.Ok(MembershipResponse.From(new MembershipWithUser(membership, user)));
    }

    /// <summary>Invitation acceptance — authenticated by the emailed secret, not by a session.</summary>
    private static async Task<IResult> AcceptMembership(
        string teamId, string membershipId, AcceptMembershipRequest req, HttpContext http,
        TeamsService teams, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        if (!Ids.TryParseWire(teamId, out var parsedTeamId))
            throw PraxyException.NotFound(ErrorTypes.TeamNotFound, "Team not found.");
        if (!Ids.TryParseWire(membershipId, out var parsedMembershipId))
            throw PraxyException.NotFound(ErrorTypes.MembershipNotFound, "Membership not found.");
        if (!Ids.TryParseWire(req.UserId, out var userId))
            throw new PraxyException(401, ErrorTypes.TeamInvalidSecret, "Invalid invitation secret.");

        var (membership, session) = await teams.AcceptInvitationAsync(
            project, parsedTeamId, parsedMembershipId, userId, req.Secret,
            http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent, ct);

        AppSessionCookie.Set(http, project.Id, session.Token, session.Session.ExpiresAt);
        return Results.Ok(new AcceptedMembershipResponse(
            MembershipResponse.From(membership), CreatedSessionResponse.From(session)));
    }

    private static async Task<IResult> DeleteMembership(
        string teamId, string membershipId, HttpContext http, TeamsService teams, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var principal = AppPrincipalFilter.Current(http);
        if (!Ids.TryParseWire(teamId, out var parsedTeamId))
            throw PraxyException.NotFound(ErrorTypes.TeamNotFound, "Team not found.");
        var team = await teams.GetTeamAsync(project.Id, parsedTeamId, ct);
        var membership = await GetMembershipAsync(teams, team.Id, membershipId, ct);

        // Self-removal is always allowed; otherwise it takes a team owner or a scoped key.
        if (principal is RequestPrincipal.AppUser(var user, _))
        {
            if (membership.UserId != user.Id && !await teams.IsTeamOwnerAsync(team.Id, user.Id, ct))
                throw PraxyException.Unauthorized("Only team owners can remove other members.");
        }
        else
        {
            AppPrincipalFilter.RequireScope(http, ApiKeyScopes.TeamsWrite);
        }

        await teams.DeleteMembershipAsync(project.Id, membership, ct);
        return Results.NoContent();
    }

    // ---- shared access rules -------------------------------------------------------------------

    /// <summary>
    /// Resolves the team and enforces access: keys need the given scope; sessions need
    /// membership (read) or the <c>owner</c> role (<paramref name="ownerOnly"/>).
    /// </summary>
    private static async Task<(Team Team, RequestPrincipal.AppUser? User)> RequireTeamAccessAsync(
        string teamId, HttpContext http, TeamsService teams, string keyScope, CancellationToken ct,
        bool ownerOnly = false)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var (user, _) = AppPrincipalFilter.RequireUserOrScope(http, keyScope);
        if (!Ids.TryParseWire(teamId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.TeamNotFound, "Team not found.");
        var team = await teams.GetTeamAsync(project.Id, parsed, ct);

        if (user is not null)
        {
            var allowed = ownerOnly
                ? await teams.IsTeamOwnerAsync(team.Id, user.User.Id, ct)
                : await teams.IsTeamMemberAsync(team.Id, user.User.Id, ct);
            if (!allowed)
                throw PraxyException.Unauthorized(ownerOnly
                    ? "This action requires the team's owner role."
                    : "You are not a member of this team.");
        }
        return (team, user);
    }

    private static async Task<Membership> GetMembershipAsync(
        TeamsService teams, Guid teamId, string membershipId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(membershipId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.MembershipNotFound, "Membership not found.");
        return await teams.GetMembershipAsync(teamId, parsed, ct);
    }

    private static async Task<Dictionary<Guid, int>> MemberCountsAsync(
        PraxyDb db, IEnumerable<Guid> teamIds, CancellationToken ct) =>
        await db.Memberships
            .Where(m => teamIds.Contains(m.TeamId) && m.Confirmed)
            .GroupBy(m => m.TeamId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}
