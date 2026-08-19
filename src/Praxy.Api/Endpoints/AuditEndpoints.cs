using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record AuditLogEntryResponse(
    string Id, string? ProjectId, string Actor, string Action, string Resource, string? Ip, DateTimeOffset CreatedAt)
{
    public static AuditLogEntryResponse From(AuditLogEntry e) =>
        new(Ids.Wire(e.Id), e.ProjectId, e.Actor, e.Action, e.Resource, e.Ip, e.CreatedAt);
}

public sealed record AuditLogListResponse(int Total, IReadOnlyList<AuditLogEntryResponse> Entries);

/// <summary>
/// The read half of the audit log: every entry any endpoint writes via <c>db.AuditLog.Add</c>,
/// filterable and paginated, newest first. Two surfaces because <see cref="AuditLogEntry.ProjectId"/>
/// is nullable — <c>instance.claim</c> and any future instance-level entry carry no project and are
/// otherwise unreachable from a project-scoped query.
///
/// This does not cover data-plane activity: <c>rows.create</c>/<c>update</c>/<c>delete</c> here come
/// only from the console's own row editor (<see cref="ConsoleRowEndpoints"/>), never from an app user
/// or API key writing through <c>/v1/databases/…</c>. The console screen says so; do not let this
/// silently grow to imply otherwise.
/// </summary>
public static class AuditEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/v1/console/projects/{projectId}/audit", ListProjectAudit)
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>()
            .Produces<AuditLogListResponse>();

        // No ConsoleProjectFilter — instance-level entries have no project to scope to. The instance
        // is single-operator by construction (claim is one-shot, there is no invite endpoint), so any
        // signed-in operator seeing the whole instance log is not a privilege split worth building yet.
        api.MapGet("/v1/console/audit", ListInstanceAudit)
            .AddEndpointFilter<RequireOperatorFilter>()
            .Produces<AuditLogListResponse>();
    }

    private static async Task<IResult> ListProjectAudit(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        return Results.Ok(await ListAsync(http, db.AuditLog.Where(a => a.ProjectId == project.Id), ct));
    }

    private static async Task<IResult> ListInstanceAudit(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        return Results.Ok(await ListAsync(http, db.AuditLog.Where(a => a.ProjectId == null), ct));
    }

    /// <summary>
    /// Filters compose as an AND, applied before the page is taken so total/offset stay correct.
    /// Actor/action/resource are exact matches on the same opaque strings the entries were written
    /// with — the console shows the raw value precisely so it can be copied back in here.
    /// </summary>
    private static async Task<AuditLogListResponse> ListAsync(
        HttpContext http, IQueryable<AuditLogEntry> query, CancellationToken ct)
    {
        var (action, actor, resource, from, to, limit, offset) = ListParams(http);
        if (action is not null) query = query.Where(a => a.Action == action);
        if (actor is not null) query = query.Where(a => a.Actor == actor);
        if (resource is not null) query = query.Where(a => a.Resource == resource);
        if (from is not null) query = query.Where(a => a.CreatedAt >= from);
        if (to is not null) query = query.Where(a => a.CreatedAt <= to);

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(a => a.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new AuditLogListResponse(total, [.. page.Select(AuditLogEntryResponse.From)]);
    }

    private static (string? Action, string? Actor, string? Resource, DateTimeOffset? From, DateTimeOffset? To, int Limit, int Offset)
        ListParams(HttpContext http)
    {
        var q = http.Request.Query;
        var limit = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 100) : 25;
        var offset = int.TryParse(q["offset"], out var o) ? Math.Max(0, o) : 0;
        var from = DateTimeOffset.TryParse(q["from"], out var f) ? f : (DateTimeOffset?)null;
        var to = DateTimeOffset.TryParse(q["to"], out var t) ? t : (DateTimeOffset?)null;
        return (Trim(q["action"]), Trim(q["actor"]), Trim(q["resource"]), from, to, limit, offset);
    }

    private static string? Trim(Microsoft.Extensions.Primitives.StringValues v) =>
        string.IsNullOrWhiteSpace(v.FirstOrDefault()) ? null : v.FirstOrDefault()!.Trim();
}
