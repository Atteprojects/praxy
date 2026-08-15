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

    /// <summary>
    /// Custom resource ids: 1–36 chars, lowercase alphanumeric plus hyphen, must start alphanumeric.
    /// </summary>
    public static bool IsValidCustomId(string id) => CustomIdRegex().IsMatch(id);

    public static bool IsReservedProjectId(string id) =>
        string.Equals(id, ConsoleProjectId, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,35}$")]
    private static partial Regex CustomIdRegex();
}
