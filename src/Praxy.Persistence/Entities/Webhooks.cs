namespace Praxy.Persistence.Entities;

/// <summary>
/// A project-scoped HTTP delivery target on the shared event grammar (architecture.md §7).
/// Unlike session/API-key secrets, <see cref="Secret"/> is stored in plaintext, not hashed — a
/// one-way hash can't be used to compute a new HMAC on every delivery, so the raw value must stay
/// retrievable server-side. Reveal-once to the operator happens at the API response layer (same as
/// API keys), not at storage; see docs/research/dotnet-stack.md for the write-up of this decision.
/// </summary>
public class WebhookSubscription
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }

    /// <summary>
    /// Event grammar patterns this subscription matches, e.g. <c>databases.*.tables.*.rows.*.create</c>.
    /// Matched against a concrete event's <c>Praxy.Realtime.ChannelGrammar.ExpandEventNames(type)</c>
    /// output — the same wildcard expansion realtime already computes, not a second matcher.
    /// </summary>
    public string[] Events { get; set; } = [];

    public required string Secret { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Set when auto-disabled after too many consecutive failed deliveries; null otherwise (CLAUDE.md: loud, not a silent stop — the console renders this as a warning banner).</summary>
    public string? DisabledReason { get; set; }

    /// <summary>Consecutive deliveries that exhausted their own retries. Reset to 0 by the next successful delivery.</summary>
    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One logical delivery: a single outbox event matched against one subscription at dispatch time.
/// May be attempted several times (<see cref="WebhookDeliveryAttempt"/> holds the per-attempt log)
/// before reaching a terminal status. The console's "Redeliver" action clones a fresh row rather
/// than mutating this one, so past history is never overwritten.
/// </summary>
public class WebhookDelivery
{
    public required Guid Id { get; set; }
    public required Guid SubscriptionId { get; set; }
    public required string ProjectId { get; set; }
    public required Guid EventId { get; set; }
    public required string EventType { get; set; }

    /// <summary>Snapshot of the outbox event's payload at dispatch time — a redelivery must not depend on the source row still existing.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>queued | delivering | succeeded | failed</summary>
    public string Status { get; set; } = "queued";

    public int Attempts { get; set; }

    /// <summary>Due time for the next attempt; the delivery worker's claim query filters on this. Backoff pushes it forward on each failure.</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }

    /// <summary>Set when this row was created by a console "Redeliver" click; points at the delivery it re-sends.</summary>
    public Guid? RedeliveredFromId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Append-only per-attempt log entry: status/latency/response code/body, capped size.</summary>
public class WebhookDeliveryAttempt
{
    public required Guid Id { get; set; }
    public required Guid DeliveryId { get; set; }
    public required int AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public int DurationMs { get; set; }

    /// <summary>Null when the request never got a response (connect/timeout/SSRF-guard failure) — see <see cref="Error"/>.</summary>
    public int? StatusCode { get; set; }

    public string? ResponseBody { get; set; }
    public string? Error { get; set; }
}
