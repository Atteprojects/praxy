using System.Text.RegularExpressions;

namespace Praxy.Core;

public static partial class Ids
{
    /// <summary>The reserved console project id. Data-plane endpoints and API keys refuse it.</summary>
    public const string ConsoleProjectId = "console";

    /// <summary>Time-ordered UUIDv7 — Postgres' bytewise uuid comparison matches its big-endian layout.</summary>
    public static Guid NewUuid() => Guid.CreateVersion7();

    /// <summary>A generated, URL-safe, time-ordered id for wire-visible resources (projects).</summary>
    public static string NewResourceId() => Guid.CreateVersion7().ToString("n");

    /// <summary>Wire form of uuid-keyed entities (app users, sessions, teams …): 32 lowercase hex chars.</summary>
    public static string Wire(Guid id) => id.ToString("n");

    /// <summary>Parses the wire form back. Accepts dashed uuids too so hand-typed curl calls survive.</summary>
    public static bool TryParseWire(string? value, out Guid id)
    {
        id = default;
        return value is not null &&
               (Guid.TryParseExact(value, "n", out id) || Guid.TryParseExact(value, "d", out id));
    }

    /// <summary>
    /// Custom resource ids: 1–36 chars, lowercase alphanumeric plus hyphen, must start alphanumeric.
    /// Null-safe like <see cref="TryParseWire"/> — a request body missing a "required" JSON string
    /// property binds it to <c>null</c> (System.Text.Json does not enforce C#'s non-nullable
    /// reference annotations at runtime), so every validator on this boundary must treat null as
    /// "invalid," not "crash" (found by Phase 9's security pass: <c>Regex.IsMatch(null)</c> threw
    /// <see cref="ArgumentNullException"/>, an unhandled 500, for something as ordinary as an
    /// incomplete request body).
    /// </summary>
    public static bool IsValidCustomId(string? id) => id is not null && CustomIdRegex().IsMatch(id);

    public static bool IsReservedProjectId(string id) =>
        string.Equals(id, ConsoleProjectId, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,35}$")]
    private static partial Regex CustomIdRegex();
}
