using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth;

public sealed record MembershipWithUser(Membership Membership, User User);

/// <summary>
/// Teams and memberships with Appwrite's battle-tested semantics: a client (session) call
/// emails an invitation, a server/console call adds the member directly, and acceptance
/// auto-creates a session for the invitee.
/// </summary>
public sealed class TeamsService(PraxyDb db, AppAuthService auth, IEventBus bus, IEmailSender email)
{
    public async Task<Team> CreateTeamAsync(
        Project project, User? creator, string name, string[] creatorRoles, CancellationToken ct = default)
    {
        ValidateTeamName(name);
        ValidateRoles(creatorRoles);

        var team = new Team { Id = Ids.NewUuid(), ProjectId = project.Id, Name = name.Trim() };
        db.Teams.Add(team);

        // A user creating a team becomes its first confirmed member — owner unless stated otherwise.
        if (creator is not null)
        {
            db.Memberships.Add(new Membership
            {
                Id = Ids.NewUuid(),
                TeamId = team.Id,
                UserId = creator.Id,
                Roles = creatorRoles.Length > 0 ? creatorRoles : ["owner"],
                Confirmed = true,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(project.Id, $"teams.{Ids.Wire(team.Id)}.create", [$"team:{Ids.Wire(team.Id)}"], ct);
        return team;
    }

    public async Task<Team> GetTeamAsync(string projectId, Guid teamId, CancellationToken ct = default) =>
        await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.TeamNotFound, "Team not found.");

    /// <summary>Sessions see the teams they belong to; API keys and the console see all of them.</summary>
    public async Task<List<Team>> ListTeamsAsync(string projectId, Guid? memberUserId, CancellationToken ct = default)
    {
        var query = db.Teams.Where(t => t.ProjectId == projectId);
        if (memberUserId is { } userId)
            query = query.Where(t => db.Memberships.Any(
                m => m.TeamId == t.Id && m.UserId == userId && m.Confirmed));
        return await query.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
    }

    public async Task<Team> UpdateTeamNameAsync(Team team, string name, CancellationToken ct = default)
    {
        ValidateTeamName(name);
        team.Name = name.Trim();
        team.UpdatedAt = DateTimeOffset.UtcNow;
        db.Teams.Update(team);
        await db.SaveChangesAsync(ct);
        await PublishAsync(team.ProjectId, $"teams.{Ids.Wire(team.Id)}.update", [$"team:{Ids.Wire(team.Id)}"], ct);
        return team;
    }

    public async Task DeleteTeamAsync(Team team, CancellationToken ct = default)
    {
        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct);
        await PublishAsync(team.ProjectId, $"teams.{Ids.Wire(team.Id)}.delete", [$"team:{Ids.Wire(team.Id)}"], ct);
    }

    /// <summary>True when the user can manage the team: any confirmed membership holding <c>owner</c>.</summary>
    public async Task<bool> IsTeamOwnerAsync(Guid teamId, Guid userId, CancellationToken ct = default) =>
        await db.Memberships.AnyAsync(
            m => m.TeamId == teamId && m.UserId == userId && m.Confirmed && m.Roles.Contains("owner"), ct);

    public async Task<bool> IsTeamMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default) =>
        await db.Memberships.AnyAsync(m => m.TeamId == teamId && m.UserId == userId && m.Confirmed, ct);

    // ---- memberships -------------------------------------------------------------------------

    /// <summary>
    /// Invitation (client semantics): resolves or creates the target user, records an
    /// unconfirmed membership, and emails an acceptance link to <paramref name="url"/> —
    /// which must pass the platform allowlist.
    /// </summary>
    public async Task<MembershipWithUser> InviteMemberAsync(
        Project project, Team team, string? emailAddress, Guid? userId, string[] roles, string url,
        CancellationToken ct = default)
    {
        await auth.ValidateRedirectUrlAsync(project, url, "url", ct);
        ValidateRoles(roles);
        var user = await ResolveTargetUserAsync(project, emailAddress, userId, createIfMissing: true, ct);

        await EnsureNotMemberAsync(team.Id, user.Id, ct);
        var (secret, secretHash) = Secrets.Generate();
        var membership = new Membership
        {
            Id = Ids.NewUuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Roles = roles,
            Confirmed = false,
            SecretHash = secretHash,
            InvitedAt = DateTimeOffset.UtcNow,
        };
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);

        var separator = url.Contains('?') ? '&' : '?';
        var link = $"{url}{separator}teamId={Ids.Wire(team.Id)}&membershipId={Ids.Wire(membership.Id)}" +
                   $"&userId={Ids.Wire(user.Id)}&secret={Uri.EscapeDataString(secret)}";
        await email.SendAsync(new EmailMessage(
            user.Email,
            $"You have been invited to join {team.Name}",
            $"Follow this link to join the team \"{team.Name}\" on {project.Name}:\n\n{link}\n\n" +
            "If you did not expect this invitation, you can ignore this message."), ct);

        await PublishMembershipAsync(project.Id, team.Id, membership, user.Id, "create", ct);
        return new MembershipWithUser(membership, user);
    }

    /// <summary>Direct add (server/console semantics): membership is confirmed immediately, no email.</summary>
    public async Task<MembershipWithUser> AddMemberAsync(
        Project project, Team team, string? emailAddress, Guid? userId, string[] roles,
        CancellationToken ct = default)
    {
        ValidateRoles(roles);
        var user = await ResolveTargetUserAsync(project, emailAddress, userId, createIfMissing: true, ct);

        await EnsureNotMemberAsync(team.Id, user.Id, ct);
        var membership = new Membership
        {
            Id = Ids.NewUuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Roles = roles,
            Confirmed = true,
            JoinedAt = DateTimeOffset.UtcNow,
        };
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);
        await PublishMembershipAsync(project.Id, team.Id, membership, user.Id, "create", ct);
        return new MembershipWithUser(membership, user);
    }

    /// <summary>Acceptance — and per Appwrite semantics, the invitee walks away signed in.</summary>
    public async Task<(MembershipWithUser Membership, AppSession Session)> AcceptInvitationAsync(
        Project project, Guid teamId, Guid membershipId, Guid userId, string secret,
        string? ip, string? userAgent, CancellationToken ct = default)
    {
        var membership = await db.Memberships.FirstOrDefaultAsync(
                m => m.Id == membershipId && m.TeamId == teamId, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.MembershipNotFound, "Membership not found.");
        var team = await GetTeamAsync(project.Id, teamId, ct);

        if (membership.Confirmed)
            throw new PraxyException(409, ErrorTypes.MembershipAlreadyConfirmed,
                "This invitation has already been accepted.");
        if (membership.UserId != userId || !Secrets.HashEquals(membership.SecretHash, Secrets.Hash(secret)))
            throw new PraxyException(401, ErrorTypes.TeamInvalidSecret, "Invalid invitation secret.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.ProjectId == project.Id, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");
        if (!user.Status)
            throw new PraxyException(401, ErrorTypes.UserBlocked, "This account has been blocked.");

        membership.Confirmed = true;
        membership.SecretHash = null;
        membership.JoinedAt = DateTimeOffset.UtcNow;
        db.Memberships.Update(membership);
        // Accepting an invitation proves control of the invited mailbox.
        if (!user.EmailVerified)
        {
            user.EmailVerified = true;
            db.Users.Update(user);
        }
        await db.SaveChangesAsync(ct);

        await PublishMembershipAsync(project.Id, team.Id, membership, user.Id, "update.status", ct);
        var session = await auth.CreateSessionAsync(project, user, "invite", ip, userAgent, ct);
        return (new MembershipWithUser(membership, user), session);
    }

    public async Task<List<MembershipWithUser>> ListMembershipsAsync(Guid teamId, CancellationToken ct = default) =>
        (await (
            from m in db.Memberships
            join u in db.Users on m.UserId equals u.Id
            where m.TeamId == teamId
            orderby m.CreatedAt
            select new { m, u }).ToListAsync(ct))
        .Select(x => new MembershipWithUser(x.m, x.u)).ToList();

    public async Task<List<MembershipWithUser>> ListUserMembershipsAsync(Guid userId, CancellationToken ct = default) =>
        (await (
            from m in db.Memberships
            join u in db.Users on m.UserId equals u.Id
            where m.UserId == userId
            orderby m.CreatedAt
            select new { m, u }).ToListAsync(ct))
        .Select(x => new MembershipWithUser(x.m, x.u)).ToList();

    public async Task<Membership> GetMembershipAsync(Guid teamId, Guid membershipId, CancellationToken ct = default) =>
        await db.Memberships.FirstOrDefaultAsync(m => m.Id == membershipId && m.TeamId == teamId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.MembershipNotFound, "Membership not found.");

    public async Task<Membership> UpdateMembershipRolesAsync(
        string projectId, Guid teamId, Membership membership, string[] roles, CancellationToken ct = default)
    {
        ValidateRoles(roles);
        membership.Roles = roles;
        db.Memberships.Update(membership);
        await db.SaveChangesAsync(ct);
        await PublishMembershipAsync(projectId, teamId, membership, membership.UserId, "update", ct);
        return membership;
    }

    public async Task DeleteMembershipAsync(string projectId, Membership membership, CancellationToken ct = default)
    {
        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);
        await PublishMembershipAsync(projectId, membership.TeamId, membership, membership.UserId, "delete", ct);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Identifier precedence userId &gt; email (Appwrite semantics). Inviting an unknown email creates the user.</summary>
    private async Task<User> ResolveTargetUserAsync(
        Project project, string? emailAddress, Guid? userId, bool createIfMissing, CancellationToken ct)
    {
        if (userId is { } id)
            return await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.ProjectId == project.Id, ct)
                ?? throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");

        if (string.IsNullOrWhiteSpace(emailAddress))
            throw PraxyException.ArgumentInvalid("Provide a userId or an email.",
                new Dictionary<string, string[]> { ["email"] = ["Provide a userId or an email."] });

        var normalized = AppAuthService.NormalizeEmail(emailAddress);
        var existing = await db.Users.FirstOrDefaultAsync(
            u => u.ProjectId == project.Id && u.Email == normalized, ct);
        if (existing is not null)
            return existing;
        if (!createIfMissing)
            throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");

        // Invited-by-email users start passwordless and unverified; acceptance verifies them.
        return await auth.CreateUserAsync(project, normalized, password: null, name: "", emailVerified: false, ct);
    }

    private async Task EnsureNotMemberAsync(Guid teamId, Guid userId, CancellationToken ct)
    {
        if (await db.Memberships.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct))
            throw new PraxyException(409, ErrorTypes.MembershipAlreadyExists,
                "This user is already a member of (or invited to) this team.");
    }

    private static void ValidateTeamName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            throw PraxyException.ArgumentInvalid("Invalid team payload.",
                new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });
    }

    private static void ValidateRoles(string[] roles)
    {
        if (roles.Length > 32 || roles.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 32 || r.Contains('/')))
            throw PraxyException.ArgumentInvalid("Invalid roles.",
                new Dictionary<string, string[]>
                {
                    ["roles"] = ["At most 32 roles of at most 32 characters, no '/' allowed."],
                });
    }

    private async ValueTask PublishMembershipAsync(
        string projectId, Guid teamId, Membership membership, Guid userId, string action, CancellationToken ct)
    {
        await PublishAsync(projectId,
            $"teams.{Ids.Wire(teamId)}.memberships.{Ids.Wire(membership.Id)}.{action}",
            [$"team:{Ids.Wire(teamId)}", $"user:{Ids.Wire(userId)}"], ct);
    }

    private async ValueTask PublishAsync(string projectId, string type, string[] permissions, CancellationToken ct)
    {
        await bus.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, projectId, type, permissions, null), ct);
    }
}
