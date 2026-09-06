using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Tables.Quotas;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record OrganizationResponse(string Id, string Name, DateTimeOffset CreatedAt)
{
    public static OrganizationResponse From(Organization o) => new(Ids.Wire(o.Id), o.Name, o.CreatedAt);
}

public sealed record OrganizationListResponse(int Total, IReadOnlyList<OrganizationResponse> Organizations);

public sealed record CreateOrganizationRequest(string Name);

public sealed record UpdateOrganizationRequest(string Name);

/// <summary>
/// Organization lifecycle for the console: create, rename, delete, and the read surface the home
/// screen resolves to build the org-scoped URL. Organizations-phase-1: no membership changes yet —
/// every org still has exactly one member, its creator, and <see cref="OrganizationMember.Role"/>
/// stays unread (organizations-phase-1-prompt.md deliberately leaves it dead; Phase 2 turns it on).
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
    }

    private static async Task<IResult> List(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var list = await AccessibleOrganizations(db, op.Account.Id)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new OrganizationListResponse(list.Count, [.. list.Select(OrganizationResponse.From)]));
    }

    private static async Task<IResult> Get(
        string organizationId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var organization = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);
        return Results.Ok(OrganizationResponse.From(organization));
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
        });
        Audit(db, http, op, "organizations.create", org.Id);

        await db.SaveChangesAsync(ct);
        return Results.Created($"/v1/console/organizations/{Ids.Wire(org.Id)}", OrganizationResponse.From(org));
    }

    /// <summary>Name only — an organization's id never changes, same as a project's.</summary>
    private static async Task<IResult> Update(
        string organizationId, UpdateOrganizationRequest req, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var organization = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);

        organization.Name = ValidateName(req.Name);
        Audit(db, http, op, "organizations.update", organization.Id);

        await db.SaveChangesAsync(ct);
        return Results.Ok(OrganizationResponse.From(organization));
    }

    /// <summary>
    /// Empty-only, no <c>force</c> escape hatch — deliberately (organizations-phase-1-prompt.md):
    /// cascading through projects into databases, buckets, functions and sites is exactly the kind
    /// of implicit destruction the engine refuses everywhere else, and unlike a schema change there
    /// is no case where "delete this org and everything in it" is the obvious intent. An operator
    /// also always keeps at least one organization — deleting the last one would produce an account
    /// that can do nothing and has no way back.
    /// </summary>
    private static async Task<IResult> Delete(
        string organizationId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var organization = await RequireAccessibleAsync(db, op.Account.Id, organizationId, ct);

        if (await db.Projects.AnyAsync(p => p.OrganizationId == organization.Id, ct))
            throw new PraxyException(409, ErrorTypes.OrganizationHasProjects,
                "This organization still has projects. Delete them first.");

        var memberOf = await db.OrganizationMembers.CountAsync(m => m.UserId == op.Account.Id, ct);
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

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is < 1 or > 128)
            throw PraxyException.ArgumentInvalid("Invalid organization payload.",
                new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });
        return trimmed;
    }

    private static async Task<Organization> RequireAccessibleAsync(
        PraxyDb db, Guid operatorId, string organizationId, CancellationToken ct)
    {
        // An unparseable segment is simply not one of the operator's organizations: it gets the
        // same 404 as a real one they don't belong to, so membership stays unguessable.
        if (!Ids.TryParseWire(organizationId, out var id))
            throw NotFound(organizationId);

        return await AccessibleOrganizations(db, operatorId).FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw NotFound(organizationId);
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

    /// <summary>
    /// Organizations the operator belongs to, scoped through the same membership join
    /// <c>ProjectEndpoints.AccessibleProjects</c> uses — one rule, two surfaces. Multi-org since
    /// this phase: an operator can belong to several, so this always joins rather than assuming one.
    /// </summary>
    private static IQueryable<Organization> AccessibleOrganizations(PraxyDb db, Guid operatorId) =>
        from o in db.Organizations
        join m in db.OrganizationMembers on o.Id equals m.OrganizationId
        where m.UserId == operatorId
        select o;
}
