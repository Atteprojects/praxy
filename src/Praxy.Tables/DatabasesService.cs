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
/// architecture.md §4.1. No delete endpoint this phase — the roadmap only asks for create/list/get.
/// </summary>
public sealed class DatabasesService(PraxyDb db, QuotaService quotas)
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
