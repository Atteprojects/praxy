namespace Praxy.Persistence.Entities;

public class AuditLogEntry
{
    public required Guid Id { get; set; }
    public string? ProjectId { get; set; }

    /// <summary>Who acted: <c>user:&lt;id&gt;</c>, <c>key:&lt;id&gt;</c>, or <c>system</c>.</summary>
    public required string Actor { get; set; }

    public required string Action { get; set; }
    public required string Resource { get; set; }
    public string? Ip { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
