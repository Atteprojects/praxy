using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Tables.Quotas;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

/// <summary><c>Role</c> is the caller's own role in this organization — not a property of the organization itself.</summary>
public sealed record OrganizationResponse(string Id, string Name, string Role, DateTimeOffset CreatedAt)
{
    public static OrganizationResponse From(Organization o, string role) => new(Ids.Wire(o.Id), o.Name, role, o.CreatedAt);
}

public sealed record OrganizationListResponse(int Total, IReadOnlyList<OrganizationResponse> Organizations);

public sealed record CreateOrganizationRequest(string Name);

public sealed record UpdateOrganizationRequest(string Name);

public sealed record OrganizationMemberResponse(
    string UserId, string Email, string Name, string Role, bool Confirmed,
    DateTimeOffset? InvitedAt, DateTimeOffset CreatedAt)
{
    // UserId matches ConsoleAccount.Id's own (unconverted, dashed) Guid serialization — not
    // Ids.Wire — so the console can compare a member row against /console/account's own id
    // directly, string-for-string, to know "is this row me" without a second lookup.
    public static OrganizationMemberResponse From(OrganizationMemberWithAccount m) => new(
        m.Member.UserId.ToString(), m.Account.Email, m.Account.Name, m.Member.Role, m.Member.Confirmed,
        m.Member.InvitedAt, m.Member.CreatedAt);
}

public sealed record OrganizationMemberListResponse(int Total, IReadOnlyList<OrganizationMemberResponse> Members);

public sealed record InviteOrganizationMemberRequest(string Email, string? Role, string Url);

public sealed record UpdateOrganizationMemberRoleRequest(string Role);

public sealed record AcceptOrganizationInviteRequest(string Secret, string? Password);

public sealed record AcceptedOrganizationInviteResponse(
    OrganizationMemberResponse Member, ConsoleAccount Account, ConsoleSessionResponse Session);

/// <summary>
/// Organization lifecycle for the console: create, rename, delete, membership (invite by email,
/// accept, remove, change role), and the read surface the home screen resolves to build the
/// org-scoped URL. Organizations-phase-2 turns on <see cref="OrganizationMember.Role"/> for the
/// first time — <c>owner</c> manages the org itself (rename, delete, invite, remove, change roles);
/// <c>member</c> uses the projects inside it. The owner-role check lives in exactly one place,
/// <see cref="OrganizationsService.RequireOwnerAsync"/>, called from every owner-only action below.
/// </summary>
public static class ConsoleOrganizationEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var organizations = api.MapGroup("/v1/console/organizations")
            .AddEndpointFilter<RequireOperatorFilter>();

        organizations.MapGet("", List).Produces<OrganizationListResponse>();
        organizations.MapGet("/{organizationId}", Get).Produces<OrganizationResponse>();
        organizations.MapPost("", Create).Produces<OrganizationResponse>(StatusCodes.Status201Created);
        organizations.MapPatch("/{organizationId}", Update).Produces<OrganizationResponse>();
        organizations.MapDelete("/{organizationId}", Delete).Produces(StatusCodes.Status204NoContent);

        organizations.MapGet("/{organizationId}/members", ListMembers).Produces<OrganizationMemberListResponse>();
        organizations.MapPost("/{organizationId}/members", InviteMember)
            .RequireRateLimiting("auth-email")
            .Produces<OrganizationMemberResponse>(StatusCodes.Status201Created);
        organizations.MapPatch("/{organizationId}/members/{userId}", UpdateMemberRole)
            .Produces<OrganizationMemberResponse>();
        organizations.MapDelete("/{organizationId}/members/{userId}", RemoveMember)
            .Produces(StatusCodes.Status204NoContent);

        // Public — no session required to accept, same posture as Teams' own membership accept.
        // The secret is the credential here, not a cookie, and it is validated inside AcceptInvite.
        api.MapPost("/v1/console/organizations/{organizationId}/members/{userId}/accept", AcceptInvite)
            .RequireRateLimiting("auth")
            .Produces<AcceptedOrganizationInviteResponse>(StatusCodes.Status201Created);
    }

    private static async Task<IResult> List(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var list = await AccessibleOrganizationsAsync(db, op.Account.Id, ct);
        return Results.Ok(new OrganizationListResponse(
            list.Count, [.. list.Select(x => OrganizationResponse.From(x.Organization, x.Role))]));
    }

    private static async Task<IResult> Get(
        string organizationId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var accessible = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);
        return Results.Ok(OrganizationResponse.From(accessible.Organization, accessible.Role));
    }

    private static async Task<IResult> Create(
        CreateOrganizationRequest req, HttpContext http, PraxyDb db, QuotaService quotas, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var name = ValidateName(req.Name);

        // Organizations were the only creatable resource in Praxy without a cap — and the one every
        // other quota is scoped to, so an operator at their MaxProjects ceiling could just make
        // another organization and keep going. See EnsureOrganizationQuotaAsync's remarks.
        await quotas.EnsureOrganizationQuotaAsync(op.Account.Id, ct);

        var org = new Organization { Id = Ids.NewUuid(), Name = name };
        db.Organizations.Add(org);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = org.Id,
            UserId = op.Account.Id,
            Role = "owner",
            Confirmed = true,
        });
        Audit(db, http, op, "organizations.create", org.Id);

        await db.SaveChangesAsync(ct);
        return Results.Created(
            $"/v1/console/organizations/{Ids.Wire(org.Id)}", OrganizationResponse.From(org, "owner"));
    }

    /// <summary>Name only — an organization's id never changes, same as a project's. Owner only.</summary>
    private static async Task<IResult> Update(
        string organizationId, UpdateOrganizationRequest req, HttpContext http, PraxyDb db,
        OrganizationsService orgs, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var accessible = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);
        await orgs.RequireOwnerAsync(accessible.Organization.Id, op.Account.Id, ct);

        accessible.Organization.Name = ValidateName(req.Name);
        Audit(db, http, op, "organizations.update", accessible.Organization.Id);

        await db.SaveChangesAsync(ct);
        return Results.Ok(OrganizationResponse.From(accessible.Organization, accessible.Role));
    }

    /// <summary>
    /// Empty-only, no <c>force</c> escape hatch — deliberately (organizations-phase-1-prompt.md):
    /// cascading through projects into databases, buckets, functions and sites is exactly the kind
    /// of implicit destruction the engine refuses everywhere else, and unlike a schema change there
    /// is no case where "delete this org and everything in it" is the obvious intent. An operator
    /// also always keeps at least one organization — deleting the last one would produce an account
    /// that can do nothing and has no way back. Owner only.
    /// </summary>
    private static async Task<IResult> Delete(
        string organizationId, HttpContext http, PraxyDb db, OrganizationsService orgs, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var accessible = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);
        await orgs.RequireOwnerAsync(accessible.Organization.Id, op.Account.Id, ct);
        var organization = accessible.Organization;

        if (await db.Projects.AnyAsync(p => p.OrganizationId == organization.Id, ct))
            throw new PraxyException(409, ErrorTypes.OrganizationHasProjects,
                "This organization still has projects. Delete them first.");

        // Confirmed only: a pending invite into some other organization is not a fallback home for
        // this operator's account, so it must not count as "you still have somewhere to go."
        var memberOf = await db.OrganizationMembers.CountAsync(m => m.UserId == op.Account.Id && m.Confirmed, ct);
        if (memberOf <= 1)
            throw new PraxyException(409, ErrorTypes.OrganizationLastOne,
                "You cannot delete your only organization.");

        // OrganizationMember cascades (ON DELETE CASCADE) — nothing else references an org directly
        // once it holds no projects.
        db.Organizations.Remove(organization);
        Audit(db, http, op, "organizations.delete", organization.Id);

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- members ----------------------------------------------------------------------------------

    /// <summary>Any confirmed member can see the roster — membership-only, same as reading the org itself.</summary>
    private static async Task<IResult> ListMembers(
        string organizationId, HttpContext http, PraxyDb db, OrganizationsService orgs, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var accessible = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);
        var members = await orgs.ListMembersAsync(accessible.Organization.Id, ct);
        return Results.Ok(new OrganizationMemberListResponse(
            members.Count, [.. members.Select(OrganizationMemberResponse.From)]));
    }

    /// <summary>Owner only. Emails an acceptance link built from the caller's own <c>url</c> (the console's origin).</summary>
    private static async Task<IResult> InviteMember(
        string organizationId, InviteOrganizationMemberRequest req, HttpContext http, PraxyDb db,
        OrganizationsService orgs, QuotaService quotas, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var accessible = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);
        await orgs.RequireOwnerAsync(accessible.Organization.Id, op.Account.Id, ct);

        var fields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@') || req.Email.Length > 320)
            fields["email"] = ["Must be a valid email address."];
        if (string.IsNullOrWhiteSpace(req.Url))
            fields["url"] = ["Required — the console's own origin, so the emailed link points back at it."];
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid invite payload.", fields);

        // After validation, before InviteAsync does anything: that call creates a console User row
        // for an unknown address and sends mail, so the seat check has to precede it rather than
        // clean up after. The auth-email rate limit on this route bounds mail per window; this
        // bounds the total.
        await quotas.EnsureOrganizationMemberQuotaAsync(accessible.Organization.Id, ct);

        var member = await orgs.InviteAsync(accessible.Organization, req.Email, req.Role, req.Url, ct);
        Audit(db, http, op, "organizations.members.invite", accessible.Organization.Id);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/v1/console/organizations/{Ids.Wire(accessible.Organization.Id)}/members/{Ids.Wire(member.Member.UserId)}",
            OrganizationMemberResponse.From(member));
    }

    /// <summary>Owner/member only, no custom roles — a fixed decision, not an oversight. Owner only to call.</summary>
    private static async Task<IResult> UpdateMemberRole(
        string organizationId, string userId, UpdateOrganizationMemberRoleRequest req, HttpContext http,
        PraxyDb db, OrganizationsService orgs, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var accessible = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);
        await orgs.RequireOwnerAsync(accessible.Organization.Id, op.Account.Id, ct);

        if (!Ids.TryParseWire(userId, out var targetUserId))
            throw PraxyException.NotFound(ErrorTypes.OrganizationMembershipNotFound, "Membership not found.");

        var member = await orgs.ChangeRoleAsync(accessible.Organization.Id, targetUserId, req.Role, ct);
        Audit(db, http, op, "organizations.members.update_role", accessible.Organization.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(OrganizationMemberResponse.From(member));
    }

    /// <summary>Self-removal (leaving) is always allowed; removing anyone else takes the owner role. Either way the last owner cannot go.</summary>
    private static async Task<IResult> RemoveMember(
        string organizationId, string userId, HttpContext http, PraxyDb db, OrganizationsService orgs, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var accessible = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);

        if (!Ids.TryParseWire(userId, out var targetUserId))
            throw PraxyException.NotFound(ErrorTypes.OrganizationMembershipNotFound, "Membership not found.");
        if (targetUserId != op.Account.Id)
            await orgs.RequireOwnerAsync(accessible.Organization.Id, op.Account.Id, ct);

        await orgs.RemoveMemberAsync(accessible.Organization.Id, targetUserId, ct);
        Audit(db, http, op, "organizations.members.remove", accessible.Organization.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Public: an invitee has no operator session yet. Organization and user ids come from the
    /// emailed link (wire-encoded, unguessable) — a malformed one gets the exact same error as a
    /// wrong secret, never a distinct 404, so this endpoint has a single uniform failure shape.
    /// </summary>
    private static async Task<IResult> AcceptInvite(
        string organizationId, string userId, AcceptOrganizationInviteRequest req, HttpContext http,
        OrganizationsService orgs, CancellationToken ct)
    {
        if (!Ids.TryParseWire(organizationId, out var orgId) || !Ids.TryParseWire(userId, out var targetUserId))
            throw new PraxyException(401, ErrorTypes.OrganizationInviteInvalid, "Invalid or expired invitation.");

        var (member, session) = await orgs.AcceptInviteAsync(
            orgId, targetUserId, req.Secret, req.Password,
            http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent, ct);

        SessionCookie.Set(http, session.Token, session.ExpiresAt);
        return Results.Created($"/v1/console/organizations/{organizationId}", new AcceptedOrganizationInviteResponse(
            OrganizationMemberResponse.From(member), member.Account,
            new ConsoleSessionResponse(session.Token, session.ExpiresAt)));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is < 1 or > 128)
            throw PraxyException.ArgumentInvalid("Invalid organization payload.",
                new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });
        return trimmed;
    }

    private static async Task<AccessibleOrganization> RequireAccessibleAsync(
        PraxyDb db, Guid operatorId, string organizationId, CancellationToken ct)
    {
        // An unparseable segment is simply not one of the operator's organizations: it gets the
        // same 404 as a real one they don't belong to, so membership stays unguessable.
        if (!Ids.TryParseWire(organizationId, out var id))
            throw NotFound(organizationId);

        var row = await (
            from o in db.Organizations
            join m in db.OrganizationMembers on o.Id equals m.OrganizationId
            where m.UserId == operatorId && m.Confirmed && o.Id == id
            select new { o, m.Role }).FirstOrDefaultAsync(ct);
        return row is null ? throw NotFound(organizationId) : new AccessibleOrganization(row.o, row.Role);
    }

    private static void Audit(PraxyDb db, HttpContext http, OperatorContext op, string action, Guid organizationId) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            // Instance-level, not project-scoped — same as projects.delete's own audit entry.
            ProjectId = null,
            Actor = $"admin:{op.Account.Id}",
            Action = action,
            Resource = $"organization/{Ids.Wire(organizationId)}",
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });

    private static PraxyException NotFound(string organizationId) =>
        PraxyException.NotFound(ErrorTypes.OrganizationNotFound, $"Organization '{organizationId}' not found.");

    private sealed record AccessibleOrganization(Organization Organization, string Role);

    /// <summary>
    /// Organizations the operator belongs to, scoped through the same membership join
    /// <c>ProjectEndpoints.AccessibleProjects</c> uses — one rule, two surfaces. Confirmed only: a
    /// pending invite is not yet "your organization" for list/read purposes (it becomes one only
    /// once accepted). Membership-only, never a role check — that stays additive, in
    /// <see cref="OrganizationsService.RequireOwnerAsync"/>, for the owner-only actions alone.
    /// </summary>
    private static async Task<List<AccessibleOrganization>> AccessibleOrganizationsAsync(
        PraxyDb db, Guid operatorId, CancellationToken ct) =>
        (await (
            from o in db.Organizations
            join m in db.OrganizationMembers on o.Id equals m.OrganizationId
            where m.UserId == operatorId && m.Confirmed
            orderby o.CreatedAt
            select new { o, m.Role }).ToListAsync(ct))
        .Select(x => new AccessibleOrganization(x.o, x.Role)).ToList();
}
