using System.Text.Json;
using System.Text.Json.Nodes;

namespace Praxy.Auth;

/// <summary>
/// Per-project auth configuration, stored under the <c>auth</c> key of <c>projects.settings</c>
/// jsonb. Everything has a safe default so a project created in Phase 0 needs no backfill.
/// </summary>
public sealed record ProjectAuthSettings
{
    public const int MaxSessionLimit = 100;

    public bool EmailPassword { get; init; } = true;
    public bool GoogleEnabled { get; init; }
    public string? GoogleClientId { get; init; }
    public string? GoogleClientSecret { get; init; }

    /// <summary>Per-user session cap; creating one past the cap evicts the oldest.</summary>
    public int SessionLimit { get; init; } = 10;

    public int PasswordMinLength { get; init; } = 8;

    public static ProjectAuthSettings Parse(string settingsJson)
    {
        JsonObject? auth = null;
        try
        {
            auth = (JsonNode.Parse(settingsJson) as JsonObject)?["auth"] as JsonObject;
        }
        catch (JsonException)
        {
            // Unreadable settings behave like defaults rather than breaking every auth call.
        }
        if (auth is null)
            return new ProjectAuthSettings();

        return new ProjectAuthSettings
        {
            EmailPassword = GetBool(auth, "emailPassword") ?? true,
            GoogleEnabled = GetBool(auth, "googleEnabled") ?? false,
            GoogleClientId = GetString(auth, "googleClientId"),
            GoogleClientSecret = GetString(auth, "googleClientSecret"),
            SessionLimit = Math.Clamp(GetInt(auth, "sessionLimit") ?? 10, 1, MaxSessionLimit),
            PasswordMinLength = Math.Clamp(GetInt(auth, "passwordMinLength") ?? 8, 8, 128),
        };
    }

    /// <summary>Writes this back into a settings json blob, preserving unrelated keys.</summary>
    public string ApplyTo(string settingsJson)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(settingsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            root = [];
        }

        root["auth"] = new JsonObject
        {
            ["emailPassword"] = EmailPassword,
            ["googleEnabled"] = GoogleEnabled,
            ["googleClientId"] = GoogleClientId,
            ["googleClientSecret"] = GoogleClientSecret,
            ["sessionLimit"] = SessionLimit,
            ["passwordMinLength"] = PasswordMinLength,
        };
        return root.ToJsonString();
    }

    private static bool? GetBool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static int? GetInt(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static string? GetString(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
