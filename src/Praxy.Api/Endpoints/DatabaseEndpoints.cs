using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Tables;

namespace Praxy.Api.Endpoints;

/// <summary>
/// The data-plane schema API (<c>/v1/databases</c>): API-key callers only, scoped on
/// <c>databases.read</c>/<c>databases.write</c> — schema management is a server/CI concern, not
/// something an end-user session does. The console has its own operator-authenticated equivalent
/// in <see cref="ConsoleDatabaseEndpoints"/>; both sit on the same <c>Praxy.Tables</c> services.
/// </summary>
public static class DatabaseEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/v1/databases")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>();

        group.MapPost("", CreateDatabase)
            .Produces<DatabaseResponse>(StatusCodes.Status201Created);
        group.MapGet("", ListDatabases)
            .Produces<DatabaseListResponse>();
        group.MapGet("/{databaseId}", GetDatabase)
            .Produces<DatabaseResponse>();
        group.MapPatch("/{databaseId}", UpdateDatabase)
            .Produces<DatabaseResponse>();
        group.MapDelete("/{databaseId}", DeleteDatabase)
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/{databaseId}/tables", CreateTable)
            .Produces<TableResponse>(StatusCodes.Status201Created);
        group.MapGet("/{databaseId}/tables", ListTables)
            .Produces<TableListResponse>();
        group.MapGet("/{databaseId}/tables/{tableId}", GetTable)
            .Produces<TableResponse>();
        group.MapPatch("/{databaseId}/tables/{tableId}", UpdateTable)
            .Produces<TableResponse>();
        group.MapDelete("/{databaseId}/tables/{tableId}", DeleteTable)
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/{databaseId}/tables/{tableId}/permissions", GetPermissions)
            .Produces<TablePermissionsResponse>();
        group.MapPatch("/{databaseId}/tables/{tableId}/permissions", UpdatePermissions)
            .Produces<TablePermissionsResponse>();

        group.MapPost("/{databaseId}/tables/{tableId}/columns/{type}", CreateColumn)
            .Produces<ColumnResponse>(StatusCodes.Status201Created);
        group.MapGet("/{databaseId}/tables/{tableId}/columns", ListColumns)
            .Produces<ColumnListResponse>();
        group.MapGet("/{databaseId}/tables/{tableId}/columns/{columnId}", GetColumn)
            .Produces<ColumnResponse>();
        group.MapPatch("/{databaseId}/tables/{tableId}/columns/{columnId}", UpdateColumn)
            .Produces<ColumnResponse>();
        group.MapDelete("/{databaseId}/tables/{tableId}/columns/{columnId}", DeleteColumn)
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/{databaseId}/tables/{tableId}/indexes", CreateIndex)
            .Produces<IndexResponse>(StatusCodes.Status201Created);
        group.MapGet("/{databaseId}/tables/{tableId}/indexes", ListIndexes)
            .Produces<IndexListResponse>();
        group.MapGet("/{databaseId}/tables/{tableId}/indexes/{indexId}", GetIndex)
            .Produces<IndexResponse>();
        group.MapDelete("/{databaseId}/tables/{tableId}/indexes/{indexId}", DeleteIndex)
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/{databaseId}/jobs", ListJobs)
            .Produces<SchemaJobListResponse>();
        group.MapGet("/{databaseId}/jobs/{jobId}", GetJob)
            .Produces<SchemaJobResponse>();
        group.MapPost("/{databaseId}/jobs/{jobId}/cancel", CancelJob)
            .Produces<SchemaJobResponse>();
        group.MapPost("/{databaseId}/jobs/{jobId}/retry", RetryJob)
            .Produces<SchemaJobResponse>();
    }

    // ---- databases ------------------------------------------------------------------------------

    private static async Task<IResult> CreateDatabase(
        CreateDatabaseRequest req, HttpContext http, DatabasesService databases, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await databases.CreateAsync(project.Id, req.Key, req.Name, ct);
        return Results.Created($"/v1/databases/{Ids.Wire(database.Id)}", DatabaseResponse.From(database));
    }

    private static async Task<IResult> ListDatabases(HttpContext http, DatabasesService databases, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var list = await databases.ListAsync(project.Id, ct);
        return Results.Ok(new DatabaseListResponse(list.Count, [.. list.Select(DatabaseResponse.From)]));
    }

    private static async Task<IResult> GetDatabase(
        string databaseId, HttpContext http, DatabasesService databases, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        return Results.Ok(DatabaseResponse.From(database));
    }

    private static async Task<IResult> UpdateDatabase(
        string databaseId, UpdateDatabaseRequest req, HttpContext http, DatabasesService databases, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        database = await databases.UpdateAsync(database, req.Name, ct);
        return Results.Ok(DatabaseResponse.From(database));
    }

    private static async Task<IResult> DeleteDatabase(
        string databaseId, HttpContext http, DatabasesService databases, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        await databases.DeleteAsync(database, SchemaLookup.TryParseForce(http), ct);
        return Results.NoContent();
    }

    // ---- tables ---------------------------------------------------------------------------------

    private static async Task<IResult> CreateTable(
        string databaseId, CreateTableRequest req, HttpContext http,
        DatabasesService databases, TablesService tables, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await tables.CreateAsync(database, req.Key, req.Name, ct);
        return Results.Created($"/v1/databases/{databaseId}/tables/{Ids.Wire(table.Id)}", TableResponse.From(table));
    }

    private static async Task<IResult> ListTables(
        string databaseId, HttpContext http, DatabasesService databases, TablesService tables, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var list = await tables.ListAsync(database.Id, ct);
        return Results.Ok(new TableListResponse(list.Count, [.. list.Select(TableResponse.From)]));
    }

    private static async Task<IResult> GetTable(
        string databaseId, string tableId, HttpContext http,
        DatabasesService databases, TablesService tables, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        return Results.Ok(TableResponse.From(table));
    }

    private static async Task<IResult> UpdateTable(
        string databaseId, string tableId, UpdateTableRequest req, HttpContext http,
        DatabasesService databases, TablesService tables, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        table = await tables.UpdateAsync(table, req.Name, req.Enabled, ct);
        return Results.Ok(TableResponse.From(table));
    }

    private static async Task<IResult> DeleteTable(
        string databaseId, string tableId, HttpContext http,
        DatabasesService databases, TablesService tables, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        await tables.DeleteAsync(database, table, SchemaLookup.TryParseForce(http), ct);
        return Results.NoContent();
    }

    // ---- permissions ----------------------------------------------------------------------------

    private static async Task<IResult> GetPermissions(
        string databaseId, string tableId, HttpContext http,
        DatabasesService databases, TablesService tables, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var permissions = await tables.GetPermissionsAsync(table.Id, ct);
        return Results.Ok(new TablePermissionsResponse(table.RowSecurity, permissions));
    }

    private static async Task<IResult> UpdatePermissions(
        string databaseId, string tableId, UpdateTablePermissionsRequest req, HttpContext http,
        DatabasesService databases, TablesService tables, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);

        if (req.RowSecurity is { } rowSecurity)
            table = await tables.SetRowSecurityAsync(database, table, rowSecurity, ct);
        var permissions = req.Permissions is not null
            ? await tables.ReplacePermissionsAsync(table.Id, req.Permissions, ct)
            : await tables.GetPermissionsAsync(table.Id, ct);
        return Results.Ok(new TablePermissionsResponse(table.RowSecurity, permissions));
    }

    // ---- columns --------------------------------------------------------------------------------

    private static async Task<IResult> CreateColumn(
        string databaseId, string tableId, string type, CreateColumnRequest req, HttpContext http,
        DatabasesService databases, TablesService tables, ColumnsService columns, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var column = await columns.CreateAsync(
            database, table, type, req.Key, req.Required ?? false, req.Array ?? false,
            req.Size, req.Elements, req.Default, ct);
        return Results.Created(
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/{Ids.Wire(column.Id)}", ColumnResponse.From(column));
    }

    private static async Task<IResult> ListColumns(
        string databaseId, string tableId, HttpContext http,
        DatabasesService databases, TablesService tables, ColumnsService columns, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var list = await columns.ListAsync(table.Id, ct);
        return Results.Ok(new ColumnListResponse(list.Count, [.. list.Select(ColumnResponse.From)]));
    }

    private static async Task<IResult> GetColumn(
        string databaseId, string tableId, string columnId, HttpContext http,
        DatabasesService databases, TablesService tables, ColumnsService columns, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var column = await SchemaLookup.ColumnAsync(columns, table.Id, columnId, ct);
        return Results.Ok(ColumnResponse.From(column));
    }

    private static async Task<IResult> UpdateColumn(
        string databaseId, string tableId, string columnId, UpdateColumnRequest req, HttpContext http,
        DatabasesService databases, TablesService tables, ColumnsService columns, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var column = await SchemaLookup.ColumnAsync(columns, table.Id, columnId, ct);
        column = await columns.UpdateAsync(database, table, column, req.Key, req.Required, ct);
        return Results.Ok(ColumnResponse.From(column));
    }

    private static async Task<IResult> DeleteColumn(
        string databaseId, string tableId, string columnId, HttpContext http,
        DatabasesService databases, TablesService tables, ColumnsService columns, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var column = await SchemaLookup.ColumnAsync(columns, table.Id, columnId, ct);
        await columns.DeleteAsync(database, table, column, SchemaLookup.TryParseForce(http), ct);
        return Results.NoContent();
    }

    // ---- indexes --------------------------------------------------------------------------------

    private static async Task<IResult> CreateIndex(
        string databaseId, string tableId, CreateIndexRequest req, HttpContext http,
        DatabasesService databases, TablesService tables, IndexesService indexes, SchemaJobSignal signal,
        CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var index = await indexes.CreateAsync(database, table, req.Key, req.Type, req.Columns, req.Orders, signal, ct);
        return Results.Created(
            $"/v1/databases/{databaseId}/tables/{tableId}/indexes/{Ids.Wire(index.Id)}", IndexResponse.From(index));
    }

    private static async Task<IResult> ListIndexes(
        string databaseId, string tableId, HttpContext http,
        DatabasesService databases, TablesService tables, IndexesService indexes, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var list = await indexes.ListAsync(table.Id, ct);
        return Results.Ok(new IndexListResponse(list.Count, [.. list.Select(IndexResponse.From)]));
    }

    private static async Task<IResult> GetIndex(
        string databaseId, string tableId, string indexId, HttpContext http,
        DatabasesService databases, TablesService tables, IndexesService indexes, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var index = await SchemaLookup.IndexAsync(indexes, table.Id, indexId, ct);
        return Results.Ok(IndexResponse.From(index));
    }

    private static async Task<IResult> DeleteIndex(
        string databaseId, string tableId, string indexId, HttpContext http,
        DatabasesService databases, TablesService tables, IndexesService indexes, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var table = await SchemaLookup.TableAsync(tables, database.Id, tableId, ct);
        var index = await SchemaLookup.IndexAsync(indexes, table.Id, indexId, ct);
        await indexes.DeleteAsync(database, table, index, ct);
        return Results.NoContent();
    }

    // ---- schema jobs ----------------------------------------------------------------------------

    private static async Task<IResult> ListJobs(
        string databaseId, HttpContext http, DatabasesService databases, SchemaJobsService jobs, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var tableId = http.Request.Query["tableId"].FirstOrDefault();
        Guid? tableFilter = Ids.TryParseWire(tableId, out var parsed) ? parsed : null;
        var list = await jobs.ListAsync(database.Id, tableFilter, ct);
        return Results.Ok(new SchemaJobListResponse(list.Count, [.. list.Select(SchemaJobResponse.From)]));
    }

    private static async Task<IResult> GetJob(
        string databaseId, string jobId, HttpContext http,
        DatabasesService databases, SchemaJobsService jobs, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesRead);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var job = await SchemaLookup.JobAsync(jobs, database.Id, jobId, ct);
        return Results.Ok(SchemaJobResponse.From(job));
    }

    private static async Task<IResult> CancelJob(
        string databaseId, string jobId, HttpContext http,
        DatabasesService databases, SchemaJobsService jobs, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var job = await SchemaLookup.JobAsync(jobs, database.Id, jobId, ct);
        job = await jobs.CancelAsync(job, ct);
        return Results.Ok(SchemaJobResponse.From(job));
    }

    private static async Task<IResult> RetryJob(
        string databaseId, string jobId, HttpContext http,
        DatabasesService databases, SchemaJobsService jobs, SchemaJobSignal signal, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.DatabasesWrite);
        var database = await SchemaLookup.DatabaseAsync(databases, project.Id, databaseId, ct);
        var job = await SchemaLookup.JobAsync(jobs, database.Id, jobId, ct);
        job = await jobs.RetryAsync(job, ct);
        signal.Notify();
        return Results.Ok(SchemaJobResponse.From(job));
    }
}
