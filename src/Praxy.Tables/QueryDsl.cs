using System.Text.Json;
using Praxy.Core.Errors;

namespace Praxy.Tables;

/// <summary>One parsed query-wire object: <c>{"method":"equal","attribute":"title","values":["Hello"]}</c>.</summary>
public sealed record ParsedQuery(string Method, string? Attribute, JsonElement[] Values, ParsedQuery[] Children);

/// <summary>
/// Parses the appwrite-api.md JSON-per-query wire format into an AST, enforcing the caps
/// architecture.md §4.6 requires: 100 queries × 4096 chars, nesting depth 3, limit default 25/max
/// 100. Never touches SQL — that's <see cref="QueryCompiler"/>'s job, once every attribute has been
/// checked against real column metadata.
/// </summary>
public static class QueryDsl
{
    public const int MaxQueries = 100;
    public const int MaxQueryChars = 4096;
    public const int MaxDepth = 3;
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;
    public const int MaxOffset = 100_000;
    public const int MaxSelectFields = 100;

    private static readonly string[] LogicalMethods = ["and", "or"];

    /// <summary>Methods that don't take an <c>attribute</c> — they operate on the query as a whole.</summary>
    private static readonly string[] NoAttributeMethods =
        ["select", "limit", "offset", "cursorAfter", "cursorBefore", "and", "or"];

    public static readonly string[] AllMethods =
    [
        "equal", "notEqual", "lessThan", "lessThanEqual", "greaterThan", "greaterThanEqual", "between",
        "isNull", "isNotNull", "startsWith", "endsWith", "contains", "search",
        "select", "orderAsc", "orderDesc", "limit", "offset", "cursorAfter", "cursorBefore", "and", "or",
    ];

    public static List<ParsedQuery> Parse(IReadOnlyList<string> raw)
    {
        if (raw.Count > MaxQueries)
            throw Invalid("Too many queries.", "queries", $"At most {MaxQueries} queries are allowed per request.");

        var result = new List<ParsedQuery>(raw.Count);
        foreach (var text in raw)
        {
            if (text.Length > MaxQueryChars)
                throw Invalid("Query too long.", "queries", $"Each query must be at most {MaxQueryChars} characters.");

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw Invalid("Malformed query JSON.", "queries", "Each query must be valid JSON.");
            }

            using (doc)
                result.Add(ParseNode(doc.RootElement, depth: 1));
        }
        return result;
    }

    private static ParsedQuery ParseNode(JsonElement el, int depth)
    {
        if (depth > MaxDepth)
            throw Invalid("Query nesting too deep.", "queries", $"'and'/'or' may nest at most {MaxDepth} levels deep.");
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            throw Invalid("Malformed query.", "queries", "Each query needs a string 'method'.");

        var method = methodEl.GetString()!;
        if (!AllMethods.Contains(method))
            throw Invalid($"Unknown query method '{method}'.", "queries", $"'{method}' is not a supported query method.");

        string? attribute = el.TryGetProperty("attribute", out var attrEl) && attrEl.ValueKind == JsonValueKind.String
            ? attrEl.GetString()
            : null;
        if (!NoAttributeMethods.Contains(method) && string.IsNullOrEmpty(attribute))
            throw Invalid($"'{method}' requires an 'attribute'.", "queries", $"'{method}' requires an 'attribute'.");

        if (LogicalMethods.Contains(method))
        {
            if (!el.TryGetProperty("values", out var childrenEl) || childrenEl.ValueKind != JsonValueKind.Array ||
                childrenEl.GetArrayLength() < 1)
                throw Invalid($"'{method}' requires nested queries.", "queries",
                    $"'{method}' requires at least one nested query in 'values'.");
            var children = childrenEl.EnumerateArray().Select(c => ParseNode(c, depth + 1)).ToArray();
            return new ParsedQuery(method, null, [], children);
        }

        var values = el.TryGetProperty("values", out var valuesEl) && valuesEl.ValueKind == JsonValueKind.Array
            ? valuesEl.EnumerateArray().Select(v => v.Clone()).ToArray()
            : [];
        ValidateArity(method, values.Length);
        return new ParsedQuery(method, attribute, values, []);
    }

    private static void ValidateArity(string method, int count)
    {
        var ok = method switch
        {
            "lessThan" or "lessThanEqual" or "greaterThan" or "greaterThanEqual"
                or "startsWith" or "endsWith" or "contains" or "search" => count == 1,
            "between" => count == 2,
            "equal" or "notEqual" => count >= 1,
            "select" => count is >= 1 and <= MaxSelectFields,
            "limit" or "offset" or "cursorAfter" or "cursorBefore" => count == 1,
            _ => true, // isNull/isNotNull/orderAsc/orderDesc: values ignored if present
        };
        if (!ok)
            throw Invalid($"'{method}' has an invalid number of values.", "queries",
                $"'{method}' received an unexpected number of values.");
    }

    internal static PraxyException Invalid(string message, string field, string detail) =>
        new(400, ErrorTypes.GeneralQueryInvalid, message, new Dictionary<string, string[]> { [field] = [detail] });
}
