using Praxy.Core;

namespace Praxy.Realtime;

/// <summary>
/// The realtime channel grammar (architecture.md §6, research/appwrite-api.md): mirrors the
/// resource tree, plus action-suffixed variants subscribable directly. Deliberately does not
/// validate subscribed channel strings — an unrecognized channel is simply inert (no event's
/// derived channel set will ever match it), which is deny-by-default for free rather than a
/// separate rule to keep in sync with <see cref="ChannelsForEvent"/>.
/// </summary>
public static class ChannelGrammar
{
    private static readonly string[] RowActions = ["create", "update", "delete"];

    /// <summary>Storage Phase 1: file events use the same three actions as rows.</summary>
    private static readonly string[] FileActions = RowActions;

    /// <summary>Server-side rewrite at subscribe time: the bare <c>account</c> channel becomes <c>account.&lt;userId&gt;</c>. Guests and keys have no id to rewrite to, so the channel is left as-is (and stays permanently inert).</summary>
    public static string[] NormalizeForSubscribe(IReadOnlyList<string> requested, Guid? userId)
    {
        var result = new string[requested.Count];
        for (var i = 0; i < requested.Count; i++)
            result[i] = requested[i] == "account" && userId is { } uid ? $"account.{Ids.Wire(uid)}" : requested[i];
        return result;
    }

    /// <summary>
    /// Every channel string an event of this <see cref="Events.PraxyEvent.Type"/> is delivered on.
    /// A row event fans out on four variants: the whole-table channel, the specific row, the
    /// action-suffixed table channel, and the action-suffixed row channel.
    /// </summary>
    public static IReadOnlyList<string> ChannelsForEvent(string type)
    {
        var parts = type.Split('.');

        if (parts.Length == 7 && parts[0] == "databases" && parts[2] == "tables" && parts[4] == "rows" &&
            IsHexId(parts[1]) && IsHexId(parts[3]) && IsHexId(parts[5]) && RowActions.Contains(parts[6]))
        {
            var basePrefix = $"databases.{parts[1]}.tables.{parts[3]}.rows";
            var rowId = parts[5];
            var action = parts[6];
            return [basePrefix, $"{basePrefix}.{rowId}", $"{basePrefix}.{action}", $"{basePrefix}.{rowId}.{action}"];
        }

        // Storage Phase 1: `buckets.<bucketId>.files.<fileId>.<action>` — the same four variants a
        // row event fans out on, one level shallower. A new prefix on the existing mechanism, not a
        // second grammar (docs/research/storage.md).
        if (parts.Length == 5 && parts[0] == "buckets" && parts[2] == "files" &&
            IsHexId(parts[1]) && IsHexId(parts[3]) && FileActions.Contains(parts[4]))
        {
            var basePrefix = $"buckets.{parts[1]}.files";
            var fileId = parts[3];
            var action = parts[4];
            return [basePrefix, $"{basePrefix}.{fileId}", $"{basePrefix}.{action}", $"{basePrefix}.{fileId}.{action}"];
        }

        if (parts.Length >= 2 && parts[0] == "users" && IsHexId(parts[1]))
            return [$"account.{parts[1]}"];

        if (parts.Length >= 2 && parts[0] == "teams" && IsHexId(parts[1]))
            return [$"teams.{parts[1]}"];

        return [];
    }

    /// <summary>
    /// The outgoing <c>events[]</c> field: the concrete type plus every combination of its id
    /// segments wildcarded to <c>*</c>, so an SDK matching against a pattern like
    /// <c>databases.*.tables.*.rows.*.create</c> can do plain string equality (research/appwrite-api.md).
    /// At most 3 id segments appear in any Praxy event type, so the powerset is at most 8 entries.
    /// </summary>
    public static IReadOnlyList<string> ExpandEventNames(string type)
    {
        var parts = type.Split('.');
        var idPositions = new List<int>();
        for (var i = 0; i < parts.Length; i++)
            if (IsHexId(parts[i]))
                idPositions.Add(i);

        var results = new HashSet<string> { type };
        var combos = 1 << idPositions.Count;
        for (var mask = 1; mask < combos; mask++)
        {
            var copy = (string[])parts.Clone();
            for (var bit = 0; bit < idPositions.Count; bit++)
                if ((mask & (1 << bit)) != 0)
                    copy[idPositions[bit]] = "*";
            results.Add(string.Join('.', copy));
        }
        return [.. results];
    }

    private static bool IsHexId(string s) => s.Length == 32 && s.All(Uri.IsHexDigit);
}
