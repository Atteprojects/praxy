namespace Praxy.Api.Infrastructure;

/// <summary>
/// Every knob configurable, per CLAUDE.md's cross-phase rule. Bound from <c>Praxy:Retention:*</c>
/// config in Program.cs, same plain-record-of-defaults shape as <c>WebhookOptions</c>/
/// <c>QuotaOptions</c>. 90-day defaults are a deliberate choice, not a number handed down: short
/// enough that these tables don't grow forever, generous enough that the audit-log read surface
/// (post-v0.1.0 gap #3) stays useful rather than being emptied out from under it.
/// </summary>
public sealed record RetentionOptions(
    int SweepIntervalSeconds = 3600,
    int EventsMaxAgeDays = 90,
    int WebhookDeliveriesMaxAgeDays = 90,
    int AuditLogMaxAgeDays = 90);
