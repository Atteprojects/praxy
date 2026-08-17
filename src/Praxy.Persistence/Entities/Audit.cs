namespace Praxy.Persistence.Entities;

public class AuditLogEntry
{
    public required Guid Id { get; set; }
    public string? ProjectId { get; set; }

    /// <summary>
    /// Who acted: <c>admin:&lt;id&gt;</c> for a console operator (every entry today — every write to
    /// this table is a console-authenticated action, from schema DDL to the console's own row editor),
    /// reserved <c>user:&lt;id&gt;</c> for a future app-user-initiated entry, or <c>system</c> for the
    /// instance itself. Deliberately not <c>user:&lt;id&gt;</c> for admins (Phase 9 fix, roadmap: "admin
    /// actions distinguished from user actions") — that format already means "one app user" in the
    /// permission-role grammar (architecture.md §4.3), so tagging an operator that way read as an app
    /// user's own action to anyone parsing the log, even though app-user actions aren't logged here at
    /// all yet.
    /// </summary>
    public required string Actor { get; set; }

    public required string Action { get; set; }
    public required string Resource { get; set; }
    public string? Ip { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
