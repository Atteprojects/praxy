using System.Net.WebSockets;
using System.Threading.Channels;

namespace Praxy.Realtime;

/// <summary>
/// One realtime WebSocket connection: resolved roles, active subscriptions, and a bounded
/// outbound queue with a single writer task (research/dotnet-stack.md's WebSocket shape).
/// <see cref="Subscriptions"/>, <see cref="Roles"/> and the index-bookkeeping sets are mutated
/// only by the connection's own read loop in <c>Praxy.Api</c> — <see cref="ConnectionRegistry"/>
/// reaches in only through its own thread-safe index, and <see cref="RealtimeHub"/> only ever
/// flips <see cref="NeedsRevalidation"/> or calls <see cref="RequestClose"/>. That single-writer
/// discipline is what lets these be plain (non-concurrent) collections.
/// </summary>
public sealed class Connection
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string ProjectId { get; init; }
    public Guid? UserId { get; init; }
    public Guid? SessionId { get; init; }

    /// <summary>Operator console connections and bypass-flagged API keys: fan-out matches on subscribed channel alone, skipping role/permission intersection entirely — the realtime analogue of <c>ApiKey.BypassRowPermissions</c>.</summary>
    public bool Bypass { get; init; }

    /// <summary>
    /// security-review-phase-3: for a bypass connection backed by an API key, the resource words
    /// (<c>"databases"</c>, <c>"buckets"</c>, <c>"users"</c>, <c>"teams"</c>) that key's own scopes
    /// actually cover — <see cref="ConnectionRegistry.Reindex"/> refuses to index a bypass
    /// subscription (exact channel or <c>"&lt;resource&gt;.*"</c> firehose) outside this set. Null for
    /// an operator console connection (Bypass is still true, but an operator's own project access has
    /// no scope concept to narrow it by — unrestricted, same as before this check existed) and for
    /// every non-bypass connection (irrelevant there; matching is by role intersection instead).
    /// Without this, a key minted with only <c>databases.read</c> plus the independently-settable
    /// <c>BypassRowPermissions</c> flag — an ordinary "trusted server key for the database" grant —
    /// could firehose-subscribe to <c>users.*</c>/<c>teams.*</c>/<c>buckets.*</c> and receive every
    /// account, membership, and file event in the project despite never holding
    /// <c>users.read</c>/<c>teams.read</c>/<c>storage.read</c>.
    /// </summary>
    public HashSet<string>? AllowedBypassResources { get; init; }

    private volatile string[] _roles = [];
    public string[] Roles { get => _roles; set => _roles = value; }

    private volatile bool _needsRevalidation;
    public bool NeedsRevalidation { get => _needsRevalidation; set => _needsRevalidation = value; }

    /// <summary>subscriptionId -> requested channels. Owner-thread-only.</summary>
    public Dictionary<string, string[]> Subscriptions { get; } = [];

    /// <summary>What <see cref="ConnectionRegistry"/> currently has indexed for this connection, so it can diff rather than rescan on every change. Owner-thread-only.</summary>
    internal HashSet<(string Role, string Channel, string SubscriptionId)> IndexEntries { get; } = [];
    internal HashSet<(string Channel, string SubscriptionId)> BypassIndexEntries { get; } = [];

    /// <summary>Bypass-only: subscriptions to a <c>"&lt;resource&gt;.*"</c> firehose channel (e.g. <c>databases.*</c> for the console's realtime inspector, which cannot know every table's channel string up front). Owner-thread-only.</summary>
    internal HashSet<(string Prefix, string SubscriptionId)> BypassPrefixEntries { get; } = [];

    public Channel<ReadOnlyMemory<byte>> Outbound { get; } = System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });

    public CancellationTokenSource CloseCts { get; } = new();
    public WebSocketCloseStatus? PendingCloseStatus { get; private set; }
    public string? PendingCloseReason { get; private set; }
    private int _closeRequested;

    /// <summary>
    /// Enqueues a message for the writer loop. On a full buffer — a slow consumer — the
    /// connection is closed rather than left to buffer unboundedly (architecture.md §6); the
    /// client is expected to reconnect and resubscribe.
    /// </summary>
    public void Enqueue(ReadOnlyMemory<byte> message)
    {
        if (!Outbound.Writer.TryWrite(message))
            RequestClose(RealtimeCloseCodes.Overloaded, "Slow consumer — outbound buffer full.");
    }

    /// <summary>Idempotent: the first caller's status/reason wins. Safe to call from any thread.</summary>
    public void RequestClose(WebSocketCloseStatus status, string reason)
    {
        if (Interlocked.CompareExchange(ref _closeRequested, 1, 0) == 0)
        {
            PendingCloseStatus = status;
            PendingCloseReason = reason;
        }
        try { CloseCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
