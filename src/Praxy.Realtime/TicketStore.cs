using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Praxy.Realtime;

/// <summary>Identity to reconstruct at redemption time — never the raw session/key secret itself.</summary>
public sealed record RealtimeTicketData(string ProjectId, Guid? UserId, Guid? SessionId, Guid? ApiKeyId);

/// <summary>
/// Single-use, 60-second realtime connection tickets for non-browser clients (architecture.md §6):
/// avoids putting a long-lived session secret in a URL, where it lands in proxy logs. In-memory
/// only — losing outstanding tickets on restart just means the client mints a new one.
/// </summary>
public sealed class TicketStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (RealtimeTicketData Data, DateTimeOffset ExpiresAt)> _tickets = new();

    public (string Ticket, DateTimeOffset ExpiresAt) Mint(RealtimeTicketData data)
    {
        Sweep();
        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var expiresAt = DateTimeOffset.UtcNow + Lifetime;
        _tickets[ticket] = (data, expiresAt);
        return (ticket, expiresAt);
    }

    /// <summary>Consumes the ticket if present and unexpired — always removed, so a second redemption attempt always fails.</summary>
    public bool TryConsume(string ticket, out RealtimeTicketData data)
    {
        data = null!;
        if (!_tickets.TryRemove(ticket, out var entry))
            return false;
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;
        data = entry.Data;
        return true;
    }

    private void Sweep()
    {
        if (_tickets.Count < 1000)
            return;
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in _tickets)
            if (value.ExpiresAt <= now)
                _tickets.TryRemove(key, out _);
    }
}
