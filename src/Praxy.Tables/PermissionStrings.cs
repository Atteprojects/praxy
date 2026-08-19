using System.Text.RegularExpressions;
using Praxy.Core;

namespace Praxy.Tables;

/// <summary>
/// Appwrite's permission string grammar, adopted verbatim per appwrite-api.md:
/// <c>&lt;action&gt;("&lt;role&gt;")</c>. <c>write</c> is an aggregate — expanded to
/// create+update+delete at write time, never stored, never returned.
/// </summary>
public static partial class PermissionStrings
{
    public const string Read = "read";
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Write = "write";

    /// <summary>The only actions ever persisted — <see cref="Write"/> is expanded before storage.</summary>
    public static readonly IReadOnlyList<string> StorableActions = [Read, Create, Update, Delete];

    /// <summary>What <see cref="Write"/> expands to — create+update+delete, deliberately not read.</summary>
    private static readonly IReadOnlyList<string> WriteExpansion = [Create, Update, Delete];

    private static readonly IReadOnlyList<string> AllActions = [Read, Create, Update, Delete, Write];

    public static string Format(string action, string role) => $"{action}(\"{role}\")";

    /// <summary>
    /// Parses one <c>action("role")</c> string. Throws <see cref="FormatException"/> on a malformed
    /// entry — including null (a permissions array can carry a JSON <c>null</c> element; found by
    /// Phase 9's security pass throwing <see cref="ArgumentNullException"/>, an unhandled 500,
    /// instead of the same clean 400 every other malformed entry already produced here).
    /// </summary>
    public static (string Action, string Role) Parse(string? permission)
    {
        if (permission is null)
            throw new FormatException("A permission entry must be a string, not null.");
        var match = PermissionRegex().Match(permission);
        if (!match.Success)
            throw new FormatException($"'{permission}' is not a valid permission string.");
        var action = match.Groups["action"].Value;
        var role = match.Groups["role"].Value;
        if (!AllActions.Contains(action))
            throw new FormatException($"Unknown permission action '{action}'.");
        if (!IsValidRole(role))
            throw new FormatException($"Invalid role '{role}'.");
        return (action, role);
    }

    /// <summary>
    /// Parses a wire-format permission list, expanding any <c>write(...)</c> entries into their
    /// three storable actions. Deduplicates (action, role) pairs.
    /// </summary>
    public static IReadOnlyList<(string Action, string Role)> ParseAndExpand(IEnumerable<string> permissions)
    {
        var expanded = new List<(string Action, string Role)>();
        foreach (var permission in permissions)
        {
            var (action, role) = Parse(permission);
            if (action == Write)
                expanded.AddRange(WriteExpansion.Select(a => (a, role)));
            else
                expanded.Add((action, role));
        }
        return expanded.Distinct().ToArray();
    }

    /// <summary>
    /// Validates a role string's shape against architecture.md §4.3's grammar. Delegates to
    /// <see cref="Roles.IsValid"/> — function <c>execute</c> lists validate the same strings, and
    /// two copies of this grammar would drift.
    /// </summary>
    public static bool IsValidRole(string role) => Roles.IsValid(role);

    [GeneratedRegex("""^(?<action>read|create|update|delete|write)\("(?<role>[^"]+)"\)$""")]
    private static partial Regex PermissionRegex();
}
