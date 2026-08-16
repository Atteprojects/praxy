using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Database = Praxy.Persistence.Entities.Database;

namespace Praxy.Tables;

/// <summary>
/// Tables: sync DDL in the same transaction as the metadata write (architecture.md §4.5). New
/// tables carry only the three system columns (<c>_id</c>, <c>_created_at</c>, <c>_updated_at</c>)
/// — user columns are added one at a time via <see cref="ColumnsService"/>.
/// </summary>
public sealed class TablesService(PraxyDb db)
{
    public const int MaxTablesPerDatabase = 200;

    public async Task<TableDef> CreateAsync(Database database, string key, string name, CancellationToken ct)
    {
        ValidateKeyAndName(key, name);

        if (await db.Tables.CountAsync(t => t.DatabaseId == database.Id, ct) >= MaxTablesPerDatabase)
            throw new PraxyException(400, ErrorTypes.GeneralResourceLimitExceeded,
                $"This database already has the maximum of {MaxTablesPerDatabase} tables.");

        var id = Ids.NewUuid();
        var physicalName = PhysicalNaming.EntityName(key, id);
        var table = new TableDef
        {
            Id = id,
            DatabaseId = database.Id,
            Key = key,
            Name = name,
            PhysicalName = physicalName,
        };

        return await SchemaDdl.InTransactionAsync(db, async () =>
        {
            db.Tables.Add(table);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                throw new PraxyException(409, ErrorTypes.TableAlreadyExists,
                    $"A table with key '{key}' already exists in this database.");
            }

            var qualified = PhysicalNaming.QualifiedTable(database.SchemaName, physicalName);
            await SchemaDdl.ExecuteAsync(db,
                $"""
                 CREATE TABLE {qualified} (
                   "_id" uuid PRIMARY KEY,
                   "_created_at" timestamptz NOT NULL DEFAULT now(),
                   "_updated_at" timestamptz NOT NULL DEFAULT now()
                 )
                 """, ct);
            return table;
        }, ct);
    }

    public Task<List<TableDef>> ListAsync(Guid databaseId, CancellationToken ct) =>
        db.Tables.Where(t => t.DatabaseId == databaseId).OrderBy(t => t.CreatedAt).ToListAsync(ct);

    public async Task<TableDef> GetAsync(Guid databaseId, Guid tableId, CancellationToken ct) =>
        await db.Tables.FirstOrDefaultAsync(t => t.Id == tableId && t.DatabaseId == databaseId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.TableNotFound, "Table not found.");

    /// <summary>Name/enabled are metadata-only; row security is handled by <see cref="SetRowSecurityAsync"/>.</summary>
    public async Task<TableDef> UpdateAsync(TableDef table, string? name, bool? enabled, CancellationToken ct)
    {
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
                throw PraxyException.ArgumentInvalid("Invalid table payload.",
                    new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });
            table.Name = name.Trim();
        }
        if (enabled is not null)
            table.Enabled = enabled.Value;
        table.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return table;
    }

    /// <summary>
    /// Turning row security on creates the per-row permissions side table (architecture.md §4.2) if
    /// it doesn't already exist. Turning it off just flips the flag — existing row grants are kept,
    /// so re-enabling never loses data.
    /// </summary>
    public async Task<TableDef> SetRowSecurityAsync(Database database, TableDef table, bool enabled, CancellationToken ct)
    {
        if (table.RowSecurity == enabled)
            return table;

        if (enabled)
        {
            await SchemaDdl.InTransactionAsync(db, async () =>
            {
                table.RowSecurity = true;
                table.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                var permsTable = PhysicalNaming.QualifiedTable(
                    database.SchemaName, PhysicalNaming.PermsTableName(table.PhysicalName));
                var rowsTable = PhysicalNaming.QualifiedTable(database.SchemaName, table.PhysicalName);
                await SchemaDdl.ExecuteAsync(db,
                    $"""
                     CREATE TABLE IF NOT EXISTS {permsTable} (
                       row_id uuid NOT NULL REFERENCES {rowsTable}("_id") ON DELETE CASCADE,
                       action text NOT NULL,
                       role text NOT NULL,
                       PRIMARY KEY (row_id, action, role)
                     )
                     """, ct);
                await SchemaDdl.ExecuteAsync(db,
                    $"CREATE INDEX IF NOT EXISTS {PhysicalNaming.Quote($"{PhysicalNaming.PermsTableName(table.PhysicalName)}_action_role_idx")} " +
                    $"ON {permsTable} (action, role) INCLUDE (row_id)", ct);
            }, ct);
        }
        else
        {
            table.RowSecurity = false;
            table.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return table;
    }

    /// <summary>Always destructive; the caller (console) gets a typed-name confirm before setting <paramref name="force"/>.</summary>
    public async Task DeleteAsync(Database database, TableDef table, bool force, CancellationToken ct)
    {
        if (!force)
            throw new PraxyException(400, ErrorTypes.GeneralForceRequired,
                "Deleting a table is destructive. Pass force=true to confirm.");

        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            db.Tables.Remove(table);
            await db.SaveChangesAsync(ct);

            var qualified = PhysicalNaming.QualifiedTable(database.SchemaName, table.PhysicalName);
            await SchemaDdl.SetLockTimeoutAsync(db, TimeSpan.FromSeconds(5), ct);
            // CASCADE also drops the row-permissions side table (it FKs to this table).
            await SchemaDdl.ExecuteAsync(db, $"DROP TABLE IF EXISTS {qualified} CASCADE", ct);
        }, ct);
    }

    // ---- permissions ----------------------------------------------------------------------------

    public async Task<string[]> GetPermissionsAsync(Guid tableId, CancellationToken ct)
    {
        var rows = await db.TablePermissions
            .Where(p => p.TableId == tableId)
            .OrderBy(p => p.Action).ThenBy(p => p.Role)
            .ToListAsync(ct);
        return [.. rows.Select(p => PermissionStrings.Format(p.Action, p.Role))];
    }

    /// <summary>Full-replace semantics: the given set becomes the table's entire permission grant.</summary>
    public async Task<string[]> ReplacePermissionsAsync(Guid tableId, string[] permissions, CancellationToken ct)
    {
        IReadOnlyList<(string Action, string Role)> expanded;
        try
        {
            expanded = PermissionStrings.ParseAndExpand(permissions);
        }
        catch (FormatException ex)
        {
            throw PraxyException.ArgumentInvalid(ex.Message,
                new Dictionary<string, string[]> { ["permissions"] = [ex.Message] });
        }

        await db.TablePermissions.Where(p => p.TableId == tableId).ExecuteDeleteAsync(ct);
        db.TablePermissions.AddRange(expanded.Select(e => new TablePermission
        {
            TableId = tableId,
            Action = e.Action,
            Role = e.Role,
        }));
        await db.SaveChangesAsync(ct);
        return await GetPermissionsAsync(tableId, ct);
    }

    private static void ValidateKeyAndName(string key, string name)
    {
        var fields = new Dictionary<string, string[]>();
        if (!Keys.IsValid(key))
            fields["key"] = ["Must start with a letter and contain only letters, digits and underscores (max 64 chars)."];
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid table payload.", fields);
    }
}
