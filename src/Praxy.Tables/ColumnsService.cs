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
/// Columns: sync DDL in the same transaction as the metadata write. The row byte budget is
/// recomputed against every existing column plus the one being added, and named offenders are
/// reported when it's exceeded (roadmap.md).
/// </summary>
public sealed class ColumnsService(PraxyDb db)
{
    public const int MaxColumnsPerTable = 200;

    public async Task<ColumnDef> CreateAsync(
        Database database, TableDef table, string type, string key, bool required, bool isArray,
        int? size, string[]? elements, JsonElement? defaultValue, CancellationToken ct)
    {
        if (!ColumnTypes.IsValid(type))
            throw PraxyException.NotFound(ErrorTypes.ColumnInvalid, $"Unknown column type '{type}'.");
        ValidateKey(key);
        ValidateTypeShape(type, size, elements);

        if (await db.Columns.CountAsync(c => c.TableId == table.Id, ct) >= MaxColumnsPerTable)
            throw new PraxyException(400, ErrorTypes.GeneralResourceLimitExceeded,
                $"This table already has the maximum of {MaxColumnsPerTable} columns.");

        await AssertRowBudgetAsync(table.Id, extra: (key, type, size, isArray, elements), ct);

        var id = Ids.NewUuid();
        var physicalName = PhysicalNaming.EntityName(key, id);
        var storageType = ColumnTypes.PostgresStorageType(type, size, isArray);

        string? defaultLiteral = null;
        if (defaultValue is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } dv)
        {
            defaultLiteral = FormatAndValidateDefault(type, isArray, elements, dv);
        }

        var position = await db.Columns.Where(c => c.TableId == table.Id).CountAsync(ct);
        var column = new ColumnDef
        {
            Id = id,
            TableId = table.Id,
            Key = key,
            Type = type,
            PhysicalName = physicalName,
            Required = required,
            IsArray = isArray,
            Size = type == ColumnTypes.String ? size : null,
            DefaultValue = defaultValue is { ValueKind: not JsonValueKind.Undefined } dv2 ? dv2.GetRawText() : null,
            Options = type == ColumnTypes.Enum ? JsonSerializer.Serialize(new { elements }) : "{}",
            Status = "available",
            Position = position,
        };

        return await SchemaDdl.InTransactionAsync(db, async () =>
        {
            db.Columns.Add(column);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                throw new PraxyException(409, ErrorTypes.ColumnAlreadyExists,
                    $"A column with key '{key}' already exists on this table.");
            }

            var qualified = PhysicalNaming.QualifiedTable(database.SchemaName, table.PhysicalName);
            var notNull = required ? " NOT NULL" : "";
            var withDefault = defaultLiteral is not null ? $" DEFAULT {defaultLiteral}" : "";
            await SchemaDdl.ExecuteAsync(db,
                $"ALTER TABLE {qualified} ADD COLUMN {PhysicalNaming.Quote(physicalName)} {storageType}{withDefault}{notNull}",
                ct);
            return column;
        }, ct);
    }

    public Task<List<ColumnDef>> ListAsync(Guid tableId, CancellationToken ct) =>
        db.Columns.Where(c => c.TableId == tableId).OrderBy(c => c.Position).ToListAsync(ct);

    public async Task<ColumnDef> GetAsync(Guid tableId, Guid columnId, CancellationToken ct) =>
        await db.Columns.FirstOrDefaultAsync(c => c.Id == columnId && c.TableId == tableId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.ColumnNotFound, "Column not found.");

    /// <summary>
    /// Rename is metadata-only — the physical column is untouched (architecture.md §4.1).
    /// Toggling <paramref name="required"/> is a plain <c>SET/DROP NOT NULL</c>; tables have no
    /// rows before Phase 3, so there is nothing to backfill and no force gate is needed here.
    /// </summary>
    public async Task<ColumnDef> UpdateAsync(
        Database database, TableDef table, ColumnDef column, string? newKey, bool? required, CancellationToken ct)
    {
        var renaming = newKey is not null && newKey != column.Key;
        var togglingRequired = required is not null && required.Value != column.Required;
        if (!renaming && !togglingRequired)
            return column;

        if (renaming)
            ValidateKey(newKey!);

        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            if (renaming)
                column.Key = newKey!;
            if (togglingRequired)
                column.Required = required!.Value;
            column.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                throw new PraxyException(409, ErrorTypes.ColumnAlreadyExists,
                    $"A column with key '{newKey}' already exists on this table.");
            }

            if (togglingRequired)
            {
                var qualified = PhysicalNaming.QualifiedTable(database.SchemaName, table.PhysicalName);
                var quotedCol = PhysicalNaming.Quote(column.PhysicalName);
                await SchemaDdl.SetLockTimeoutAsync(db, TimeSpan.FromSeconds(5), ct);
                await SchemaDdl.ExecuteAsync(db,
                    $"ALTER TABLE {qualified} ALTER COLUMN {quotedCol} {(required!.Value ? "SET" : "DROP")} NOT NULL",
                    ct);
            }
        }, ct);
        return column;
    }

    /// <summary>
    /// Refuses to drop a column an index depends on (fix the index first) and requires
    /// <paramref name="force"/> for required columns — architecture.md §4.5's destructive-change rule.
    /// </summary>
    public async Task DeleteAsync(Database database, TableDef table, ColumnDef column, bool force, CancellationToken ct)
    {
        var dependentIndex = await db.Indexes
            .Where(i => i.TableId == table.Id)
            .ToListAsync(ct);
        var blocking = dependentIndex.FirstOrDefault(i => i.Columns.Contains(column.Key));
        if (blocking is not null)
            throw new PraxyException(409, ErrorTypes.IndexDependency,
                $"Column '{column.Key}' is used by index '{blocking.Key}'. Delete the index first.");

        if (column.Required && !force)
            throw new PraxyException(400, ErrorTypes.GeneralForceRequired,
                $"Column '{column.Key}' is required. Pass force=true to confirm dropping it.");

        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            db.Columns.Remove(column);
            await db.SaveChangesAsync(ct);

            var qualified = PhysicalNaming.QualifiedTable(database.SchemaName, table.PhysicalName);
            await SchemaDdl.SetLockTimeoutAsync(db, TimeSpan.FromSeconds(5), ct);
            await SchemaDdl.ExecuteAsync(db,
                $"ALTER TABLE {qualified} DROP COLUMN {PhysicalNaming.Quote(column.PhysicalName)}", ct);
        }, ct);
    }

    // ---- validation -----------------------------------------------------------------------------

    private static void ValidateKey(string key)
    {
        if (!Keys.IsValid(key))
            throw PraxyException.ArgumentInvalid("Invalid column payload.",
                new Dictionary<string, string[]>
                {
                    ["key"] = ["Must start with a letter and contain only letters, digits and underscores (max 64 chars)."],
                });
    }

    private static void ValidateTypeShape(string type, int? size, string[]? elements)
    {
        var fields = new Dictionary<string, string[]>();
        if (type == ColumnTypes.String && size is not (> 0 and <= ColumnTypes.MaxStringSize))
            fields["size"] = [$"Required for 'string' columns: a positive integer up to {ColumnTypes.MaxStringSize}."];
        if (type == ColumnTypes.Enum)
        {
            if (elements is null or { Length: 0 } or { Length: > ColumnTypes.MaxEnumElements })
                fields["elements"] = [$"Required for 'enum' columns: 1 to {ColumnTypes.MaxEnumElements} values."];
            else if (elements.Any(e => string.IsNullOrEmpty(e) || e.Length > ColumnTypes.MaxEnumElementLength))
                fields["elements"] = [$"Each value must be 1 to {ColumnTypes.MaxEnumElementLength} characters."];
            else if (elements.Distinct().Count() != elements.Length)
                fields["elements"] = ["Values must be unique."];
        }
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid column payload.", fields);
    }

    private static string FormatAndValidateDefault(string type, bool isArray, string[]? elements, JsonElement value)
    {
        try
        {
            if (type == ColumnTypes.Enum)
                AssertEnumDefault(elements!, isArray, value);
            return isArray ? ColumnTypes.FormatArrayLiteral(type, value) : ColumnTypes.FormatLiteral(type, value);
        }
        catch (FormatException ex)
        {
            throw PraxyException.ArgumentInvalid(ex.Message,
                new Dictionary<string, string[]> { ["default"] = [ex.Message] });
        }
    }

    private static void AssertEnumDefault(string[] elements, bool isArray, JsonElement value)
    {
        var values = isArray ? value.EnumerateArray().Select(e => e.GetString()) : [value.GetString()];
        foreach (var v in values)
            if (v is null || !elements.Contains(v))
                throw new FormatException($"Default value '{v}' is not one of the declared enum elements.");
    }

    /// <summary>Row byte budget across every existing column plus the one being added, per architecture.md §4.4.</summary>
    private async Task AssertRowBudgetAsync(
        Guid tableId, (string Key, string Type, int? Size, bool IsArray, string[]? Elements) extra, CancellationToken ct)
    {
        var existing = await db.Columns.Where(c => c.TableId == tableId)
            .Select(c => new { c.Key, c.Type, c.Size, c.IsArray, c.Options })
            .ToListAsync(ct);

        var estimates = existing
            .Select(c => new RowByteBudget.ColumnEstimate(
                c.Key, RowByteBudget.EstimateBytes(c.Type, c.Size, c.IsArray, ExtractElements(c.Options))))
            .Append(new RowByteBudget.ColumnEstimate(
                extra.Key, RowByteBudget.EstimateBytes(extra.Type, extra.Size, extra.IsArray, extra.Elements)))
            .ToList();

        try
        {
            RowByteBudget.Assert(estimates);
        }
        catch (RowSizeExceededException ex)
        {
            var offenders = string.Join(", ", ex.LargestColumns.Select(c => $"{c.Key} (~{c.Bytes}B)"));
            throw new PraxyException(400, ErrorTypes.RowSizeExceeded,
                $"Row would need ~{ex.TotalBytes} bytes, over the {ex.BudgetBytes}-byte budget. Largest columns: {offenders}.");
        }
    }

    private static string[]? ExtractElements(string optionsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            return doc.RootElement.TryGetProperty("elements", out var el) && el.ValueKind == JsonValueKind.Array
                ? [.. el.EnumerateArray().Select(e => e.GetString() ?? "")]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
