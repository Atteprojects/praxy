namespace Praxy.Persistence.Entities;

/// <summary>
/// The outbox. Written inside the same transaction as data changes from Phase 3 onward;
/// consumed by webhooks in Phase 6. Created now so retrofitting never touches write paths twice.
/// </summary>
public class OutboxEvent
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }

    /// <summary>Event grammar: <c>databases.&lt;db&gt;.tables.&lt;t&gt;.rows.&lt;r&gt;.create</c> ...</summary>
    public required string Type { get; set; }

    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Set by the Phase 6 webhook outbox dispatcher once it has expanded this event into every
    /// matching subscription's <see cref="WebhookDelivery"/> row. Null means "not yet claimed" —
    /// the dispatcher's <c>FOR UPDATE SKIP LOCKED</c> claim query filters on this being null, the
    /// same shape <c>SchemaJobRunner</c> uses for <c>schema_jobs.status = 'queued'</c>.
    /// </summary>
    /// <remarks>
    /// Originally named <c>DispatchedAt</c> (singular, implicitly "the one consumer"). Phase 7 adds a
    /// second independent consumer (<see cref="FunctionsDispatchedAt"/>) — sharing one claim column
    /// between two consumers would mean whichever dispatcher claims a row first silently hides it
    /// from the other, so each consumer now gets its own nullable timestamp rather than a shared one.
    /// </remarks>
    public DateTimeOffset? WebhooksDispatchedAt { get; set; }

    /// <summary>
    /// Same shape as <see cref="WebhooksDispatchedAt"/>, claimed independently by
    /// <c>Praxy.Functions.FunctionEventDispatcher</c> for event-triggered function executions.
    /// </summary>
    public DateTimeOffset? FunctionsDispatchedAt { get; set; }
}
