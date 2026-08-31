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
    int AuditLogMaxAgeDays = 90,
    // Deliberately much shorter than the 90-day default above: praxy.site_requests logs every
    // request to every deployed site, unconditionally — see docs/handoff/sites-request-logs-prompt.md's
    // landmine on why this table can't repeat function_executions' "defer retention" call. A week is
    // enough to debug a recent problem without letting this become the largest table in the database
    // on a busy instance.
    int SiteRequestsMaxAgeDays = 7);
