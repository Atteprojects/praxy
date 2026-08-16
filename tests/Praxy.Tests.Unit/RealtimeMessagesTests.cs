using System.Text;
using System.Text.Json.Nodes;
using Praxy.Realtime;

namespace Praxy.Tests.Unit;

public class RealtimeMessagesTests
{
    private static ReadOnlySpan<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Parses_a_ping_message()
    {
        var (type, data) = ClientMessage.Parse(Bytes("""{"type":"ping"}"""));
        Assert.Equal("ping", type);
        Assert.Null(data);
    }

    [Fact]
    public void Parses_a_batched_subscribe_message()
    {
        var (type, data) = ClientMessage.Parse(Bytes(
            """{"type":"subscribe","data":[{"subscriptionId":"s1","channels":["databases.db.tables.t.rows"]},{"subscriptionId":"s2","channels":["account"]}]}"""));
        Assert.Equal("subscribe", type);
        var entries = ClientMessage.ParseSubscribe(data);
        Assert.Equal(2, entries.Count);
        Assert.Equal("s1", entries[0].SubscriptionId);
        Assert.Equal(["databases.db.tables.t.rows"], entries[0].Channels);
        Assert.Equal("s2", entries[1].SubscriptionId);
    }

    [Fact]
    public void Parses_an_unsubscribe_message()
    {
        var (_, data) = ClientMessage.Parse(Bytes("""{"type":"unsubscribe","data":[{"subscriptionId":"s1"}]}"""));
        Assert.Equal(["s1"], ClientMessage.ParseUnsubscribe(data));
    }

    [Fact]
    public void A_non_object_message_is_rejected()
    {
        Assert.Throws<FormatException>(() => ClientMessage.Parse(Bytes("""["not","an","object"]""")));
    }

    [Fact]
    public void A_missing_type_is_rejected()
    {
        Assert.Throws<FormatException>(() => ClientMessage.Parse(Bytes("""{"data":[]}""")));
    }

    [Fact]
    public void A_subscribe_entry_missing_subscriptionId_is_rejected()
    {
        var (_, data) = ClientMessage.Parse(Bytes("""{"type":"subscribe","data":[{"channels":["account"]}]}"""));
        Assert.Throws<FormatException>(() => ClientMessage.ParseSubscribe(data));
    }

    [Fact]
    public void Connected_message_carries_the_user_node_or_null()
    {
        var withUser = Encoding.UTF8.GetString(ServerMessage.Connected(new JsonObject { ["$id"] = "u1" }).Span);
        Assert.Contains("\"$id\":\"u1\"", withUser);

        var guest = Encoding.UTF8.GetString(ServerMessage.Connected(null).Span);
        Assert.Contains("\"user\":null", guest);
    }

    [Fact]
    public void Event_message_carries_events_channels_subscriptions_and_a_cloned_payload()
    {
        var payload = new JsonObject { ["rowId"] = "row1" };

        // Building two messages from the same JsonNode payload must not throw
        // "the node already has a parent" — Event() must deep-clone it.
        var first = ServerMessage.Event(["a.create"], ["a"], ["s1"], DateTimeOffset.UtcNow, payload);
        var second = ServerMessage.Event(["a.create"], ["a"], ["s1", "s2"], DateTimeOffset.UtcNow, payload);

        var json = Encoding.UTF8.GetString(second.Span);
        Assert.Contains("\"row1\"", json);
        Assert.Contains("\"s1\"", json);
        Assert.Contains("\"s2\"", json);
        Assert.NotEmpty(first.ToArray());
    }

    [Fact]
    public void Response_message_echoes_the_processed_subscription_ids()
    {
        var json = Encoding.UTF8.GetString(ServerMessage.Response("subscribe", ["s1", "s2"]).Span);
        Assert.Contains("\"to\":\"subscribe\"", json);
        Assert.Contains("\"s1\"", json);
        Assert.Contains("\"s2\"", json);
    }
}
