using System.Text.Json.Nodes;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Tables;

namespace Praxy.Api.Endpoints;

/// <summary>
/// The real data plane: row CRUD against a table's data, reachable by app-user sessions, guests
/// (roles <c>any</c>/<c>guests</c> may still be granted table/row permissions), and API keys alike
/// — unlike <see cref="DatabaseEndpoints"/>'s schema-management surface, this is exactly what an
/// end-user session is for. Permission filtering (table-level always, row-level when
/// <c>row_security</c> is on) does the access control; a key additionally needs the matching
/// <c>databases.read</c>/<c>databases.write</c> scope, but a session or guest does not.
/// </summary>
public static class RowEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/v1/databases")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>()
            .RequireRateLimiting("data-plane");

        // A row is an open JSON object — its columns are defined at runtime, so the schema is
        // `object` with the documented `$`-prefixed system fields, not a fixed record.
        group.MapPost("/{databaseId}/tables/{tableId}/rows", CreateRow)
            .Produces<JsonObject>(StatusCodes.Status201Created);
        group.MapGet("/{databaseId}/tables/{tableId}/rows", ListRows).Produces<RowListResponse>();
        group.MapGet("/{databaseId}/tables/{tableId}/rows/{rowId}", GetRow).Produces<JsonObject>();
        group.MapPatch("/{databaseId}/tables/{tableId}/rows/{rowId}", UpdateRow).Produces<JsonObject>();
        group.MapDelete("/{databaseId}/tables/{tableId}/rows/{rowId}", DeleteRow)
            .Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<IResult> CreateRow(
        string databaseId, string tableId, CreateRowRequest req, HttpContext http,
        RowsService rows, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.DatabasesWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var entry = await ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var (roles, bypass) = await CallerAsync(http, roleResolver);
        var row = await rows.CreateAsync(entry, req.RowId, req.Data, req.Permissions, roles, bypass, ct);
        return Results.Created($"/v1/databases/{databaseId}/tables/{tableId}/rows/{row["$id"]}", row);
    }

    private static async Task<IResult> ListRows(
        string databaseId, string tableId, HttpContext http, RowsService rows, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.DatabasesRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var entry = await ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var (roles, bypass) = await CallerAsync(http, roleResolver);
        var queries = QueryStrings(http);
        var includeTotal = !bool.TryParse(http.Request.Query["total"], out var t) || t;
        var (total, list) = await rows.ListAsync(entry, queries, roles, bypass, includeTotal, ExpandKeys(http), ct);
        return Results.Ok(new RowListResponse(total, list));
    }

    private static async Task<IResult> GetRow(
        string databaseId, string tableId, string rowId, HttpContext http,
        RowsService rows, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.DatabasesRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var entry = await ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var (roles, bypass) = await CallerAsync(http, roleResolver);
        var row = await rows.GetAsync(entry, ParseRowId(rowId), roles, bypass, ExpandKeys(http), ct);
        return Results.Ok(row);
    }

    private static async Task<IResult> UpdateRow(
        string databaseId, string tableId, string rowId, UpdateRowRequest req, HttpContext http,
        RowsService rows, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.DatabasesWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var entry = await ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var (roles, bypass) = await CallerAsync(http, roleResolver);
        var row = await rows.UpdateAsync(entry, ParseRowId(rowId), req.Data, req.Permissions, roles, bypass, ct);
        return Results.Ok(row);
    }

    private static async Task<IResult> DeleteRow(
        string databaseId, string tableId, string rowId, HttpContext http,
        RowsService rows, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.DatabasesWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var entry = await ResolveAsync(rows, project.Id, databaseId, tableId, ct);
        var (roles, bypass) = await CallerAsync(http, roleResolver);
        await rows.DeleteAsync(entry, ParseRowId(rowId), roles, bypass, ct);
        return Results.NoContent();
    }

    // ---- helpers ----------------------------------------------------------------------------

    internal static async Task<CatalogEntry> ResolveAsync(
        RowsService rows, string projectId, string databaseId, string tableId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(databaseId, out var dbId) || !Ids.TryParseWire(tableId, out var tblId))
            throw PraxyException.NotFound(ErrorTypes.TableNotFound, "Table not found.");
        return await rows.ResolveTableAsync(projectId, dbId, tblId, ct);
    }

    internal static Guid ParseRowId(string rowId) =>
        Ids.TryParseWire(rowId, out var id) ? id : throw PraxyException.NotFound(ErrorTypes.RowNotFound, "Row not found.");

    internal static async Task<(string[] Roles, bool Bypass)> CallerAsync(HttpContext http, IRoleResolver resolver)
    {
        var roles = await RequestRoles.GetAsync(http, resolver);
        var bypass = AppPrincipalFilter.Current(http) is RequestPrincipal.Key(var key) && key.BypassRowPermissions;
        return (roles, bypass);
    }

    /// <summary>Sessions and guests reach row endpoints on permissions alone; a key additionally needs the scope.</summary>
    private static void RequireScopeIfKey(HttpContext http, string scope)
    {
        if (AppPrincipalFilter.Current(http) is RequestPrincipal.Key)
            AppPrincipalFilter.RequireScope(http, scope);
    }

    internal static string[] QueryStrings(HttpContext http)
    {
        var values = http.Request.Query["queries[]"];
        return values.Count > 0 ? [.. values.Where(v => v is not null)!] : [.. http.Request.Query["queries"].Where(v => v is not null)!];
    }

    /// <summary>A single comma-separated <c>?expand=a,b</c> — the common REST convention, matching the design doc's own framing.</summary>
    internal static string[] ExpandKeys(HttpContext http)
    {
        var raw = http.Request.Query["expand"].ToString();
        return string.IsNullOrEmpty(raw) ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
