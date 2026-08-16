using Praxy.Realtime;

namespace Praxy.Tests.Unit;

public class TicketStoreTests
{
    [Fact]
    public void A_minted_ticket_can_be_consumed_exactly_once()
    {
        var store = new TicketStore();
        var data = new RealtimeTicketData("proj1", Guid.NewGuid(), Guid.NewGuid(), null);
        var (ticket, expiresAt) = store.Mint(data);

        Assert.True(expiresAt > DateTimeOffset.UtcNow);
        Assert.True(store.TryConsume(ticket, out var consumed));
        Assert.Equal(data, consumed);

        Assert.False(store.TryConsume(ticket, out _));
    }

    [Fact]
    public void An_unknown_ticket_fails_to_consume()
    {
        var store = new TicketStore();
        Assert.False(store.TryConsume("does-not-exist", out _));
    }

    [Fact]
    public void Two_minted_tickets_are_distinct()
    {
        var store = new TicketStore();
        var data = new RealtimeTicketData("proj1", null, null, Guid.NewGuid());
        var (first, _) = store.Mint(data);
        var (second, _) = store.Mint(data);
        Assert.NotEqual(first, second);
    }
}
