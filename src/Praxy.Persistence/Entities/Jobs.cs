namespace Praxy.Persistence.Entities;

/// <summary>
/// Async DDL work (index builds, type changes). Created in Phase 0 so the shape is settled;
/// the runner arrives in Phase 2. No FK — the databases table itself lands in Phase 2.
/// </summary>
public class SchemaJob
{
    public required Guid Id { get; set; }
    public required Guid DatabaseId { get; set; }

    /// <summary>Nullable: only jobs scoped to one table set it (every Phase 2 job kind does).</summary>
    public Guid? TableId { get; set; }

    /// <summary>create_index | change_type | ...</summary>
    public required string Kind { get; set; }

    public string Payload { get; set; } = "{}";

    /// <summary>queued | processing | available | failed | cancelled</summary>
    public string Status { get; set; } = "queued";

    public int Attempts { get; set; }
    public string? Error { get; set; }

    /// <summary>Backend pid of the connection running the DDL, captured for <c>pg_cancel_backend</c>.</summary>
    public int? Pid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Bumped on every state transition; "elapsed" while processing is measured from here.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
