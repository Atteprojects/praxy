using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth;

public sealed record OrganizationMemberWithAccount(OrganizationMember Member, ConsoleAccount Account);

/// <summary>
/// Organization membership — invite by email, accept, remove, and change role — plus the one
/// owner-role check every owner-only action goes through
/// (organizations-phase-2-prompt.md's own "put it in exactly one place"). Mirrors
/// <see cref="TeamsService"/>'s invite shape (<see cref="Secrets"/>, an unconfirmed row, an accept
/// endpoint that verifies the secret) as a *pattern* — Organizations and Teams are unrelated
/// resources (console operators vs. app users) and share no code path, error type, or database
/// row, per docs/research/organizations.md's own warning.
/// </summary>
public sealed class OrganizationsService(PraxyDb db, ConsoleAuthService auth, IPasswordHasher hasher, IEmailSender emailSender)
{
    private static readonly string[] ValidRoles = ["owner", "member"];

    /// <summary>
    /// The one place the owner-role check lives. Read access (list members, see an org in a list)
    /// stays membership-only by design (docs/research/organizations.md: "member uses the projects
    /// inside it") — this is only for the owner-only actions: rename/delete the org, invite, remove
    /// a member, change a role.
    /// </summary>
    public async Task RequireOwnerAsync(Guid organizationId, Guid operatorId, CancellationToken ct)
    {
        var role = await db.OrganizationMembers
            .Where(m => m.OrganizationId == organizationId && m.UserId == operatorId && m.Confirmed)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);
        if (role != "owner")
            throw new PraxyException(403, ErrorTypes.OrganizationOwnerRequired,
                "This action requires the organization's owner role.");
    }

    public async Task<List<OrganizationMemberWithAccount>> ListMembersAsync(Guid organizationId, CancellationToken ct) =>
        (await (
            from m in db.OrganizationMembers
            join u in db.Users on m.UserId equals u.Id
            where m.OrganizationId == organizationId
            orderby m.CreatedAt
            select new { m, u }).ToListAsync(ct))
        .Select(x => new OrganizationMemberWithAccount(x.m, ToAccount(x.u))).ToList();

    /// <summary>
    /// Resolves (or creates, passwordless — mirroring <c>TeamsService.ResolveTargetUserAsync</c>)
    /// the invited operator account, records an unconfirmed row with a hashed secret, and emails an
    /// acceptance link built from <paramref name="url"/> (the console's own origin — there is no
    /// per-project platform allowlist to validate against here, since organization membership has
    /// nothing to do with any developer project; the caller must already hold the org's owner role
    /// to reach this, same trust boundary Teams' own session-invite path relies on for its <c>url</c>).
    /// One <see cref="PraxyDb.SaveChangesAsync"/> for both the (possibly new) user row and the
    /// membership row, so a failure never leaves an orphaned passwordless account with no invite.
    /// </summary>
    public async Task<OrganizationMemberWithAccount> InviteAsync(
        Organization organization, string email, string? role, string url, CancellationToken ct)
    {
        var resolvedRole = string.IsNullOrWhiteSpace(role) ? "member" : role;
        ValidateRole(resolvedRole);

        var normalized = ConsoleAuthService.NormalizeEmail(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.ProjectId == Ids.ConsoleProjectId && u.Email == normalized, ct);
        var isNewAccount = user is null;
        user ??= new User
        {
            Id = Ids.NewUuid(),
            ProjectId = Ids.ConsoleProjectId,
            Email = normalized,
            PasswordHash = null,
            Name = "",
            EmailVerified = false,
        };

        if (!isNewAccount &&
            await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == organization.Id && m.UserId == user.Id, ct))
            throw new PraxyException(409, ErrorTypes.OrganizationMembershipAlreadyExists,
                "This person is already a member of (or invited to) this organization.");

        if (isNewAccount)
            db.Users.Add(user);

        var (secret, secretHash) = Secrets.Generate();
        var member = new OrganizationMember
        {
            OrganizationId = organization.Id,
            UserId = user.Id,
            Role = resolvedRole,
            Confirmed = false,
            SecretHash = secretHash,
            InvitedAt = DateTimeOffset.UtcNow,
        };
        db.OrganizationMembers.Add(member);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Two invites for the same never-before-seen email racing to create the account, or a
            // second invite into the same org landing between this method's own existence check
            // and its save — either way the row now exists, so this is the same 409 a sequential
            // caller would have gotten.
            throw new PraxyException(409, ErrorTypes.OrganizationMembershipAlreadyExists,
                "This person is already a member of (or invited to) this organization.");
        }

        var separator = url.Contains('?') ? '&' : '?';
        var link = $"{url}{separator}organizationId={Ids.Wire(organization.Id)}&userId={Ids.Wire(user.Id)}" +
                   $"&secret={Uri.EscapeDataString(secret)}";
        await emailSender.SendAsync(new EmailMessage(
            user.Email,
            $"You have been invited to join {organization.Name}",
            $"Follow this link to join \"{organization.Name}\" on Praxy:\n\n{link}\n\n" +
            "If you did not expect this invitation, you can ignore this message."), ct);

        return new(member, ToAccount(user));
    }

    /// <summary>
    /// Accept an invite by secret — no session required, same posture as Teams' own membership
    /// accept. Scoped by organization id *and* user id in one query (never an invite id alone with
    /// an org check applied afterward — security-review-phase-3 Finding D's cross-project
    /// id-confusion theme, applied here to invites), and a wrong secret gets the exact same error as
    /// a nonexistent invite, timing included: the hash comparison always runs, against a dummy value
    /// when there is no real row, mirroring <see cref="ConsoleAuthService.LoginAsync"/>'s own
    /// dummy-hash burn. An already-*confirmed* row is a different, more specific error — the
    /// invitee legitimately re-using their own link is not the enumeration case this is guarding
    /// against (mirrors Teams' own <c>AcceptInvitationAsync</c>, which checks confirmed-state before
    /// the secret for the same reason).
    /// </summary>
    public async Task<(OrganizationMemberWithAccount Member, ConsoleSession Session)> AcceptInviteAsync(
        Guid organizationId, Guid userId, string secret, string? password,
        string? ip, string? userAgent, CancellationToken ct)
    {
        var member = await db.OrganizationMembers.FirstOrDefaultAsync(
            m => m.OrganizationId == organizationId && m.UserId == userId, ct);

        if (member is not null && member.Confirmed)
            throw new PraxyException(409, ErrorTypes.OrganizationInviteAlreadyAccepted,
                "This invitation has already been accepted.");

        var candidateHash = Secrets.Hash(secret);
        if (member is null || !Secrets.HashEquals(member.SecretHash, candidateHash))
        {
            Secrets.HashEquals(DummyHash.Value, candidateHash);
            throw new PraxyException(401, ErrorTypes.OrganizationInviteInvalid, "Invalid or expired invitation.");
        }

        var user = await db.Users.FirstAsync(u => u.Id == userId && u.ProjectId == Ids.ConsoleProjectId, ct);
        if (!user.Status)
            throw new PraxyException(401, ErrorTypes.UserBlocked, "This account has been blocked.");

        // Only an account the invite itself created (still passwordless) needs one set here — an
        // existing operator invited into a second organization already has one and keeps it.
        if (user.PasswordHash is null)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || password.Length > 256)
                throw PraxyException.ArgumentInvalid("A password is required to accept this invitation.",
                    new Dictionary<string, string[]> { ["password"] = ["Must be between 8 and 256 characters."] });
            user.PasswordHash = hasher.Hash(password);
        }
        // Accepting an invitation proves control of the invited mailbox — same reasoning as Teams'.
        user.EmailVerified = true;
        db.Users.Update(user);

        member.Confirmed = true;
        member.SecretHash = null;
        db.OrganizationMembers.Update(member);
        await db.SaveChangesAsync(ct);

        var session = await auth.CreateOperatorSessionAsync(user, ip, userAgent, ct);
        return (new(member, ToAccount(user)), session);
    }

    public async Task<OrganizationMemberWithAccount> ChangeRoleAsync(
        Guid organizationId, Guid targetUserId, string role, CancellationToken ct)
    {
        ValidateRole(role);
        var (member, user) = await RequireMemberAsync(organizationId, targetUserId, ct);

        // A pending (unconfirmed) invite never counted toward the owner count in the first place,
        // so changing its role can never be the move that removes the organization's last owner.
        if (member.Confirmed && member.Role == "owner" && role != "owner" &&
            await ConfirmedOwnerCountAsync(organizationId, ct) <= 1)
            throw LastOwner();

        member.Role = role;
        await db.SaveChangesAsync(ct);
        return new(member, ToAccount(user));
    }

    public async Task RemoveMemberAsync(Guid organizationId, Guid targetUserId, CancellationToken ct)
    {
        var (member, _) = await RequireMemberAsync(organizationId, targetUserId, ct);

        if (member.Confirmed && member.Role == "owner" && await ConfirmedOwnerCountAsync(organizationId, ct) <= 1)
            throw LastOwner();

        db.OrganizationMembers.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    private async Task<(OrganizationMember Member, User User)> RequireMemberAsync(
        Guid organizationId, Guid userId, CancellationToken ct)
    {
        var row = await (
            from m in db.OrganizationMembers
            join u in db.Users on m.UserId equals u.Id
            where m.OrganizationId == organizationId && m.UserId == userId
            select new { m, u }).FirstOrDefaultAsync(ct);
        if (row is null)
            throw PraxyException.NotFound(ErrorTypes.OrganizationMembershipNotFound, "Membership not found.");
        return (row.m, row.u);
    }

    /// <summary>
    /// Confirmed owners only — the count the last-owner guard reasons about. Excludes pending
    /// owner-role invites deliberately: they might never be accepted, so they cannot be what stands
    /// between the organization and having zero owners.
    /// </summary>
    private Task<int> ConfirmedOwnerCountAsync(Guid organizationId, CancellationToken ct) =>
        db.OrganizationMembers.CountAsync(m => m.OrganizationId == organizationId && m.Role == "owner" && m.Confirmed, ct);

    private static PraxyException LastOwner() =>
        new(409, ErrorTypes.OrganizationLastOwner, "The organization must always have at least one owner.");

    private static void ValidateRole(string role)
    {
        if (!ValidRoles.Contains(role))
            throw PraxyException.ArgumentInvalid("Invalid role.",
                new Dictionary<string, string[]> { ["role"] = ["Must be 'owner' or 'member'."] });
    }

    private static ConsoleAccount ToAccount(User u) => new(u.Id, u.Email, u.Name, u.CreatedAt);

    /// <summary>A syntactically valid hash of an unguessable value, for timing-flattened invite acceptance.</summary>
    private static class DummyHash
    {
        public static readonly string Value = Secrets.Hash(Guid.NewGuid().ToString("n"));
    }
}
