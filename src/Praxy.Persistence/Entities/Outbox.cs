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
    public DateTimeOffset? DispatchedAt { get; set; }
}
