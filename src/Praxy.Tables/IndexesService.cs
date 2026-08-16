using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Database = Praxy.Persistence.Entities.Database;

namespace Praxy.Tables;

/// <summary>
/// Indexes always go through <c>schema_jobs</c> — <c>CREATE INDEX CONCURRENTLY</c> cannot run
/// inside a transaction, so even a "fast" single-column index takes the async path for a
/// consistent story (architecture.md §4.5). Deletion is a plain synchronous <c>DROP INDEX</c>: a
/// brief exclusive lock, guarded by <c>lock_timeout</c>, not a concurrent rebuild.
/// </summary>
public sealed class IndexesService(PraxyDb db, CatalogCache cache)
{
    public const string TypeKey = "key";
    public const string TypeUnique = "unique";
    public const string TypeFulltext = "fulltext";
    public static readonly IReadOnlyList<string> Types = [TypeKey, TypeUnique, TypeFulltext];

    public const int MaxIndexesPerTable = 64;
    public const int MaxColumnsPerIndex = 8;

    /// <summary>Text-backed column types — the only ones a fulltext index can source from.</summary>
    private static readonly string[] FulltextEligibleTypes =
        [ColumnTypes.String, ColumnTypes.Email, ColumnTypes.Url, ColumnTypes.Ip, ColumnTypes.Enum];

    public async Task<IndexDef> CreateAsync(
        Database database, TableDef table, string key, string type, string[] columnKeys, string[]? orders,
        SchemaJobSignal signal, CancellationToken ct)
    {
        if (!Types.Contains(type))
            throw new PraxyException(400, ErrorTypes.IndexInvalid, $"Unknown index type '{type}'.");
        if (!Keys.IsValid(key))
            throw PraxyException.ArgumentInvalid("Invalid index payload.",
                new Dictionary<string, string[]>
                {
                    ["key"] = ["Must start with a letter and contain only letters, digits and underscores (max 64 chars)."],
                });
        if (await db.Indexes.CountAsync(i => i.TableId == table.Id, ct) >= MaxIndexesPerTable)
            throw new PraxyException(400, ErrorTypes.GeneralResourceLimitExceeded,
                $"This table already has the maximum of {MaxIndexesPerTable} indexes.");

        var columns = await db.Columns.Where(c => c.TableId == table.Id).ToListAsync(ct);
        var resolved = ResolveIndexColumns(columns, columnKeys, type);
        var effectiveOrders = orders is { Length: > 0 } ? orders : [.. resolved.Select(_ => "asc")];
        if (effectiveOrders.Length != resolved.Count || effectiveOrders.Any(o => o is not ("asc" or "desc")))
            throw PraxyException.ArgumentInvalid("Invalid index payload.",
                new Dictionary<string, string[]> { ["orders"] = ["Must have one 'asc'/'desc' entry per column."] });

        var id = Ids.NewUuid();
        var physicalName = PhysicalNaming.IndexName(key, id);
        var index = new IndexDef
        {
            Id = id,
            TableId = table.Id,
            Key = key,
            Type = type,
            Columns = columnKeys,
            Orders = type == TypeFulltext ? [] : effectiveOrders,
            PhysicalName = physicalName,
            Status = "processing",
        };

        var fulltextColumnPhysical = type == TypeFulltext ? PhysicalNaming.FulltextColumnName(physicalName) : null;
        var payload = JsonSerializer.Serialize(new CreateIndexJobPayload(
            IndexId: Ids.Wire(id),
            SchemaName: database.SchemaName,
            TablePhysicalName: table.PhysicalName,
            IndexPhysicalName: physicalName,
            Unique: type == TypeUnique,
            Fulltext: type == TypeFulltext,
            ColumnsPhysical: [.. resolved.Select(c => c.PhysicalName)],
            Orders: index.Orders,
            FulltextColumnPhysical: fulltextColumnPhysical));

        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            db.Indexes.Add(index);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                throw new PraxyException(409, ErrorTypes.IndexAlreadyExists,
                    $"An index with key '{key}' already exists on this table.");
            }

            db.SchemaJobs.Add(new SchemaJob
            {
                Id = Ids.NewUuid(),
                DatabaseId = database.Id,
                TableId = table.Id,
                Kind = SchemaJobKinds.CreateIndex,
                Payload = payload,
                Status = "queued",
            });
            await db.SaveChangesAsync(ct);
        }, ct);

        cache.Invalidate(table.Id);
        signal.Notify();
        return index;
    }

    public Task<List<IndexDef>> ListAsync(Guid tableId, CancellationToken ct) =>
        db.Indexes.Where(i => i.TableId == tableId).OrderBy(i => i.CreatedAt).ToListAsync(ct);

    public async Task<IndexDef> GetAsync(Guid tableId, Guid indexId, CancellationToken ct) =>
        await db.Indexes.FirstOrDefaultAsync(i => i.Id == indexId && i.TableId == tableId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.IndexNotFound, "Index not found.");

    public async Task DeleteAsync(Database database, TableDef table, IndexDef index, CancellationToken ct)
    {
        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            db.Indexes.Remove(index);
            await db.SaveChangesAsync(ct);

            await SchemaDdl.SetLockTimeoutAsync(db, TimeSpan.FromSeconds(5), ct);
            await SchemaDdl.ExecuteAsync(db,
                $"DROP INDEX IF EXISTS {PhysicalNaming.Quote(database.SchemaName)}.{PhysicalNaming.Quote(index.PhysicalName)}",
                ct);
            if (index.Type == TypeFulltext)
            {
                var qualified = PhysicalNaming.QualifiedTable(database.SchemaName, table.PhysicalName);
                var ftsColumn = PhysicalNaming.Quote(PhysicalNaming.FulltextColumnName(index.PhysicalName));
                await SchemaDdl.ExecuteAsync(db, $"ALTER TABLE {qualified} DROP COLUMN IF EXISTS {ftsColumn}", ct);
            }
        }, ct);
        cache.Invalidate(table.Id);
    }

    private static List<ColumnDef> ResolveIndexColumns(List<ColumnDef> columns, string[] columnKeys, string type)
    {
        if (columnKeys.Length is 0 or > MaxColumnsPerIndex)
            throw PraxyException.ArgumentInvalid("Invalid index payload.",
                new Dictionary<string, string[]> { ["columns"] = [$"Provide 1 to {MaxColumnsPerIndex} column keys."] });

        var byKey = columns.ToDictionary(c => c.Key);
        var resolved = new List<ColumnDef>();
        foreach (var key in columnKeys)
        {
            if (!byKey.TryGetValue(key, out var column))
                throw PraxyException.NotFound(ErrorTypes.ColumnNotFound, $"Column '{key}' not found on this table.");
            if (type == TypeFulltext && !FulltextEligibleTypes.Contains(column.Type))
                throw new PraxyException(400, ErrorTypes.IndexInvalid,
                    $"Column '{key}' (type '{column.Type}') can't be used in a fulltext index.");
            resolved.Add(column);
        }
        return resolved;
    }
}

public static class SchemaJobKinds
{
    public const string CreateIndex = "create_index";
}

public sealed record CreateIndexJobPayload(
    string IndexId, string SchemaName, string TablePhysicalName, string IndexPhysicalName,
    bool Unique, bool Fulltext, string[] ColumnsPhysical, string[] Orders, string? FulltextColumnPhysical);
