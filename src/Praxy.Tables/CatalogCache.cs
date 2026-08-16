using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Database = Praxy.Persistence.Entities.Database;

namespace Praxy.Tables;

/// <summary>
/// Everything the row-CRUD path needs to know about one table, loaded in a single round trip
/// instead of the five separate lookups (database, table, columns, indexes, permissions) the
/// schema-management services make individually.
/// </summary>
public sealed record CatalogEntry(
    Database Database, TableDef Table, IReadOnlyList<ColumnDef> Columns, IReadOnlyList<IndexDef> Indexes,
    IReadOnlyList<TablePermission> Permissions)
{
    public ColumnDef? FindColumn(string key) => Columns.FirstOrDefault(c => c.Key == key);

    /// <summary>Roles granted <paramref name="action"/> at the table level (write already expanded at storage time).</summary>
    public string[] TableRoles(string action) =>
        [.. Permissions.Where(p => p.Action == action).Select(p => p.Role)];

    /// <summary>An available fulltext index covering exactly this single column, if any — what <c>search</c> requires.</summary>
    public IndexDef? FulltextIndexFor(string columnKey) => Indexes.FirstOrDefault(i =>
        i.Type == IndexesService.TypeFulltext && i.Status == "available" &&
        i.Columns.Length == 1 && i.Columns[0] == columnKey);
}

/// <summary>
/// In-memory per-table cache (roadmap.md: "every row request otherwise costs 5 catalog round
/// trips"). Invalidated directly by the mutating services below rather than through
/// <c>IEventBus</c> — in a single Praxy process that is strictly more immediate than a pub/sub
/// hop, and every mutation path already runs in-process. A short TTL is kept as a safety net for
/// any path that forgets to invalidate explicitly.
/// </summary>
public sealed class CatalogCache(IServiceScopeFactory scopeFactory)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private sealed record Cached(CatalogEntry Entry, DateTime LoadedAt);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Cached> _cache = new();

    public async Task<CatalogEntry> GetAsync(Guid tableId, CancellationToken ct)
    {
        if (_cache.TryGetValue(tableId, out var cached) && DateTime.UtcNow - cached.LoadedAt < Ttl)
            return cached.Entry;

        var entry = await LoadAsync(tableId, ct);
        _cache[tableId] = new Cached(entry, DateTime.UtcNow);
        return entry;
    }

    public void Invalidate(Guid tableId) => _cache.TryRemove(tableId, out _);

    private async Task<CatalogEntry> LoadAsync(Guid tableId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

        var table = await db.Tables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tableId, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.TableNotFound, "Table not found.");
        var database = await db.Databases.AsNoTracking().FirstAsync(d => d.Id == table.DatabaseId, ct);
        var columns = await db.Columns.AsNoTracking().Where(c => c.TableId == tableId)
            .OrderBy(c => c.Position).ToListAsync(ct);
        var indexes = await db.Indexes.AsNoTracking().Where(i => i.TableId == tableId).ToListAsync(ct);
        var permissions = await db.TablePermissions.AsNoTracking().Where(p => p.TableId == tableId).ToListAsync(ct);

        return new CatalogEntry(database, table, columns, indexes, permissions);
    }
}
