using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Tables.Quotas;
using Database = Praxy.Persistence.Entities.Database;

namespace Praxy.Tables;

/// <summary>
/// Databases: metadata insert + <c>CREATE SCHEMA px_&lt;hex32&gt;</c> in one transaction, per
/// architecture.md §4.1. <see cref="DeleteAsync"/> is the same trade in reverse — metadata row plus
/// <c>DROP SCHEMA ... CASCADE</c>, force-gated exactly like <see cref="TablesService.DeleteAsync"/>.
/// </summary>
public sealed class DatabasesService(PraxyDb db, CatalogCache cache, QuotaService quotas)
{
    public async Task<Database> CreateAsync(string projectId, string key, string name, CancellationToken ct)
    {
        ValidateKeyAndName(key, name);

        await quotas.EnsureDatabaseQuotaAsync(projectId, ct);

        var id = Ids.NewUuid();
        var schemaName = PhysicalNaming.SchemaName(id);
        var database = new Database
        {
            Id = id,
            ProjectId = projectId,
            Key = key,
            Name = name,
            SchemaName = schemaName,
        };

        return await SchemaDdl.InTransactionAsync(db, async () =>
        {
            db.Databases.Add(database);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                throw new PraxyException(409, ErrorTypes.DatabaseAlreadyExists,
                    $"A database with key '{key}' already exists.");
            }

            await SchemaDdl.ExecuteAsync(db, $"CREATE SCHEMA {PhysicalNaming.Quote(schemaName)}", ct);
            return database;
        }, ct);
    }

    public Task<List<Database>> ListAsync(string projectId, CancellationToken ct) =>
        db.Databases.Where(d => d.ProjectId == projectId).OrderBy(d => d.CreatedAt).ToListAsync(ct);

    public async Task<Database> GetAsync(string projectId, Guid databaseId, CancellationToken ct) =>
        await db.Databases.FirstOrDefaultAsync(d => d.Id == databaseId && d.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.DatabaseNotFound, "Database not found.");

    /// <summary>Name only — <see cref="Database.Key"/> and <see cref="Database.SchemaName"/> never change once created, same rule <c>TableDef</c> follows.</summary>
    public async Task<Database> UpdateAsync(Database database, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            throw PraxyException.ArgumentInvalid("Invalid database payload.",
                new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });
        database.Name = name.Trim();
        await db.SaveChangesAsync(ct);
        return database;
    }

    /// <summary>
    /// Always destructive, and more so than a table drop: <c>force=true</c> takes every table in the
    /// database with it. One <c>DROP SCHEMA ... CASCADE</c> does the physical half; the catalog rows
    /// (tables, columns, indexes, permissions) go with the metadata row on its FK cascade. Schema
    /// jobs have no FK to hang off — they are deleted explicitly, or the runner would keep picking up
    /// queued DDL for a schema that no longer exists.
    /// </summary>
    public async Task DeleteAsync(Database database, bool force, CancellationToken ct)
    {
        if (!force)
            throw new PraxyException(400, ErrorTypes.GeneralForceRequired,
                "Deleting a database drops every table it contains. Pass force=true to confirm.");

        // Read before the delete: the cache is keyed by table id, and after the cascade there is no
        // row left to learn those ids from.
        var tableIds = await db.Tables.Where(t => t.DatabaseId == database.Id).Select(t => t.Id).ToListAsync(ct);

        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            await db.SchemaJobs.Where(j => j.DatabaseId == database.Id).ExecuteDeleteAsync(ct);
            db.Databases.Remove(database);
            await db.SaveChangesAsync(ct);

            await SchemaDdl.SetLockTimeoutAsync(db, TimeSpan.FromSeconds(5), ct);
            await SchemaDdl.ExecuteAsync(db,
                $"DROP SCHEMA IF EXISTS {PhysicalNaming.Quote(database.SchemaName)} CASCADE", ct);
        }, ct);

        foreach (var tableId in tableIds)
            cache.Invalidate(tableId);
    }

    private static void ValidateKeyAndName(string key, string name)
    {
        var fields = new Dictionary<string, string[]>();
        if (!Keys.IsValid(key))
            fields["key"] = ["Must start with a letter and contain only letters, digits and underscores (max 64 chars)."];
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid database payload.", fields);
    }
}
