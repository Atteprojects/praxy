using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence.Entities;
using Praxy.Tables;
using Database = Praxy.Persistence.Entities.Database;

namespace Praxy.Api.Endpoints;

/// <summary>Wire-id resolution shared by the data-plane and console-admin schema endpoints.</summary>
internal static class SchemaLookup
{
    public static async Task<Database> DatabaseAsync(
        DatabasesService databases, string projectId, string databaseId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(databaseId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.DatabaseNotFound, "Database not found.");
        return await databases.GetAsync(projectId, parsed, ct);
    }

    public static async Task<TableDef> TableAsync(
        TablesService tables, Guid databaseId, string tableId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(tableId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.TableNotFound, "Table not found.");
        return await tables.GetAsync(databaseId, parsed, ct);
    }

    public static async Task<ColumnDef> ColumnAsync(
        ColumnsService columns, Guid tableId, string columnId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(columnId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.ColumnNotFound, "Column not found.");
        return await columns.GetAsync(tableId, parsed, ct);
    }

    public static async Task<IndexDef> IndexAsync(
        IndexesService indexes, Guid tableId, string indexId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(indexId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.IndexNotFound, "Index not found.");
        return await indexes.GetAsync(tableId, parsed, ct);
    }

    public static async Task<SchemaJob> JobAsync(
        SchemaJobsService jobs, Guid databaseId, string jobId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(jobId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.SchemaJobNotFound, "Job not found.");
        return await jobs.GetAsync(databaseId, parsed, ct);
    }

    public static bool TryParseForce(HttpContext http) =>
        bool.TryParse(http.Request.Query["force"], out var force) && force;
}
