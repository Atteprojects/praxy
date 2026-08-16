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

    public static bool IsValid(string key) => key.Length is > 0 and <= MaxLength && KeyRegex().IsMatch(key);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex KeyRegex();
}
