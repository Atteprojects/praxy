using System.Text.Json;
using Praxy.Core;
using Praxy.Persistence.Entities;

namespace Praxy.Tables;

/// <summary>One SQL parameter, kept as a plain tuple until a concrete <c>NpgsqlCommand</c> binds it.</summary>
public readonly record struct SqlParam(string Name, object Value);

public sealed record CompiledListQuery(
    string Sql, IReadOnlyList<SqlParam> Params,
    string? CountSql, IReadOnlyList<SqlParam>? CountParams,
    string[]? SelectedKeys, bool Reversed);

public sealed record CompiledPredicate(string Sql, IReadOnlyList<SqlParam> Params);

/// <summary>
/// AST → parameterized SQL, per architecture.md §4.6's non-negotiables: identifiers only ever from
/// metadata lookup (never a request string), values always bound as parameters, keyset pagination
/// as a <c>(sort_column, _id) &gt; (@v, @id)</c> tuple compare. Permission filtering
/// (architecture.md §4.2) is folded into the same WHERE clause: table-level grant short-circuits to
/// <c>TRUE</c>, otherwise an <c>EXISTS</c> against the row's <c>__perms</c> side table when
/// <c>row_security</c> is on, otherwise <c>FALSE</c>.
/// </summary>
public static class QueryCompiler
{
    private const string IdType = "id";
    private static readonly ColumnDef IdColumn = new()
    { Id = Guid.Empty, TableId = Guid.Empty, Key = "$id", Type = IdType, PhysicalName = "_id" };
    private static readonly ColumnDef CreatedAtColumn = new()
    { Id = Guid.Empty, TableId = Guid.Empty, Key = "$createdAt", Type = ColumnTypes.Datetime, PhysicalName = "_created_at" };
    private static readonly ColumnDef UpdatedAtColumn = new()
    { Id = Guid.Empty, TableId = Guid.Empty, Key = "$updatedAt", Type = ColumnTypes.Datetime, PhysicalName = "_updated_at" };

    /// <summary>The permission predicate alone, for single-row get/update/delete (combined by the caller with an <c>_id = @id</c> clause).</summary>
    public static CompiledPredicate CompilePermissionPredicate(
        CatalogEntry entry, string action, string[] callerRoles, bool bypassPermissions)
    {
        var b = new Builder(entry);
        var sql = b.PermissionPredicate(action, callerRoles, bypassPermissions);
        return new CompiledPredicate(sql, b.Params);
    }

    public static CompiledListQuery CompileList(
        CatalogEntry entry, IReadOnlyList<ParsedQuery> queries, string[] callerRoles, bool bypassPermissions,
        bool includeTotal)
    {
        string[]? selectFields = null;
        ColumnDef? sortColumn = null;
        var orderAscending = true;
        (double Lat, double Lng)? sortNearPoint = null;
        int? limit = null;
        int? offset = null;
        string? cursorAfterId = null;
        string? cursorBeforeId = null;
        var filterNodes = new List<ParsedQuery>();

        foreach (var q in queries)
        {
            switch (q.Method)
            {
                case "select":
                    selectFields = MergeSelect(selectFields, q.Values);
                    break;
                case "orderAsc" or "orderDesc" or "orderNear":
                    if (sortColumn is null) // first order query wins; architecture.md's cursor tuple is single-column
                    {
                        sortColumn = ResolveColumn(entry, q.Attribute!);
                        if (q.Method == "orderNear")
                        {
                            if (sortColumn.Type != ColumnTypes.Geo)
                                throw QueryDsl.Invalid($"'orderNear' isn't supported on '{sortColumn.Key}'.", "queries",
                                    "'orderNear' only works on 'geo' attributes.");
                            _ = entry.SpatialIndexFor(sortColumn.Key)
                                ?? throw QueryDsl.Invalid($"'{sortColumn.Key}' has no spatial index.", "queries",
                                    $"Create a spatial index on '{sortColumn.Key}' before using 'orderNear' — never a silent sequential scan.");
                            sortNearPoint = (RequireNearValue(q.Values[0], "lat"), RequireNearValue(q.Values[1], "lng"));
                        }
                        else
                        {
                            orderAscending = q.Method == "orderAsc";
                        }
                    }
                    break;
                case "limit":
                    limit = RequireIntValue(q.Values, "limit");
                    break;
                case "offset":
                    offset = RequireIntValue(q.Values, "offset");
                    break;
                case "cursorAfter":
                    cursorAfterId = RequireStringValue(q.Values, "cursorAfter");
                    break;
                case "cursorBefore":
                    cursorBeforeId = RequireStringValue(q.Values, "cursorBefore");
                    break;
                default:
                    filterNodes.Add(q);
                    break;
            }
        }

        if (limit is { } l && l is < 1 or > QueryDsl.MaxLimit)
            throw QueryDsl.Invalid("Invalid limit.", "limit", $"'limit' must be between 1 and {QueryDsl.MaxLimit}.");
        var effectiveLimit = limit ?? QueryDsl.DefaultLimit;

        if (offset is { } o && o is < 0 or > QueryDsl.MaxOffset)
            throw QueryDsl.Invalid("Invalid offset.", "offset", $"'offset' must be between 0 and {QueryDsl.MaxOffset}.");

        if (cursorAfterId is not null && cursorBeforeId is not null)
            throw QueryDsl.Invalid("Conflicting cursors.", "cursorAfter", "Only one of 'cursorAfter'/'cursorBefore' may be used.");
        if ((cursorAfterId ?? cursorBeforeId) is not null && offset is not null)
            throw QueryDsl.Invalid("Conflicting pagination.", "offset", "'offset' can't be combined with cursor pagination.");

        var effectiveSortColumn = sortColumn ?? IdColumn;
        var qualifiedTable = PhysicalNaming.QualifiedTable(entry.Database.SchemaName, entry.Table.PhysicalName);
        var sortColQuoted = PhysicalNaming.Quote(effectiveSortColumn.PhysicalName);
        var idColQuoted = PhysicalNaming.Quote("_id");

        // The direction actually scanned in SQL: cursorBefore scans backwards to grab the nearest
        // page, then the caller reverses the in-memory result to restore the requested order.
        var reversed = cursorBeforeId is not null;
        var scanAscending = reversed ? !orderAscending : orderAscending;

        var select = new Builder(entry);

        // AddParam exactly once each for lat/lng — the returned "@pN" names are reused verbatim
        // everywhere the near-point expression is needed (subselect, tuple-compare, ORDER BY), since
        // Npgsql binds parameters by name.
        string? nearPointExpr = null;
        if (sortNearPoint is { } near)
        {
            var lngParam = select.AddParam(near.Lng);
            var latParam = select.AddParam(near.Lat);
            nearPointExpr = $"ST_MakePoint({lngParam}, {latParam})::geography";
        }

        // For a plain column sort this is just the quoted column reference (today's behavior,
        // unchanged); for orderNear it's the KNN distance expression. `alias` is "" in the unaliased
        // cursor subselect and "t." in the two aliased sites (tuple-compare, ORDER BY).
        string SortKeyExpr(string alias) => nearPointExpr is null
            ? $"{alias}{sortColQuoted}"
            : $"{alias}{sortColQuoted} <-> {nearPointExpr}";

        var wherePredicate = select.FullPredicate(filterNodes, "read", callerRoles, bypassPermissions);

        var cursorId = cursorAfterId ?? cursorBeforeId;
        if (cursorId is not null)
        {
            if (!Ids.TryParseWire(cursorId, out var cursorGuid))
                throw QueryDsl.Invalid("Invalid cursor.", cursorAfterId is not null ? "cursorAfter" : "cursorBefore",
                    "The cursor must be a valid row id.");
            var cursorParam = select.AddParam(cursorGuid);
            var cmp = scanAscending ? ">" : "<";
            var subSelect = $"(SELECT {SortKeyExpr("")} FROM {qualifiedTable} WHERE {idColQuoted} = {cursorParam})";
            wherePredicate =
                $"{wherePredicate} AND (({SortKeyExpr("t.")}, t.{idColQuoted}) {cmp} ({subSelect}, {cursorParam}))";
        }

        var (selectList, selectedKeys) = BuildSelectList(entry, selectFields);
        var dir = scanAscending ? "ASC" : "DESC";
        var orderClause = $"ORDER BY {SortKeyExpr("t.")} {dir}, t.{idColQuoted} {dir}";
        var limitParam = select.AddParam(effectiveLimit);
        var sql = $"SELECT {selectList} FROM {qualifiedTable} AS t WHERE {wherePredicate} {orderClause} LIMIT {limitParam}";
        if (cursorId is null)
            sql += $" OFFSET {select.AddParam(offset ?? 0)}";

        string? countSql = null;
        IReadOnlyList<SqlParam>? countParams = null;
        if (includeTotal)
        {
            var count = new Builder(entry);
            var countPredicate = count.FullPredicate(filterNodes, "read", callerRoles, bypassPermissions);
            countSql = $"SELECT COUNT(*) FROM {qualifiedTable} AS t WHERE {countPredicate}";
            countParams = count.Params;
        }

        return new CompiledListQuery(sql, select.Params, countSql, countParams, selectedKeys, reversed);
    }

    // ---- shared predicate/filter compilation -------------------------------------------------

    private sealed class Builder(CatalogEntry entry)
    {
        public List<SqlParam> Params { get; } = [];
        private int _seq;

        public string AddParam(object value)
        {
            var name = $"p{_seq++}";
            Params.Add(new SqlParam(name, value));
            return "@" + name;
        }

        public string PermissionPredicate(string action, string[] callerRoles, bool bypassPermissions)
        {
            if (bypassPermissions)
                return "TRUE";

            var tableRoles = entry.TableRoles(action);
            if (tableRoles.Intersect(callerRoles).Any())
                return "TRUE";
            if (!entry.Table.RowSecurity)
                return "FALSE";

            var permsQualified = PhysicalNaming.QualifiedTable(
                entry.Database.SchemaName, PhysicalNaming.PermsTableName(entry.Table.PhysicalName));
            var actionParam = AddParam(action);
            var rolesParam = AddParam(callerRoles);
            var idQuoted = PhysicalNaming.Quote("_id");
            return $"EXISTS (SELECT 1 FROM {permsQualified} __p WHERE __p.row_id = t.{idQuoted} " +
                   $"AND __p.action = {actionParam} AND __p.role = ANY({rolesParam}))";
        }

        public string FullPredicate(List<ParsedQuery> filterNodes, string action, string[] callerRoles, bool bypassPermissions)
        {
            var permission = PermissionPredicate(action, callerRoles, bypassPermissions);
            if (filterNodes.Count == 0)
                return permission;
            var filters = string.Join(" AND ", filterNodes.Select(CompileFilterNode));
            return $"{permission} AND ({filters})";
        }

        private string CompileFilterNode(ParsedQuery q)
        {
            if (q.Method is "and" or "or")
            {
                var parts = q.Children.Select(CompileFilterNode);
                return "(" + string.Join(q.Method == "and" ? " AND " : " OR ", parts) + ")";
            }

            var column = ResolveColumn(entry, q.Attribute!);
            var colSql = $"t.{PhysicalNaming.Quote(column.PhysicalName)}";

            switch (q.Method)
            {
                case "isNull":
                    return $"{colSql} IS NULL";
                case "isNotNull":
                    return $"{colSql} IS NOT NULL";
                case "equal":
                case "notEqual":
                {
                    var arr = BuildArray(q.Values.Select(v => ConvertValue(column, v)).ToList());
                    var p = AddParam(arr);
                    return q.Method == "equal" ? $"{colSql} = ANY({p})" : $"NOT ({colSql} = ANY({p}))";
                }
                case "lessThan":
                    return $"{colSql} < {AddParam(ConvertValue(column, q.Values[0]))}";
                case "lessThanEqual":
                    return $"{colSql} <= {AddParam(ConvertValue(column, q.Values[0]))}";
                case "greaterThan":
                    return $"{colSql} > {AddParam(ConvertValue(column, q.Values[0]))}";
                case "greaterThanEqual":
                    return $"{colSql} >= {AddParam(ConvertValue(column, q.Values[0]))}";
                case "between":
                    return $"{colSql} BETWEEN {AddParam(ConvertValue(column, q.Values[0]))} AND {AddParam(ConvertValue(column, q.Values[1]))}";
                case "startsWith" or "endsWith" or "contains":
                    return CompileStringOrArrayOp(q.Method, colSql, column, q.Values[0]);
                case "search":
                    return CompileSearch(column, q.Values[0]);
                case "near":
                    return CompileNear(column, colSql, q.Values);
                default:
                    throw QueryDsl.Invalid($"'{q.Method}' can't be used as a filter.", "queries",
                        $"'{q.Method}' is a pagination/select/order method, not a filter.");
            }
        }

        private string CompileStringOrArrayOp(string method, string colSql, ColumnDef column, JsonElement valueEl)
        {
            if (column.Type == IdType)
                throw QueryDsl.Invalid($"'{method}' isn't supported on '{column.Key}'.", "queries", "Not supported on row ids.");

            if (column.IsArray)
            {
                if (method != "contains")
                    throw QueryDsl.Invalid($"'{method}' isn't supported on array attribute '{column.Key}'.", "queries",
                        "Only 'contains' (element membership) works on array attributes.");
                var elementParam = AddParam(ConvertValue(column, valueEl));
                return $"{elementParam} = ANY({colSql})";
            }

            var raw = ConvertValue(column, valueEl) as string
                ?? throw QueryDsl.Invalid($"'{method}' requires a string value.", "queries", $"'{column.Key}' needs a string value for '{method}'.");
            var escaped = EscapeLike(raw);
            var pattern = method switch
            {
                "startsWith" => escaped + "%",
                "endsWith" => "%" + escaped,
                _ => "%" + escaped + "%",
            };
            return $"{colSql} ILIKE {AddParam(pattern)} ESCAPE '\\'";
        }

        private string CompileSearch(ColumnDef column, JsonElement valueEl)
        {
            if (column.IsArray || column.Type is IdType or ColumnTypes.Datetime or ColumnTypes.Integer
                or ColumnTypes.Float or ColumnTypes.Boolean or ColumnTypes.Relationship or ColumnTypes.Geo)
                throw QueryDsl.Invalid($"'search' isn't supported on '{column.Key}'.", "queries",
                    "'search' only works on text-like attributes with a fulltext index.");
            var index = entry.FulltextIndexFor(column.Key)
                ?? throw QueryDsl.Invalid($"'{column.Key}' has no fulltext index.", "queries",
                    $"Create a fulltext index on '{column.Key}' before using 'search' — never a silent ILIKE scan.");
            var ftsCol = PhysicalNaming.Quote(PhysicalNaming.FulltextColumnName(index.PhysicalName));
            var raw = ConvertValue(column, valueEl) as string
                ?? throw QueryDsl.Invalid("'search' requires a string value.", "queries", "'search' needs a string value.");
            return $"t.{ftsCol} @@ websearch_to_tsquery('simple', {AddParam(raw)})";
        }

        /// <summary>
        /// <c>near(lat, lng, radiusMeters)</c> — a pure radius filter, never automatic
        /// nearest-first sorting (docs/research/geo-nearby.md's explicit non-goal). Its three
        /// values describe a query point and a distance, not a value *of* the column's own type, so
        /// unlike every other filter method they're parsed as plain doubles here rather than routed
        /// through <see cref="ConvertValue"/>/<see cref="RowValues.ToFilterScalar"/> — the same
        /// three-independent-<see cref="AddParam"/> shape <c>between</c> already establishes as
        /// precedent for a multi-value method. Requires a declared spatial index, mirroring
        /// <see cref="CompileSearch"/>'s fulltext-index requirement exactly: reject rather than let
        /// an unindexed <c>ST_DWithin</c> silently sequential-scan.
        /// </summary>
        private string CompileNear(ColumnDef column, string colSql, JsonElement[] values)
        {
            if (column.Type != ColumnTypes.Geo)
                throw QueryDsl.Invalid($"'near' isn't supported on '{column.Key}'.", "queries",
                    "'near' only works on 'geo' attributes.");
            _ = entry.SpatialIndexFor(column.Key)
                ?? throw QueryDsl.Invalid($"'{column.Key}' has no spatial index.", "queries",
                    $"Create a spatial index on '{column.Key}' before using 'near' — never a silent sequential scan.");

            var lat = RequireNearValue(values[0], "lat");
            var lng = RequireNearValue(values[1], "lng");
            var radiusMeters = RequireNearValue(values[2], "radiusMeters");
            return $"ST_DWithin({colSql}, ST_MakePoint({AddParam(lng)}, {AddParam(lat)})::geography, {AddParam(radiusMeters)})";
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Shared by <c>near</c>'s filter and <c>orderNear</c>'s sort-key — both parse a query point's
    /// lat/lng as plain doubles rather than routing through <see cref="ConvertValue"/>, since neither is a
    /// value *of* the column's own type.</summary>
    private static double RequireNearValue(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var d))
            throw QueryDsl.Invalid("'near' requires numeric values.", "queries", $"'near' {label} must be a number.");
        return d;
    }

    private static ColumnDef ResolveColumn(CatalogEntry entry, string attribute) => attribute switch
    {
        "$id" => IdColumn,
        "$createdAt" => CreatedAtColumn,
        "$updatedAt" => UpdatedAtColumn,
        _ => entry.FindColumn(attribute)
             ?? throw QueryDsl.Invalid($"Unknown attribute '{attribute}'.", "queries", $"'{attribute}' is not a column on this table."),
    };

    /// <summary>
    /// Converts one filter value the same way a write value is validated (<see cref="RowValues.ToFilterScalar"/>)
    /// — but a filter value comes from a query string, not a trusted write body, so a type mismatch
    /// here (found by Phase 9's query-compiler fuzz test: <c>equal("views", "not-a-number")</c> and
    /// friends reached Postgres as a bad parameter and surfaced as an unhandled 500) is a normal,
    /// expected client mistake. <see cref="FormatException"/> is caught and rethrown as the same
    /// <see cref="QueryDsl.Invalid"/> 400 every other query-shape error in this compiler already uses,
    /// never left to fall through to the generic 500 handler.
    /// </summary>
    private static object ConvertValue(ColumnDef column, JsonElement value)
    {
        try
        {
            if (column.Type != IdType)
                return RowValues.ToFilterScalar(column, column.Key, value);
            if (value.ValueKind != JsonValueKind.String || !Ids.TryParseWire(value.GetString(), out var g))
                throw new FormatException($"'{column.Key}' must be a valid row id.");
            return g;
        }
        catch (FormatException ex)
        {
            throw QueryDsl.Invalid(ex.Message, "queries", ex.Message);
        }
    }

    private static object BuildArray(List<object> items)
    {
        if (items.Count == 0)
            return Array.Empty<string>();
        var elementType = items[0].GetType();
        var arr = Array.CreateInstance(elementType, items.Count);
        for (var i = 0; i < items.Count; i++)
            arr.SetValue(items[i], i);
        return arr;
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static (string SelectList, string[]? Keys) BuildSelectList(CatalogEntry entry, string[]? requestedKeys)
    {
        const string systemCols = "t.\"_id\", t.\"_created_at\", t.\"_updated_at\"";
        if (requestedKeys is null)
        {
            if (entry.Columns.Count == 0)
                return (systemCols, null);
            var all = string.Join(", ", entry.Columns.Select(GeoAwareColumnExpr));
            return ($"{systemCols}, {all}", null);
        }

        var resolved = requestedKeys.Select(key => entry.FindColumn(key)
            ?? throw QueryDsl.Invalid($"Unknown select attribute '{key}'.", "select", $"'{key}' is not a column on this table.")).ToList();
        if (resolved.Count == 0)
            return (systemCols, requestedKeys);
        var cols = string.Join(", ", resolved.Select(GeoAwareColumnExpr));
        return ($"{systemCols}, {cols}", requestedKeys);
    }

    /// <summary>
    /// One column's SELECT expression, always <c>t.</c>-qualified. Every type but geo is the bare
    /// column reference; geo's single physical column expands into two selected expressions
    /// (<c>ST_X</c>/<c>ST_Y</c> on the column cast to <c>geometry</c>), aliased
    /// <c>{physicalName}_lng</c>/<c>{physicalName}_lat</c> in that order — the same shape
    /// <see cref="RowsService"/>'s own read path (<c>GeoAwareColumnExpr</c>, <c>ReadGeoPoint</c>)
    /// relies on for <c>Get</c>/<c>Expand</c>, kept independent here since <c>List</c>'s SQL is built
    /// entirely in this compiler, not <see cref="RowsService"/>.
    /// </summary>
    private static string GeoAwareColumnExpr(ColumnDef c)
    {
        var quoted = PhysicalNaming.Quote(c.PhysicalName);
        if (c.Type != ColumnTypes.Geo)
            return $"t.{quoted}";
        var lngAlias = PhysicalNaming.Quote($"{c.PhysicalName}_lng");
        var latAlias = PhysicalNaming.Quote($"{c.PhysicalName}_lat");
        return $"ST_X(t.{quoted}::geometry) AS {lngAlias}, ST_Y(t.{quoted}::geometry) AS {latAlias}";
    }

    private static string[] MergeSelect(string[]? existing, JsonElement[] values)
    {
        var strs = values.Select(v => v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw QueryDsl.Invalid("'select' values must be strings.", "select", "Each selected attribute must be a string.")).ToArray();
        var merged = existing is null ? strs : [.. existing, .. strs];
        merged = [.. merged.Distinct()];
        if (merged.Length > QueryDsl.MaxSelectFields)
            throw QueryDsl.Invalid("Too many select fields.", "select", $"'select' may name at most {QueryDsl.MaxSelectFields} attributes.");
        return merged;
    }

    private static int RequireIntValue(JsonElement[] values, string field)
    {
        if (values.Length != 1 || values[0].ValueKind != JsonValueKind.Number || !values[0].TryGetInt32(out var i))
            throw QueryDsl.Invalid($"'{field}' requires a single integer value.", field, $"'{field}' requires a single integer value.");
        return i;
    }

    private static string RequireStringValue(JsonElement[] values, string field)
    {
        if (values.Length != 1 || values[0].ValueKind != JsonValueKind.String)
            throw QueryDsl.Invalid($"'{field}' requires a single string value.", field, $"'{field}' requires a single string value.");
        return values[0].GetString()!;
    }
}
