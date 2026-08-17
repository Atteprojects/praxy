using System.Text.RegularExpressions;

namespace Praxy.Tables;

/// <summary>
/// The developer-facing "key" shared by databases, tables, columns and indexes — the slug an app
/// reads/writes (e.g. a column key like <c>title</c>). Keys are metadata-only and renameable; the
/// physical identifier they map to never changes. Distinct from <see cref="Praxy.Core.Ids"/>'s
/// wire-id format, which addresses these resources in URLs.
/// </summary>
public static partial class Keys
{
    public const int MaxLength = 64;

    /// <summary>
    /// Null-safe: a request body missing a "required" JSON string property binds it to <c>null</c>
    /// (System.Text.Json does not enforce C#'s non-nullable reference annotations at runtime), so
    /// this boundary must treat null as "invalid," not "crash" — found by Phase 9's security pass,
    /// where <c>key.Length</c> on a null key threw an unhandled 500 for an ordinary incomplete
    /// database/table/index create request.
    /// </summary>
    public static bool IsValid(string? key) => key is not null && key.Length is > 0 and <= MaxLength && KeyRegex().IsMatch(key);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex KeyRegex();
}
