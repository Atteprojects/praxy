using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Tables;

namespace Praxy.Api.Endpoints;

/// <summary>
/// The console's row browser surface: operator session + project ownership, exactly like
/// <see cref="ConsoleDatabaseEndpoints"/>. Operators manage the whole project, so reads/writes here
/// bypass row-level permission filtering entirely — same posture as an API key with
/// <c>bypassRowPermissions</c>, just implicit for the console's own operator auth.
/// </summary>
public static class ConsoleRowEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/v1/console/projects/{projectId}/databases")
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>();

        admin.MapPost("/{databaseId}/tables/{tableId}/rows", CreateRow);
        admin.MapGet("/{databaseId}/tables/{tableId}/rows", ListRows);
        admin.MapGet("/{databaseId}/tables/{tableId}/rows/{rowId}", GetRow);
        admin.MapPatch("/{databaseId}/tables/{tableId}/rows/{rowId}", UpdateRow);
        admin.MapDelete("/{databaseId}/tables/{tableId}/rows/{rowId}", DeleteRow);
    }

    private static async Task<IResult> CreateRow(
        string databaseId, string tableId, CreateRowRequest req, HttpContext http, PraxyDb db, RowsService rows, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var entry = await RowEndpoints.ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var row = await rows.CreateAsync(entry, req.RowId, req.Data, req.Permissions, [], bypassPermissions: true, ct);
        await AuditAsync(db, http, project.Id, "rows.create", $"table/{tableId}/row/{row["$id"]}", ct);
        return Results.Created($"/v1/console/projects/{project.Id}/databases/{databaseId}/tables/{tableId}/rows/{row["$id"]}", row);
    }

    private static async Task<IResult> ListRows(
        string databaseId, string tableId, HttpContext http, RowsService rows, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var entry = await RowEndpoints.ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var queries = RowEndpoints.QueryStrings(http);
        var includeTotal = !bool.TryParse(http.Request.Query["total"], out var t) || t;
        var (total, list) = await rows.ListAsync(entry, queries, [], bypassPermissions: true, includeTotal, ct);
        return Results.Ok(new RowListResponse(total, list));
    }

    private static async Task<IResult> GetRow(
        string databaseId, string tableId, string rowId, HttpContext http, RowsService rows, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var entry = await RowEndpoints.ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var row = await rows.GetAsync(entry, RowEndpoints.ParseRowId(rowId), [], bypassPermissions: true, ct);
        return Results.Ok(row);
    }

    private static async Task<IResult> UpdateRow(
        string databaseId, string tableId, string rowId, UpdateRowRequest req, HttpContext http, PraxyDb db,
        RowsService rows, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var entry = await RowEndpoints.ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var row = await rows.UpdateAsync(entry, RowEndpoints.ParseRowId(rowId), req.Data, req.Permissions, [], bypassPermissions: true, ct);
        await AuditAsync(db, http, project.Id, "rows.update", $"table/{tableId}/row/{rowId}", ct);
        return Results.Ok(row);
    }

    private static async Task<IResult> DeleteRow(
        string databaseId, string tableId, string rowId, HttpContext http, PraxyDb db, RowsService rows, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var entry = await RowEndpoints.ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        await rows.DeleteAsync(entry, RowEndpoints.ParseRowId(rowId), [], bypassPermissions: true, ct);
        await AuditAsync(db, http, project.Id, "rows.delete", $"table/{tableId}/row/{rowId}", ct);
        return Results.NoContent();
    }

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
