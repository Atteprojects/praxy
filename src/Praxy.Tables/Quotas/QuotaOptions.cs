namespace Praxy.Tables.Quotas;

/// <summary>
/// Instance-wide default quota dimensions, bound from <c>Praxy:Quotas:*</c> config in Program.cs —
/// same plain-record-of-defaults shape as <c>WebhookOptions</c>/<c>MessagingOptions</c>. An
/// organization's <see cref="OrganizationLimits"/> overrides these per-dimension when its
/// <c>limits</c> jsonb sets a value; every default here matches the hardcoded constants this engine
/// enforced before Phase 9, so an operator who never configures either stays on identical behavior.
/// </summary>
public sealed record QuotaOptions(
    // The one limit scoped to an *operator* rather than an organization, because organizations are
    // what every other limit is scoped to: without it, an operator who exhausts MaxProjects just
    // creates another organization and gets a fresh allowance, and every per-org quota below becomes
    // advisory. Generous enough that a consultancy running an org per client never notices;
    // configurable for anyone who needs more.
    int MaxOrganizationsPerOperator = 10,
    // Seats. Organizations-phase-2 made membership creatable (invite) and unbounded — the invite
    // endpoint is rate-limited (auth-email, 5 per 10 min), which bounds email amplification per
    // window but not the total: an org could accumulate members indefinitely. Every other creatable
    // resource here has a ceiling, and under managed hosting this is the dimension a plan is
    // actually sold by, so it is per-org overridable like the rest.
    int MaxMembersPerOrganization = 25,
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
    int MaxPreviewContainersPerProject = 10,
    // Storage Phase 1. MaxFileSizeBytes is the value Kestrel's request-body limit is derived from
    // in Program.cs — the two must never be configured independently or an upload fails at a size
    // nobody set. MaxStorageBytesPerProject is the one that bounds backup growth: file bytes live
    // in the praxy schema, so deploy/backup.sh dumps every one of them (docs/self-host.md).
    int MaxBucketsPerProject = 20,
    long MaxFileSizeBytes = 52_428_800,
    long MaxStorageBytesPerProject = 5_368_709_120);
