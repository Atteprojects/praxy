using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Praxy.Core;
using Praxy.Persistence.Entities;

namespace Praxy.Tables;

/// <summary>
/// A parsed <c>geo</c> column value. Not a bare scalar — <see cref="RowsService"/> binds it as two
/// parameters wrapped in <c>ST_MakePoint(...)::geography</c>, never a single <c>@param</c>
/// (docs/research/geo-nearby.md).
/// </summary>
public sealed record GeoPoint(double Lat, double Lng);

/// <summary>
/// The JSON ⇄ Postgres value boundary for row data: validates a wire JSON value against a
/// column's declared type (reusing the same format rules <see cref="ColumnTypes"/> applies to
/// DDL-time defaults) and converts it to a CLR value Npgsql can bind, or reads a CLR value back
/// out of a <see cref="NpgsqlDataReader"/> as JSON. Datetimes round-trip as ISO-8601 UTC strings
/// end-to-end, per CLAUDE.md's cross-phase rule.
/// </summary>
public static class RowValues
{
    /// <summary>Validates and converts one wire value for <paramref name="column"/>. Throws <see cref="FormatException"/> on a mismatch.</summary>
    public static object ToWriteValue(ColumnDef column, string key, JsonElement value)
    {
        if (!column.IsArray)
            return ToScalar(column, key, value);

        if (value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"'{key}' must be an array.");
        return column.Type switch
        {
            ColumnTypes.Integer => value.EnumerateArray().Select(e => RequireInteger(key, e)).ToArray(),
            ColumnTypes.Float => value.EnumerateArray().Select(e => RequireFloat(key, e)).ToArray(),
            ColumnTypes.Boolean => value.EnumerateArray().Select(e => RequireBool(key, e)).ToArray(),
            ColumnTypes.Datetime => value.EnumerateArray().Select(e => RequireDatetime(key, e)).ToArray(),
            ColumnTypes.String => value.EnumerateArray().Select(e => ValidateStringLength(key, RequireString(key, e), column.Size)).ToArray(),
            ColumnTypes.Email => value.EnumerateArray().Select(e => ValidateEmail(key, RequireString(key, e))).ToArray(),
            ColumnTypes.Url => value.EnumerateArray().Select(e => ValidateUrl(key, RequireString(key, e))).ToArray(),
            ColumnTypes.Ip => value.EnumerateArray().Select(e => ValidateIp(key, RequireString(key, e))).ToArray(),
            ColumnTypes.Enum => value.EnumerateArray().Select(e => ValidateEnum(key, RequireString(key, e), column.Options)).ToArray(),
            ColumnTypes.Relationship => value.EnumerateArray().Select(e => RequireRelationshipId(key, e)).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, "Unknown column type."),
        };
    }

    /// <summary>
    /// Public entry point for the query compiler, which validates filter values against a column's
    /// type the same way write values are validated. A <c>geo</c> column is the one exception: its
    /// value is a pair, not a scalar comparable via a generic operator, and <c>near</c> (the only
    /// filter that makes sense on it) parses its own three doubles directly rather than routing
    /// through here (docs/research/geo-nearby.md) — so every other operator on a geo column is
    /// rejected here, cleanly, before it could reach Postgres as a parameter type nothing understands.
    /// </summary>
    public static object ToFilterScalar(ColumnDef column, string key, JsonElement value) =>
        column.Type == ColumnTypes.Geo
            ? throw new FormatException($"'{key}' is a geo column — only the 'near' operator (and isNull/isNotNull) is supported.")
            : ToScalar(column, key, value);

    private static object ToScalar(ColumnDef column, string key, JsonElement value) => column.Type switch
    {
        ColumnTypes.Integer => RequireInteger(key, value),
        ColumnTypes.Float => RequireFloat(key, value),
        ColumnTypes.Boolean => RequireBool(key, value),
        ColumnTypes.Datetime => RequireDatetime(key, value),
        ColumnTypes.String => ValidateStringLength(key, RequireString(key, value), column.Size),
        ColumnTypes.Email => ValidateEmail(key, RequireString(key, value)),
        ColumnTypes.Url => ValidateUrl(key, RequireString(key, value)),
        ColumnTypes.Ip => ValidateIp(key, RequireString(key, value)),
        ColumnTypes.Enum => ValidateEnum(key, RequireString(key, value), column.Options),
        ColumnTypes.Relationship => RequireRelationshipId(key, value),
        ColumnTypes.Geo => ParseGeoPoint(key, value),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, "Unknown column type."),
    };

    /// <summary>Reads one column's value back out of a row reader as JSON. Null on SQL NULL.</summary>
    public static JsonNode? ReadValue(NpgsqlDataReader reader, int ordinal, ColumnDef column)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        return column.IsArray ? ReadArray(reader, ordinal, column.Type) : ReadScalar(reader, ordinal, column.Type);
    }

    private static JsonNode ReadScalar(NpgsqlDataReader reader, int ordinal, string type) => type switch
    {
        ColumnTypes.Integer => JsonValue.Create(reader.GetFieldValue<long>(ordinal)),
        ColumnTypes.Float => JsonValue.Create(reader.GetFieldValue<double>(ordinal)),
        ColumnTypes.Boolean => JsonValue.Create(reader.GetFieldValue<bool>(ordinal)),
        ColumnTypes.Datetime => JsonValue.Create(FormatDatetime(reader.GetFieldValue<DateTimeOffset>(ordinal))),
        // Npgsql maps uuid -> Guid natively; the string-fallback default below would format it as
        // Guid.ToString()'s dashed form, not Praxy's 32-hex-no-dashes wire shape.
        ColumnTypes.Relationship => JsonValue.Create(Ids.Wire(reader.GetFieldValue<Guid>(ordinal))),
        _ => JsonValue.Create(reader.GetFieldValue<string>(ordinal)),
    };

    private static JsonArray ReadArray(NpgsqlDataReader reader, int ordinal, string type) => type switch
    {
        ColumnTypes.Integer => ToJsonArray(reader.GetFieldValue<long[]>(ordinal), v => JsonValue.Create(v)),
        ColumnTypes.Float => ToJsonArray(reader.GetFieldValue<double[]>(ordinal), v => JsonValue.Create(v)),
        ColumnTypes.Boolean => ToJsonArray(reader.GetFieldValue<bool[]>(ordinal), v => JsonValue.Create(v)),
        ColumnTypes.Datetime => ToJsonArray(reader.GetFieldValue<DateTimeOffset[]>(ordinal), v => JsonValue.Create(FormatDatetime(v))),
        ColumnTypes.Relationship => ToJsonArray(reader.GetFieldValue<Guid[]>(ordinal), v => JsonValue.Create(Ids.Wire(v))),
        _ => ToJsonArray(reader.GetFieldValue<string[]>(ordinal), v => JsonValue.Create(v)),
    };

    private static JsonArray ToJsonArray<T>(T[] values, Func<T, JsonValue?> project) =>
        new([.. values.Select(v => (JsonNode?)project(v))]);

    public static string FormatDatetime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    // ---- per-type validation ----------------------------------------------------------------

    /// <summary>
    /// Postgres' <c>text</c> type cannot represent U+0000 at all — not an escaping problem,
    /// the wire protocol itself rejects it (<c>22021: invalid byte sequence for encoding "UTF8": 0x00</c>),
    /// so a parameterized value doesn't help here the way it does for injection. Found by Phase 9's
    /// query-compiler fuzz test reaching this through a filter value; the same string can arrive
    /// through row write data, so the check lives at the one shared string boundary both paths use.
    /// </summary>
    private static string RequireString(string key, JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String)
            throw new FormatException($"'{key}' must be a string.");
        var s = v.GetString()!;
        return s.Contains('\0')
            ? throw new FormatException($"'{key}' must not contain a null character.")
            : s;
    }

    private static long RequireInteger(string key, JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)
            ? i
            : throw new FormatException($"'{key}' must be a whole number.");

    private static double RequireFloat(string key, JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d
            : throw new FormatException($"'{key}' must be a number.");

    private static bool RequireBool(string key, JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException($"'{key}' must be a boolean."),
    };

    private static DateTimeOffset RequireDatetime(string key, JsonElement v) =>
        v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(
            v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt)
            ? dt
            : throw new FormatException($"'{key}' must be an ISO-8601 datetime string.");

    private static string ValidateStringLength(string key, string value, int? size) =>
        size is null || value.Length <= size
            ? value
            : throw new FormatException($"'{key}' must be at most {size} characters.");

    private static string ValidateEmail(string key, string value) =>
        value.Length <= 320 && value.Contains('@')
            ? value
            : throw new FormatException($"'{key}' must be a valid email address.");

    private static string ValidateUrl(string key, string value) =>
        value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out _)
            ? value
            : throw new FormatException($"'{key}' must be a valid absolute URL.");

    private static string ValidateIp(string key, string value) =>
        System.Net.IPAddress.TryParse(value, out _)
            ? value
            : throw new FormatException($"'{key}' must be a valid IP address.");

    private static string ValidateEnum(string key, string value, string optionsJson)
    {
        var elements = ColumnTypes.ExtractElements(optionsJson);
        return elements is not null && elements.Contains(value)
            ? value
            : throw new FormatException($"'{key}' must be one of the column's declared enum values.");
    }

    /// <summary>Shape only — does a referenced row actually exist is the async pre-pass's job (RowsService).</summary>
    private static Guid RequireRelationshipId(string key, JsonElement v) =>
        v.ValueKind == JsonValueKind.String && Ids.TryParseWire(v.GetString(), out var id)
            ? id
            : throw new FormatException($"'{key}' must be a valid row id.");

    /// <summary>
    /// <c>{"lat": &lt;number&gt;, "lng": &lt;number&gt;}</c> — an object, not GeoJSON's own
    /// <c>[lng, lat]</c> array convention, precisely to avoid that convention's well-known
    /// lat/lng-order footgun (docs/research/geo-nearby.md). Range-validated app-side (rather than
    /// left to Postgres/PostGIS's own <c>geography</c> cast, which would reject an out-of-range
    /// coordinate with a raw, un-caught exception) so a bad value surfaces as the same clean
    /// per-field 400 every other type's validation already produces.
    /// </summary>
    private static GeoPoint ParseGeoPoint(string key, JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object)
            throw new FormatException($"'{key}' must be a JSON object with 'lat' and 'lng' numbers.");
        if (!v.TryGetProperty("lat", out var latEl) || !v.TryGetProperty("lng", out var lngEl))
            throw new FormatException($"'{key}' must have 'lat' and 'lng' properties.");

        var lat = RequireFloat(key, latEl);
        var lng = RequireFloat(key, lngEl);
        if (lat is < -90 or > 90)
            throw new FormatException($"'{key}.lat' must be between -90 and 90.");
        if (lng is < -180 or > 180)
            throw new FormatException($"'{key}.lng' must be between -180 and 180.");
        return new GeoPoint(lat, lng);
    }
}
