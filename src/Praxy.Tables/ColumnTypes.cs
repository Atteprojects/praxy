using System.Globalization;
using System.Text.Json;

namespace Praxy.Tables;

/// <summary>
/// The nine column types from roadmap.md's Phase 2 scope, plus <see cref="Relationship"/> added by
/// the table-relationships initiative (docs/research/table-relationships.md). Each maps to a base
/// Postgres type; semantic types (email/url/ip) are <c>text</c> plus an application-level
/// validation rule, per architecture.md §4.4 — keeping validation out of the database means a rule
/// change never needs a table rewrite.
/// </summary>
public static class ColumnTypes
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Float = "float";
    public const string Boolean = "boolean";
    public const string Datetime = "datetime";
    public const string Email = "email";
    public const string Url = "url";
    public const string Ip = "ip";
    public const string Enum = "enum";

    /// <summary>A scalar <c>uuid</c> (real FK, <c>ON DELETE RESTRICT</c>) or <c>uuid[]</c> pointing at another table's rows.</summary>
    public const string Relationship = "relationship";

    /// <summary>
    /// A scalar <c>geography(Point, 4326)</c> — wire shape <c>{"lat","lng"}</c>, no array support, no
    /// default (docs/research/geo-nearby.md). Unlike every other type, its value isn't a bare JSON
    /// scalar and its write/read SQL isn't a single bound parameter — see RowValues/RowsService.
    /// </summary>
    public const string Geo = "geo";

    public static readonly IReadOnlyList<string> All =
        [String, Integer, Float, Boolean, Datetime, Email, Url, Ip, Enum, Relationship, Geo];

    public static bool IsValid(string type) => All.Contains(type);

    /// <summary>The scalar (non-array) Postgres type for a column type. <c>size</c> only matters for <see cref="String"/>.</summary>
    public static string PostgresType(string type, int? size) => type switch
    {
        String => $"varchar({RequireSize(size)})",
        Integer => "bigint",
        Float => "double precision",
        Boolean => "boolean",
        Datetime => "timestamptz",
        Email or Url or Ip or Enum => "text",
        Relationship => "uuid",
        Geo => "geography(Point, 4326)",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown column type."),
    };

    /// <summary>Appends <c>[]</c> for array columns — native Postgres arrays, not a junction table.</summary>
    public static string PostgresStorageType(string type, int? size, bool isArray)
    {
        var scalar = PostgresType(type, size);
        return isArray ? $"{scalar}[]" : scalar;
    }

    private static int RequireSize(int? size) => size is > 0
        ? size.Value
        : throw new ArgumentException("The 'string' type requires a positive 'size'.", nameof(size));

    public const int MaxStringSize = 1_000_000;
    public const int MaxEnumElements = 200;
    public const int MaxEnumElementLength = 255;

    /// <summary>
    /// Formats a single JSON value as a safe SQL literal for a <c>DEFAULT</c> clause. Postgres DDL
    /// does not accept bind parameters for defaults, so every value is validated against the
    /// column's declared type and emitted as a controlled literal — never raw user text.
    /// </summary>
    public static string FormatLiteral(string type, JsonElement value) => type switch
    {
        String or Email or Url or Ip or Enum => QuoteStringLiteral(RequireString(value, type)),
        Integer => RequireInteger(value).ToString(CultureInfo.InvariantCulture),
        Float => RequireFloat(value).ToString("R", CultureInfo.InvariantCulture),
        Boolean => RequireBool(value) ? "true" : "false",
        Datetime => QuoteStringLiteral(RequireDatetime(value).ToString("O", CultureInfo.InvariantCulture)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown column type."),
    };

    /// <summary>Formats a JSON array default as a Postgres <c>ARRAY[...]</c> literal, element by element.</summary>
    public static string FormatArrayLiteral(string type, JsonElement arrayValue)
    {
        if (arrayValue.ValueKind != JsonValueKind.Array)
            throw new FormatException("An array column's default must be a JSON array.");
        var items = arrayValue.EnumerateArray().Select(e => FormatLiteral(type, e));
        return $"ARRAY[{string.Join(",", items)}]::{PostgresType(type, null)}[]";
    }

    private static string QuoteStringLiteral(string s) => "'" + s.Replace("'", "''") + "'";

    private static string RequireString(JsonElement v, string type) => v.ValueKind == JsonValueKind.String
        ? v.GetString()!
        : throw new FormatException($"A '{type}' default must be a JSON string.");

    private static long RequireInteger(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)
            ? i
            : throw new FormatException("An 'integer' default must be a whole JSON number.");

    private static double RequireFloat(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d
            : throw new FormatException("A 'float' default must be a JSON number.");

    private static bool RequireBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException("A 'boolean' default must be a JSON boolean."),
    };

    private static DateTimeOffset RequireDatetime(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(
            v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : throw new FormatException("A 'datetime' default must be an ISO-8601 JSON string.");

    /// <summary>Pulls an enum column's declared <c>elements</c> back out of its jsonb <c>options</c> blob.</summary>
    public static string[]? ExtractElements(string optionsJson)
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
