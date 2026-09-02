using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Praxy.Tables;

/// <summary>
/// The security boundary described in architecture.md §4.1: every user-supplied key maps to a
/// generated physical identifier. Sanitization strips everything outside <c>[a-z0-9_]</c>, and a
/// hash suffix (derived from the entity's own id, so it's stable and collision-free) disambiguates
/// keys that sanitize to the same string. Every emitted identifier is additionally quoted and
/// regex-validated immediately before execution — belt and braces, because a single miss here is
/// arbitrary SQL execution.
/// </summary>
public static partial class PhysicalNaming
{
    private const int PostgresIdentifierLimit = 63;

    /// <summary>Physical schema name for a database: <c>px_&lt;hex32&gt;</c>.</summary>
    public static string SchemaName(Guid databaseId) => $"px_{Core.Ids.Wire(databaseId)}";

    /// <summary>
    /// Physical table/column name: <c>&lt;sanitized_key&gt;_&lt;hash6&gt;</c>. <paramref
    /// name="reserveSuffixChars"/> shrinks the budget further for a column type whose physical name
    /// gets an extra derived suffix appended at query time (a geo column's read-path
    /// <c>_lng</c>/<c>_lat</c> aliases) — without it, a max-length key could produce a physical name
    /// that leaves no room for that suffix, and the derived identifier would then fail this module's
    /// own length guard (<see cref="Quote"/>) the first time a row is read or written, not at column
    /// creation.
    /// </summary>
    public static string EntityName(string key, Guid id, int reserveSuffixChars = 0) =>
        WithHashSuffix(key, id, prefix: null, reserveSuffixChars);

    /// <summary>
    /// Physical index name: <c>ix_&lt;sanitized_key&gt;_&lt;hash6&gt;</c>. <paramref name="forFulltext"/>
    /// reserves the 5 characters <see cref="FulltextColumnName"/>'s <c>__fts</c> suffix will append to
    /// this name — the same reservation <see cref="EntityName"/> makes for a geo column's derived
    /// aliases, and for the same reason: keys are valid up to <see cref="Keys.MaxLength"/> characters,
    /// so without it a long-keyed fulltext index generates a 68-character tsvector column name that
    /// <see cref="Quote"/> refuses, turning an ordinary create request into a 500.
    /// </summary>
    public static string IndexName(string key, Guid id, bool forFulltext = false) =>
        WithHashSuffix(key, id, prefix: "ix_", reserveSuffixChars: forFulltext ? FulltextSuffix.Length : 0);

    private const string FulltextSuffix = "__fts";

    /// <summary>
    /// Physical generated tsvector column name for a fulltext index. The index's own physical name
    /// must have been generated with <c>IndexName(..., forFulltext: true)</c> so this suffix fits.
    /// </summary>
    public static string FulltextColumnName(string indexPhysicalName) => $"{indexPhysicalName}{FulltextSuffix}";

    /// <summary>
    /// Physical name of a table's row-permissions side table. The table's own physical name must have
    /// been generated reserving <see cref="RowSecuritySuffixChars"/> so this — and the index built on
    /// top of it — fit.
    /// </summary>
    public static string PermsTableName(string tablePhysicalName) => $"{tablePhysicalName}{PermsSuffix}";

    private const string PermsSuffix = "__perms";
    private const string PermsIndexSuffix = "_action_role_idx";

    /// <summary>
    /// What a table's physical name must reserve so row security can be enabled on it later. Two
    /// suffixes stack: <c>TablesService.SetRowSecurityAsync</c> derives a <c>__perms</c> side table
    /// from the table name and then an <c>_action_role_idx</c> index from *that*. Reserved for every
    /// table rather than only row-secured ones, because row security is toggled long after the name
    /// is generated and the name can never change.
    /// </summary>
    public static int RowSecuritySuffixChars => PermsSuffix.Length + PermsIndexSuffix.Length;

    private static string WithHashSuffix(string key, Guid id, string? prefix, int reserveSuffixChars = 0)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(id.ToByteArray()))[..6];
        var suffix = $"_{hash}";
        var budget = PostgresIdentifierLimit - (prefix?.Length ?? 0) - suffix.Length - reserveSuffixChars;

        var sanitized = Sanitize(key);
        if (sanitized.Length == 0)
            sanitized = "col";
        if (sanitized.Length > budget)
            sanitized = sanitized[..budget];

        return $"{prefix}{sanitized}{suffix}";
    }

    /// <summary>Lowercases and strips everything outside <c>[a-z0-9_]</c>.</summary>
    public static string Sanitize(string key) =>
        NonAlphanumericUnderscore().Replace(key.ToLowerInvariant(), "");

    /// <summary>
    /// The belt-and-braces check: every generated identifier must match this before it is ever
    /// interpolated into DDL, regardless of how it was derived.
    /// </summary>
    public static bool IsSafeIdentifier(string identifier) =>
        identifier.Length is > 0 and <= PostgresIdentifierLimit && SafeIdentifierRegex().IsMatch(identifier);

    /// <summary>
    /// Quotes an identifier for emission, after asserting it passes <see cref="IsSafeIdentifier"/>.
    /// Parameters can never bind identifiers — only values — so this plus the generated-name
    /// scheme is the actual control; quoting alone is not sufficient on its own.
    /// </summary>
    public static string Quote(string identifier)
    {
        if (!IsSafeIdentifier(identifier))
            throw new InvalidOperationException(
                $"Refusing to emit unsafe identifier '{identifier}' — this is a bug, not user input.");
        return new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    }

    /// <summary>Fully-qualified <c>"schema"."table"</c> — never relies on <c>search_path</c>.</summary>
    public static string QualifiedTable(string schemaName, string tablePhysicalName) =>
        $"{Quote(schemaName)}.{Quote(tablePhysicalName)}";

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex NonAlphanumericUnderscore();

    // No leading-letter requirement: every identifier this module emits is always quoted at use
    // (never relied on as a bare/unquoted token), so a leading digit is valid Postgres SQL.
    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex SafeIdentifierRegex();
}
