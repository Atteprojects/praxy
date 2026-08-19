using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record OrganizationResponse(string Id, string Name, DateTimeOffset CreatedAt)
{
    public static OrganizationResponse From(Organization o) => new(Ids.Wire(o.Id), o.Name, o.CreatedAt);
}

public sealed record OrganizationListResponse(int Total, IReadOnlyList<OrganizationResponse> Organizations);

/// <summary>
/// Read-only organization identity for the console: the home screen resolves the operator's
/// organization so its name and id can head the projects list, and so the id can sit in the URL.
/// Single-org by construction today — claim creates exactly one and every project lands in it —
/// so there is deliberately no create/rename/delete surface and no member management here.
/// </summary>
public static class ConsoleOrganizationEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var organizations = api.MapGroup("/v1/console/organizations")
            .AddEndpointFilter<RequireOperatorFilter>();

        organizations.MapGet("", List).Produces<OrganizationListResponse>();
        organizations.MapGet("/{organizationId}", Get).Produces<OrganizationResponse>();
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

        // An unparseable segment is simply not one of the operator's organizations: it gets the
        // same 404 as a real one they don't belong to, so membership stays unguessable.
        if (!Ids.TryParseWire(organizationId, out var id))
            throw NotFound(organizationId);

        var organization = await AccessibleOrganizations(db, op.Account.Id)
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw NotFound(organizationId);

        return Results.Ok(OrganizationResponse.From(organization));
    }

    private static PraxyException NotFound(string organizationId) =>
        PraxyException.NotFound(ErrorTypes.OrganizationNotFound, $"Organization '{organizationId}' not found.");

    /// <summary>
    /// Organizations the operator belongs to, scoped through the same membership join
    /// <c>ProjectEndpoints.AccessibleProjects</c> uses — one rule, two surfaces.
    /// </summary>
    private static IQueryable<Organization> AccessibleOrganizations(PraxyDb db, Guid operatorId) =>
        from o in db.Organizations
        join m in db.OrganizationMembers on o.Id equals m.OrganizationId
        where m.UserId == operatorId
        select o;
}
