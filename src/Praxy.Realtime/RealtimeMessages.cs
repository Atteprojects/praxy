using System.Text;
using System.Text.Json.Nodes;

namespace Praxy.Realtime;

public sealed record SubscribeEntry(string SubscriptionId, string[] Channels, JsonNode? Queries);

/// <summary>Parses the client→server message-mode protocol (research/appwrite-api.md). Throws <see cref="FormatException"/> on anything malformed — callers close the socket with <see cref="RealtimeCloseCodes.BadMessage"/>.</summary>
public static class ClientMessage
{
    public static (string Type, JsonNode? Data) Parse(ReadOnlySpan<byte> utf8Json)
    {
        var node = JsonNode.Parse(utf8Json) as JsonObject ?? throw new FormatException("Message must be a JSON object.");
        var type = node["type"]?.GetValue<string>();
        if (string.IsNullOrEmpty(type))
            throw new FormatException("Message is missing a 'type'.");
        return (type, node["data"]);
    }

    public static IReadOnlyList<SubscribeEntry> ParseSubscribe(JsonNode? data)
    {
        var array = data as JsonArray ?? throw new FormatException("'subscribe' data must be an array.");
        return [.. array.Select(ParseEntry)];

        static SubscribeEntry ParseEntry(JsonNode? node)
        {
            var entry = node as JsonObject ?? throw new FormatException("Invalid subscribe entry.");
            var subscriptionId = entry["subscriptionId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(subscriptionId))
                throw new FormatException("Subscribe entry is missing 'subscriptionId'.");
            var channelsNode = entry["channels"] as JsonArray ?? throw new FormatException("Subscribe entry is missing 'channels'.");
            var channels = channelsNode.Select(c => c?.GetValue<string>()).Where(c => !string.IsNullOrEmpty(c)).ToArray();
            return new SubscribeEntry(subscriptionId, channels!, entry["queries"]);
        }
    }

    public static IReadOnlyList<string> ParseUnsubscribe(JsonNode? data)
    {
        var array = data as JsonArray ?? throw new FormatException("'unsubscribe' data must be an array.");
        return [.. array.Select(node =>
        {
            var subscriptionId = (node as JsonObject)?["subscriptionId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(subscriptionId))
                throw new FormatException("Unsubscribe entry is missing 'subscriptionId'.");
            return subscriptionId;
        })];
    }
}

/// <summary>Builds the server→client message-mode protocol as ready-to-enqueue UTF8 bytes.</summary>
public static class ServerMessage
{
    public static ReadOnlyMemory<byte> Connected(JsonNode? user) => Encode(new JsonObject
    {
        ["type"] = "connected",
        ["data"] = new JsonObject { ["user"] = user, ["channels"] = new JsonArray() },
    });

    public static ReadOnlyMemory<byte> Pong() => Encode(new JsonObject { ["type"] = "pong" });

    public static ReadOnlyMemory<byte> Error(string type, string message) => Encode(new JsonObject
    {
        ["type"] = "error",
        ["data"] = new JsonObject { ["type"] = type, ["message"] = message },
    });

    /// <summary>Acknowledges a processed subscribe/unsubscribe batch with the caller's own subscription ids.</summary>
    public static ReadOnlyMemory<byte> Response(string to, IEnumerable<string> subscriptionIds) => Encode(new JsonObject
    {
        ["type"] = "response",
        ["data"] = new JsonObject
        {
            ["to"] = to,
            ["subscriptions"] = new JsonArray([.. subscriptionIds.Select(s => (JsonNode?)s)]),
        },
    });

    public static ReadOnlyMemory<byte> Event(
        IReadOnlyList<string> events, IReadOnlyList<string> channels, IReadOnlyList<string> subscriptions,
        DateTimeOffset timestamp, JsonNode? payload) => Encode(new JsonObject
        {
            ["type"] = "event",
            ["data"] = new JsonObject
            {
                ["events"] = new JsonArray([.. events.Select(e => (JsonNode?)e)]),
                ["channels"] = new JsonArray([.. channels.Select(c => (JsonNode?)c)]),
                ["subscriptions"] = new JsonArray([.. subscriptions.Select(s => (JsonNode?)s)]),
                ["timestamp"] = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["payload"] = payload?.DeepClone(),
            },
        });

    private static ReadOnlyMemory<byte> Encode(JsonObject message) => Encoding.UTF8.GetBytes(message.ToJsonString());
}
