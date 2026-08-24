namespace Praxy.Tables.Quotas;

/// <summary>
/// Instance-wide default quota dimensions, bound from <c>Praxy:Quotas:*</c> config in Program.cs —
/// same plain-record-of-defaults shape as <c>WebhookOptions</c>/<c>MessagingOptions</c>. An
/// organization's <see cref="OrganizationLimits"/> overrides these per-dimension when its
/// <c>limits</c> jsonb sets a value; every default here matches the hardcoded constants this engine
/// enforced before Phase 9, so an operator who never configures either stays on identical behavior.
/// </summary>
public sealed record QuotaOptions(
    int MaxProjects = 100,
    int MaxDatabasesPerProject = 20,
    int MaxTablesPerDatabase = 200,
    int MaxColumnsPerTable = 200,
    int MaxIndexesPerTable = 64,
    int MaxSitesPerProject = 20,
    // Sites Phase 2: bounds how many on-demand preview containers (any ready-but-not-active
    // deployment) a single project can have running at once, so a project accumulating many stale
    // `ready` deployments can't exhaust host Docker/memory capacity. Project-level, not per-site —
    // the resource being protected (the host's Docker daemon) is shared across a project's sites.
    int MaxPreviewContainersPerProject = 10);
