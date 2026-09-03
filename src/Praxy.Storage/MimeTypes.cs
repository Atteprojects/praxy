using System.Text.RegularExpressions;

namespace Praxy.Storage;

/// <summary>
/// The bucket <c>allowed_mime_types</c> allow-list: exact types plus <c>type/*</c> and <c>*/*</c>
/// wildcards, matched case-insensitively. A null or empty list means "any type" — the two are
/// normalized to the same thing (null) at write time so there is one representation of "no
/// restriction" rather than two that behave alike but read differently.
/// </summary>
public static partial class MimeTypes
{
    /// <summary>What an upload with no usable <c>Content-Type</c> is recorded as.</summary>
    public const string Fallback = "application/octet-stream";

    /// <summary>
    /// Normalizes a request's <c>Content-Type</c>: parameters (<c>; charset=utf-8</c>) dropped,
    /// lower-cased, trimmed. Anything that isn't a well-formed <c>type/subtype</c> becomes
    /// <see cref="Fallback"/> rather than being rejected — a browser that sends nothing useful is
    /// not a client error.
    /// </summary>
    public static string Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return Fallback;
        var value = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return TypeRegex().IsMatch(value) ? value : Fallback;
    }

    /// <summary>True when <paramref name="allowed"/> is null/empty (any type) or matches <paramref name="mimeType"/>.</summary>
    public static bool IsAllowed(IReadOnlyList<string>? allowed, string mimeType)
    {
        if (allowed is null || allowed.Count == 0)
            return true;
        foreach (var pattern in allowed)
        {
            if (Matches(pattern, mimeType))
                return true;
        }
        return false;
    }

    private static bool Matches(string pattern, string mimeType)
    {
        var p = pattern.Trim();
        if (p == "*" || p == "*/*")
            return true;
        if (p.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = p[..^1]; // keep the slash: "image/" — so "image/*" never matches "imagex/png"
            return mimeType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(p, mimeType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates one allow-list entry as it is stored: an exact <c>type/subtype</c>, a
    /// <c>type/*</c> wildcard, or <c>*/*</c>.
    /// </summary>
    public static bool IsValidPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var p = pattern.Trim();
        return p is "*" or "*/*" || PatternRegex().IsMatch(p);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9!#$&^_.+-]*\/[a-z0-9][a-z0-9!#$&^_.+-]*$")]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*\/(\*|[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*)$")]
    private static partial Regex PatternRegex();
}
