using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Tables;

/// <summary>
/// Row CRUD (roadmap.md Phase 3): create/get/list/update/delete against the raw <c>px_*</c> row
/// table, permission-filtered per architecture.md §4.2 and written through the outbox from this
/// phase on (CLAUDE.md's cross-phase rule). Every statement shares the ambient EF transaction on
/// <see cref="PraxyDb"/> — the same "raw Npgsql through the EF connection" pattern
/// <see cref="SchemaDdl"/> already established for DDL, so a row write and its outbox event commit
/// or roll back together.
/// </summary>
public sealed class RowsService(PraxyDb db, CatalogCache cache, IEventBus events)
{
    private const string IdCol = "\"_id\"";

    public async Task<CatalogEntry> ResolveTableAsync(string projectId, Guid databaseId, Guid tableId, CancellationToken ct)
    {
        var entry = await cache.GetAsync(tableId, ct);
        if (entry.Database.ProjectId != projectId || entry.Table.DatabaseId != databaseId)
            throw PraxyException.NotFound(ErrorTypes.TableNotFound, "Table not found.");
        return entry;
    }

    // ---- create -----------------------------------------------------------------------------

    public async Task<JsonObject> CreateAsync(
        CatalogEntry entry, string? rowId, JsonElement data, string[]? permissions,
        string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        if (!bypassPermissions && !entry.TableRoles(PermissionStrings.Create).Intersect(callerRoles).Any())
            throw PraxyException.Unauthorized("Not permitted to create rows on this table.");

        RequirePermissionsAllowed(entry, permissions);
        var rowPermissions = permissions is null ? [] : RowPermissions.Parse(permissions);

        Guid id;
        if (rowId is not null)
        {
            if (!Ids.TryParseWire(rowId, out id))
                throw PraxyException.ArgumentInvalid("Invalid row id.",
                    new Dictionary<string, string[]> { ["rowId"] = ["Must be a valid UUID."] });
        }
        else
        {
            id = Ids.NewUuid();
        }

        if (data.ValueKind != JsonValueKind.Object)
            throw PraxyException.ArgumentInvalid("Row data must be a JSON object.",
                new Dictionary<string, string[]> { ["data"] = ["Must be a JSON object."] });

        var (columns, values, fields) = ResolveInsertValues(entry, data);
        if (fields.Count > 0)
            throw new PraxyException(400, ErrorTypes.RowInvalidStructure, "Invalid row data.", fields);

        var qualified = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, entry.Table.PhysicalName);
        var colNames = new List<string> { IdCol };
        var placeholders = new List<string> { "@__id" };
        var parameters = new List<NpgsqlParameter> { new("__id", id) };
        for (var i = 0; i < columns.Count; i++)
        {
            var pName = $"v{i}";
            colNames.Add(PhysicalNaming.Quote(columns[i].PhysicalName));
            placeholders.Add("@" + pName);
            parameters.Add(new NpgsqlParameter(pName, values[i]));
        }

        var sql = $"INSERT INTO {qualified} ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", placeholders)}) " +
                   $"RETURNING {ReturningColumns(entry)}";

        var (row, readRoles) = await SchemaDdl.InTransactionAsync<(JsonObject Row, string[] ReadRoles)>(db, async () =>
        {
            await AssertRelationshipTargetsExistAsync(columns.Zip(values), ct);

            List<JsonObject> inserted;
            try
            {
                inserted = await QueryAsync(sql, parameters, r => BuildRowJson(entry, r, null), ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                throw new PraxyException(409, ErrorTypes.RowAlreadyExists, "A row with this id already exists.");
            }
            var created = inserted[0];

            if (rowPermissions.Count > 0)
                await ReplaceRowPermissionsAsync(entry, id, rowPermissions, ct);
            created["$permissions"] = ToPermissionArray(rowPermissions);

            var roles = ComputeCreateReadRoles(entry, rowPermissions);
            await WriteOutboxAsync(entry, "create", id, roles, ct);
            return (created, roles);
        }, ct);

        await PublishAsync(entry, "create", id, readRoles, ct);
        return row;
    }

    // ---- relationship existence pre-pass ------------------------------------------------------

    /// <summary>
    /// Rejects a write up front with a clean 400 if any relationship value points at a row that
    /// doesn't exist — batched one <c>SELECT ... WHERE _id = ANY(@ids)</c> per distinct target
    /// table (never per column, never per id), regardless of how many relationship columns target
    /// it. Existence-only: no permission check — linking to a row requires only that it exist, not
    /// that the writer can read it (docs/research/table-relationships.md).
    /// </summary>
    private async Task AssertRelationshipTargetsExistAsync(
        IEnumerable<(ColumnDef Column, object Value)> relationshipValues, CancellationToken ct)
    {
        var byTarget = new Dictionary<Guid, HashSet<Guid>>();
        var perColumn = new List<(ColumnDef Column, Guid[] Ids)>();
        foreach (var (column, value) in relationshipValues)
        {
            if (column.Type != ColumnTypes.Relationship || value is DBNull)
                continue;
            // Orphaned (Phase 2): its target table was force-deleted, TargetTableId is now null.
            // Nothing left to validate against — accept the raw id, same as any other uuid write.
            if (column.TargetTableId is not { } targetTableId)
                continue;
            var ids = column.IsArray ? (Guid[])value : [(Guid)value];
            perColumn.Add((column, ids));
            if (!byTarget.TryGetValue(targetTableId, out var set))
                byTarget[targetTableId] = set = [];
            foreach (var id in ids)
                set.Add(id);
        }
        if (byTarget.Count == 0)
            return;

        var missingByTarget = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var (targetTableId, ids) in byTarget)
        {
            var target = await cache.GetAsync(targetTableId, ct);
            var qualified = PhysicalNaming.QualifiedTable(target.Database.SchemaName, target.Table.PhysicalName);
            var found = await QueryAsync($"SELECT {IdCol} FROM {qualified} WHERE {IdCol} = ANY(@ids)",
                [new NpgsqlParameter("ids", ids.ToArray())], r => r.GetFieldValue<Guid>(0), ct);
            var missing = ids.Except(found).ToHashSet();
            if (missing.Count > 0)
                missingByTarget[targetTableId] = missing;
        }
        if (missingByTarget.Count == 0)
            return;

        var fields = new Dictionary<string, string[]>();
        foreach (var (column, ids) in perColumn)
        {
            if (!missingByTarget.TryGetValue(column.TargetTableId!.Value, out var missing))
                continue;
            var missingWire = ids.Where(missing.Contains).Select(Ids.Wire).ToArray();
            if (missingWire.Length > 0)
                fields[column.Key] = [$"References a row that doesn't exist: {string.Join(", ", missingWire)}."];
        }
        throw new PraxyException(400, ErrorTypes.RelationshipTargetNotFound, "Invalid row data.", fields);
    }

    // ---- get --------------------------------------------------------------------------------

    public async Task<JsonObject> GetAsync(
        CatalogEntry entry, Guid rowId, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var predicate = QueryCompiler.CompilePermissionPredicate(entry, PermissionStrings.Read, callerRoles, bypassPermissions);
        var qualified = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, entry.Table.PhysicalName);
        var parameters = ToParams(predicate.Params, ("row_id", rowId));
        var sql = $"SELECT {SelectColumns(entry)} FROM {qualified} AS t WHERE t.{IdCol} = @row_id AND ({predicate.Sql})";

        var rows = await QueryAsync(sql, parameters, r => BuildRowJson(entry, r, null), ct);
        if (rows.Count == 0)
            throw PraxyException.NotFound(ErrorTypes.RowNotFound, "Row not found.");

        var row = rows[0];
        row["$permissions"] = ToPermissionArray(await GetRowPermissionsAsync(entry, rowId, ct));
        return row;
    }

    // ---- list -------------------------------------------------------------------------------

    public async Task<(int? Total, List<JsonObject> Rows)> ListAsync(
        CatalogEntry entry, IReadOnlyList<string> rawQueries, string[] callerRoles, bool bypassPermissions,
        bool includeTotal, CancellationToken ct)
    {
        var parsed = QueryDsl.Parse(rawQueries);
        var compiled = QueryCompiler.CompileList(entry, parsed, callerRoles, bypassPermissions, includeTotal);

        var rows = await QueryAsync(compiled.Sql, ToParams(compiled.Params),
            r => BuildRowJson(entry, r, compiled.SelectedKeys), ct);
        if (compiled.Reversed)
            rows.Reverse();

        await AttachPermissionsAsync(entry, rows, ct);

        int? total = null;
        if (includeTotal && compiled.CountSql is not null)
        {
            var scalar = await ScalarAsync(compiled.CountSql, ToParams(compiled.CountParams!), ct);
            total = checked((int)(long)scalar!);
        }

        return (total, rows);
    }

    // ---- update (genuinely partial) ----------------------------------------------------------

    public async Task<JsonObject> UpdateAsync(
        CatalogEntry entry, Guid rowId, JsonElement? data, string[]? permissions,
        string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        if (data is null && permissions is null)
            return await GetAsync(entry, rowId, callerRoles, bypassPermissions, ct);
        if (data is { ValueKind: not JsonValueKind.Object })
            throw PraxyException.ArgumentInvalid("Row data must be a JSON object.",
                new Dictionary<string, string[]> { ["data"] = ["Must be a JSON object."] });

        RequirePermissionsAllowed(entry, permissions);

        var setClauses = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        var fields = new Dictionary<string, string[]>();
        var relationshipUpdates = new List<(ColumnDef Column, object Value)>();
        var seq = 0;

        if (data is { } d)
        {
            foreach (var prop in d.EnumerateObject())
            {
                var column = entry.FindColumn(prop.Name);
                if (column is null)
                {
                    fields[prop.Name] = ["Unknown column."];
                    continue;
                }
                var pName = $"s{seq++}";
                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    if (column.Required)
                    {
                        fields[prop.Name] = ["Required — cannot be null."];
                        continue;
                    }
                    setClauses.Add($"{PhysicalNaming.Quote(column.PhysicalName)} = @{pName}");
                    parameters.Add(new NpgsqlParameter(pName, DBNull.Value));
                }
                else
                {
                    try
                    {
                        var value = RowValues.ToWriteValue(column, prop.Name, prop.Value);
                        setClauses.Add($"{PhysicalNaming.Quote(column.PhysicalName)} = @{pName}");
                        parameters.Add(new NpgsqlParameter(pName, value));
                        if (column.Type == ColumnTypes.Relationship)
                            relationshipUpdates.Add((column, value));
                    }
                    catch (FormatException ex)
                    {
                        fields[prop.Name] = [ex.Message];
                    }
                }
            }
        }
        if (fields.Count > 0)
            throw new PraxyException(400, ErrorTypes.RowInvalidStructure, "Invalid row data.", fields);
        if (setClauses.Count == 0 && permissions is null)
            return await GetAsync(entry, rowId, callerRoles, bypassPermissions, ct);

        setClauses.Add("\"_updated_at\" = now()");

        var predicate = QueryCompiler.CompilePermissionPredicate(entry, PermissionStrings.Update, callerRoles, bypassPermissions);
        var qualified = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, entry.Table.PhysicalName);
        parameters.Add(new NpgsqlParameter("row_id", rowId));
        parameters.AddRange(predicate.Params.Select(p => new NpgsqlParameter(p.Name, p.Value)));
        var sql = $"UPDATE {qualified} AS t SET {string.Join(", ", setClauses)} " +
                   $"WHERE t.{IdCol} = @row_id AND ({predicate.Sql}) RETURNING {ReturningColumns(entry)}";

        var (row, readRoles) = await SchemaDdl.InTransactionAsync<(JsonObject Row, string[] ReadRoles)>(db, async () =>
        {
            await AssertRelationshipTargetsExistAsync(relationshipUpdates, ct);

            var updated = await QueryAsync(sql, parameters, r => BuildRowJson(entry, r, null), ct);
            if (updated.Count == 0)
                throw PraxyException.NotFound(ErrorTypes.RowNotFound, "Row not found.");
            var updatedRow = updated[0];

            IReadOnlyList<string> rowPerms;
            if (permissions is not null)
            {
                var parsedPerms = RowPermissions.Parse(permissions);
                await ReplaceRowPermissionsAsync(entry, rowId, parsedPerms, ct);
                rowPerms = [.. parsedPerms.Select(p => PermissionStrings.Format(p.Action, p.Role))];
            }
            else
            {
                rowPerms = await GetRowPermissionsAsync(entry, rowId, ct);
            }
            updatedRow["$permissions"] = ToPermissionArray(rowPerms);

            var roles = await ComputeReadRolesAsync(entry, rowId, ct);
            await WriteOutboxAsync(entry, "update", rowId, roles, ct);
            return (updatedRow, roles);
        }, ct);

        await PublishAsync(entry, "update", rowId, readRoles, ct);
        return row;
    }

    // ---- delete -------------------------------------------------------------------------------

    public async Task DeleteAsync(
        CatalogEntry entry, Guid rowId, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var predicate = QueryCompiler.CompilePermissionPredicate(entry, PermissionStrings.Delete, callerRoles, bypassPermissions);
        var qualified = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, entry.Table.PhysicalName);
        var parameters = ToParams(predicate.Params, ("row_id", rowId));
        var sql = $"DELETE FROM {qualified} AS t WHERE t.{IdCol} = @row_id AND ({predicate.Sql}) RETURNING t.{IdCol}";

        var readRoles = await SchemaDdl.InTransactionAsync(db, async () =>
        {
            // No point computing roles (below) or running the DELETE for a row that's about to be
            // rejected — the array pre-check runs first.
            await AssertNotArrayReferencedAsync(entry, rowId, ct);

            // Roles must be captured before the row (and its __perms rows, via ON DELETE CASCADE)
            // disappear — this is what makes the DELETE event authorizable later.
            var roles = await ComputeReadRolesAsync(entry, rowId, ct);

            List<Guid> deleted;
            try
            {
                deleted = await QueryAsync(sql, parameters, r => r.GetFieldValue<Guid>(0), ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "23503")
            {
                throw new PraxyException(409, ErrorTypes.RowReferenced,
                    "This row is still referenced by another row's relationship column.");
            }
            if (deleted.Count == 0)
                throw PraxyException.NotFound(ErrorTypes.RowNotFound, "Row not found.");

            await WriteOutboxAsync(entry, "delete", rowId, roles, ct);
            return roles;
        }, ct);

        await PublishAsync(entry, "delete", rowId, readRoles, ct);
    }

    /// <summary>
    /// Application-level check for the array case — Postgres can't constrain array elements with a
    /// native FK, so there's no free 23503 here. Checks every array relationship column anywhere
    /// that targets this table (there may be more than one, on more than one other table), each via
    /// its own <c>EXISTS ... = ANY(...)</c> against that column's own table. Accepted as a
    /// check-then-delete race under read-committed isolation (docs/research/table-relationships.md,
    /// Phase 2 non-goals) — application-level enforcement, not a real constraint.
    /// </summary>
    private async Task AssertNotArrayReferencedAsync(CatalogEntry entry, Guid rowId, CancellationToken ct)
    {
        var referencingColumns = await db.Columns
            .Where(c => c.TargetTableId == entry.Table.Id && c.IsArray)
            .ToListAsync(ct);

        foreach (var column in referencingColumns)
        {
            var referencing = await cache.GetAsync(column.TableId, ct);
            var referencingQualified = PhysicalNaming.QualifiedTable(referencing.Database.SchemaName, referencing.Table.PhysicalName);
            var referencingCol = PhysicalNaming.Quote(column.PhysicalName);
            var sql = $"SELECT EXISTS (SELECT 1 FROM {referencingQualified} WHERE @row_id = ANY({referencingCol}))";
            var exists = (bool)(await ScalarAsync(sql, [new NpgsqlParameter("row_id", rowId)], ct))!;
            if (exists)
                throw new PraxyException(409, ErrorTypes.RowReferenced,
                    $"This row is still referenced by column '{column.Key}' on table '{referencing.Table.Key}'.");
        }
    }

    // ---- row-level permissions --------------------------------------------------------------

    private static void RequirePermissionsAllowed(CatalogEntry entry, string[]? permissions)
    {
        if (permissions is { Length: > 0 } && !entry.Table.RowSecurity)
            throw PraxyException.ArgumentInvalid("Row permissions require row_security to be enabled on this table.",
                new Dictionary<string, string[]> { ["permissions"] = ["Enable row_security on this table to grant row-level permissions."] });
    }

    private async Task ReplaceRowPermissionsAsync(
        CatalogEntry entry, Guid rowId, IReadOnlyList<(string Action, string Role)> permissions, CancellationToken ct)
    {
        if (!entry.Table.RowSecurity)
            return;
        var permsQualified = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, PhysicalNaming.PermsTableName(entry.Table.PhysicalName));
        string deleteSql = $"DELETE FROM {permsQualified} WHERE row_id = @row_id";
        await db.Database.ExecuteSqlRawAsync(deleteSql, [new NpgsqlParameter("row_id", rowId)], ct);
        string insertSql = $"INSERT INTO {permsQualified} (row_id, action, role) VALUES (@row_id, @action, @role)";
        foreach (var (action, role) in permissions)
        {
            await db.Database.ExecuteSqlRawAsync(insertSql,
                [new NpgsqlParameter("row_id", rowId), new NpgsqlParameter("action", action), new NpgsqlParameter("role", role)], ct);
        }
    }

    private async Task<string[]> GetRowPermissionsAsync(CatalogEntry entry, Guid rowId, CancellationToken ct)
    {
        if (!entry.Table.RowSecurity)
            return [];
        var permsQualified = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, PhysicalNaming.PermsTableName(entry.Table.PhysicalName));
        var sql = $"SELECT action, role FROM {permsQualified} WHERE row_id = @row_id";
        var formatted = await QueryAsync(sql, [new NpgsqlParameter("row_id", rowId)],
            r => PermissionStrings.Format(r.GetString(0), r.GetString(1)), ct);
        return [.. formatted];
    }

    private async Task AttachPermissionsAsync(CatalogEntry entry, List<JsonObject> rows, CancellationToken ct)
    {
        if (!entry.Table.RowSecurity || rows.Count == 0)
        {
            foreach (var row in rows)
                row["$permissions"] = new JsonArray();
            return;
        }

        var idByRow = new Dictionary<JsonObject, Guid>();
        foreach (var row in rows)
        {
            var wireId = row["$id"]!.GetValue<string>();
            Ids.TryParseWire(wireId, out var g);
            idByRow[row] = g;
        }

        var permsQualified = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, PhysicalNaming.PermsTableName(entry.Table.PhysicalName));
        var sql = $"SELECT row_id, action, role FROM {permsQualified} WHERE row_id = ANY(@ids)";
        var flat = await QueryAsync(sql, [new NpgsqlParameter("ids", idByRow.Values.ToArray())],
            r => (RowId: r.GetFieldValue<Guid>(0), Action: r.GetString(1), Role: r.GetString(2)), ct);
        var byId = flat.GroupBy(f => f.RowId)
            .ToDictionary(g => g.Key, g => g.Select(f => PermissionStrings.Format(f.Action, f.Role)).ToArray());

        foreach (var row in rows)
            row["$permissions"] = ToPermissionArray(byId.TryGetValue(idByRow[row], out var p) ? p : []);
    }

    private static string[] ComputeCreateReadRoles(CatalogEntry entry, IReadOnlyList<(string Action, string Role)> rowPermissions) =>
    [
        .. entry.TableRoles(PermissionStrings.Read)
            .Concat(rowPermissions.Where(p => p.Action == PermissionStrings.Read).Select(p => p.Role))
            .Distinct(),
    ];

    private async Task<string[]> ComputeReadRolesAsync(CatalogEntry entry, Guid rowId, CancellationToken ct)
    {
        var roles = new List<string>(entry.TableRoles(PermissionStrings.Read));
        if (entry.Table.RowSecurity)
        {
            var permsQualified = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, PhysicalNaming.PermsTableName(entry.Table.PhysicalName));
            var sql = $"SELECT role FROM {permsQualified} WHERE row_id = @row_id AND action = @action";
            var rowRoles = await QueryAsync(sql,
                [new NpgsqlParameter("row_id", rowId), new NpgsqlParameter("action", PermissionStrings.Read)],
                r => r.GetString(0), ct);
            roles.AddRange(rowRoles);
        }
        return [.. roles.Distinct()];
    }

    // ---- outbox / realtime --------------------------------------------------------------------

    /// <summary>
    /// Durable copy in <c>praxy.events</c>, written inside the same transaction as the row change
    /// (Phase 6 reads this; nothing consumes it yet, per the outbox pattern). <paramref name="readRoles"/>
    /// travel in the payload too, computed pre-commit — a DELETE has no row left to re-query later.
    /// </summary>
    private async Task WriteOutboxAsync(CatalogEntry entry, string action, Guid rowId, string[] readRoles, CancellationToken ct)
    {
        var (type, payload) = BuildEvent(entry, action, rowId, readRoles);
        db.Events.Add(new OutboxEvent
        {
            Id = Ids.NewUuid(),
            ProjectId = entry.Database.ProjectId,
            Type = type,
            Payload = payload.ToJsonString(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Best-effort in-process fan-out, published after commit (architecture.md §7) — Phase 4's realtime consumer attaches here later.</summary>
    private async Task PublishAsync(CatalogEntry entry, string action, Guid rowId, string[] readRoles, CancellationToken ct)
    {
        var (type, payload) = BuildEvent(entry, action, rowId, readRoles);
        await events.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, entry.Database.ProjectId, type, readRoles, payload), ct);
    }

    private static (string Type, JsonObject Payload) BuildEvent(CatalogEntry entry, string action, Guid rowId, string[] readRoles)
    {
        var type = $"databases.{Ids.Wire(entry.Database.Id)}.tables.{Ids.Wire(entry.Table.Id)}.rows.{Ids.Wire(rowId)}.{action}";
        var payload = new JsonObject
        {
            ["databaseId"] = Ids.Wire(entry.Database.Id),
            ["tableId"] = Ids.Wire(entry.Table.Id),
            ["rowId"] = Ids.Wire(rowId),
            ["roles"] = new JsonArray([.. readRoles.Select(r => (JsonNode?)JsonValue.Create(r))]),
        };
        return (type, payload);
    }

    // ---- row shape / value materialization ---------------------------------------------------

    private static JsonObject BuildRowJson(CatalogEntry entry, NpgsqlDataReader reader, string[]? selectedKeys)
    {
        var id = reader.GetFieldValue<Guid>(0);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(1);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(2);

        var obj = new JsonObject
        {
            ["$id"] = Ids.Wire(id),
            ["$tableId"] = Ids.Wire(entry.Table.Id),
            ["$databaseId"] = Ids.Wire(entry.Database.Id),
            ["$createdAt"] = RowValues.FormatDatetime(createdAt),
            ["$updatedAt"] = RowValues.FormatDatetime(updatedAt),
            ["$permissions"] = new JsonArray(),
        };

        var columns = selectedKeys is null ? entry.Columns : selectedKeys.Select(k => entry.FindColumn(k)!).ToList();
        var ordinal = 3;
        foreach (var column in columns)
        {
            obj[column.Key] = RowValues.ReadValue(reader, ordinal, column);
            ordinal++;
        }
        return obj;
    }

    private static (List<ColumnDef> Columns, List<object> Values, Dictionary<string, string[]> Fields) ResolveInsertValues(
        CatalogEntry entry, JsonElement data)
    {
        var columns = new List<ColumnDef>();
        var values = new List<object>();
        var fields = new Dictionary<string, string[]>();
        var seen = new HashSet<string>();

        foreach (var prop in data.EnumerateObject())
        {
            seen.Add(prop.Name);
            var column = entry.FindColumn(prop.Name);
            if (column is null)
            {
                fields[prop.Name] = ["Unknown column."];
                continue;
            }
            if (prop.Value.ValueKind == JsonValueKind.Null)
            {
                if (column.Required)
                {
                    fields[prop.Name] = ["Required — cannot be null."];
                    continue;
                }
                columns.Add(column);
                values.Add(DBNull.Value);
                continue;
            }
            try
            {
                var value = RowValues.ToWriteValue(column, prop.Name, prop.Value);
                columns.Add(column);
                values.Add(value);
            }
            catch (FormatException ex)
            {
                fields[prop.Name] = [ex.Message];
            }
        }

        foreach (var column in entry.Columns)
        {
            if (seen.Contains(column.Key))
                continue;
            if (column.Required && column.DefaultValue is null)
                fields[column.Key] = ["Required."];
        }

        return (columns, values, fields);
    }

    private static string ReturningColumns(CatalogEntry entry)
    {
        const string systemCols = "\"_id\", \"_created_at\", \"_updated_at\"";
        return entry.Columns.Count == 0
            ? systemCols
            : systemCols + ", " + string.Join(", ", entry.Columns.Select(c => PhysicalNaming.Quote(c.PhysicalName)));
    }

    private static string SelectColumns(CatalogEntry entry)
    {
        const string systemCols = "t.\"_id\", t.\"_created_at\", t.\"_updated_at\"";
        return entry.Columns.Count == 0
            ? systemCols
            : systemCols + ", " + string.Join(", ", entry.Columns.Select(c => $"t.{PhysicalNaming.Quote(c.PhysicalName)}"));
    }

    private static JsonArray ToPermissionArray(IReadOnlyList<(string Action, string Role)> permissions) =>
        new([.. permissions.Select(p => (JsonNode?)JsonValue.Create(PermissionStrings.Format(p.Action, p.Role)))]);

    private static JsonArray ToPermissionArray(IReadOnlyList<string> permissions) =>
        new([.. permissions.Select(p => (JsonNode?)JsonValue.Create(p))]);

    // ---- raw SQL plumbing ---------------------------------------------------------------------

    private async Task<List<T>> QueryAsync<T>(
        string sql, IEnumerable<NpgsqlParameter> parameters, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (db.Database.CurrentTransaction is { } tx)
            cmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
        cmd.Parameters.AddRange(parameters.ToArray());

        var results = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(map(reader));
        return results;
    }

    private async Task<object?> ScalarAsync(string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (db.Database.CurrentTransaction is { } tx)
            cmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
        cmd.Parameters.AddRange(parameters.ToArray());
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static List<NpgsqlParameter> ToParams(IEnumerable<SqlParam> sqlParams, params (string Name, object Value)[] extra)
    {
        var list = sqlParams.Select(p => new NpgsqlParameter(p.Name, p.Value)).ToList();
        list.AddRange(extra.Select(e => new NpgsqlParameter(e.Name, e.Value)));
        return list;
    }
}
