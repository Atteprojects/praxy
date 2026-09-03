using Microsoft.EntityFrameworkCore;
using Praxy.Core.Errors;
using Praxy.Persistence;

namespace Praxy.Tables.Quotas;

/// <summary>
/// Org-level quotas (roadmap Phase 9): every dimension Phase 2 already capped per-project
/// (databases/tables/columns/indexes) plus the one Phase 2 never capped (projects per org), now
/// resolved as <see cref="QuotaOptions"/> instance defaults overridable per-organization via
/// <see cref="Praxy.Persistence.Entities.Organization.Limits"/>. Every check is "count, then compare"
/// against the same catalog rows the DDL endpoints already write — no new counters to keep in sync,
/// and every trip is the same <see cref="ErrorTypes.GeneralResourceLimitExceeded"/> type the
/// pre-Phase-9 hardcoded caps already used (never reworded, per CLAUDE.md — these are the same limits,
/// just configurable now).
/// </summary>
public sealed class QuotaService(PraxyDb db, QuotaOptions defaults)
{
    public async Task<OrganizationLimits> GetOrgLimitsAsync(Guid organizationId, CancellationToken ct)
    {
        var raw = await db.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => o.Limits)
            .FirstOrDefaultAsync(ct);
        return OrganizationLimits.Parse(raw);
    }

    /// <summary>Null only for the reserved <c>console</c> project, which belongs to no organization and is never routed through this service anyway (the console-project guard keeps DDL endpoints out of its reach).</summary>
    private Task<Guid?> OrgIdForProjectAsync(string projectId, CancellationToken ct) =>
        db.Projects.Where(p => p.Id == projectId).Select(p => p.OrganizationId).FirstOrDefaultAsync(ct);

    public async Task EnsureProjectQuotaAsync(Guid organizationId, CancellationToken ct)
    {
        var limits = await GetOrgLimitsAsync(organizationId, ct);
        var max = limits.MaxProjects ?? defaults.MaxProjects;
        var used = await db.Projects.CountAsync(p => p.OrganizationId == organizationId, ct);
        if (used >= max)
            throw Exceeded("organization", "projects", max);
    }

    public async Task EnsureDatabaseQuotaAsync(string projectId, CancellationToken ct)
    {
        if (await OrgIdForProjectAsync(projectId, ct) is not { } orgId) return;
        var limits = await GetOrgLimitsAsync(orgId, ct);
        var max = limits.MaxDatabasesPerProject ?? defaults.MaxDatabasesPerProject;
        var used = await db.Databases.CountAsync(d => d.ProjectId == projectId, ct);
        if (used >= max)
            throw Exceeded("project", "databases", max);
    }

    public async Task EnsureTableQuotaAsync(string projectId, Guid databaseId, CancellationToken ct)
    {
        if (await OrgIdForProjectAsync(projectId, ct) is not { } orgId) return;
        var limits = await GetOrgLimitsAsync(orgId, ct);
        var max = limits.MaxTablesPerDatabase ?? defaults.MaxTablesPerDatabase;
        var used = await db.Tables.CountAsync(t => t.DatabaseId == databaseId, ct);
        if (used >= max)
            throw Exceeded("database", "tables", max);
    }

    public async Task EnsureColumnQuotaAsync(string projectId, Guid tableId, CancellationToken ct)
    {
        if (await OrgIdForProjectAsync(projectId, ct) is not { } orgId) return;
        var limits = await GetOrgLimitsAsync(orgId, ct);
        var max = limits.MaxColumnsPerTable ?? defaults.MaxColumnsPerTable;
        var used = await db.Columns.CountAsync(c => c.TableId == tableId, ct);
        if (used >= max)
            throw Exceeded("table", "columns", max);
    }

    public async Task EnsureIndexQuotaAsync(string projectId, Guid tableId, CancellationToken ct)
    {
        if (await OrgIdForProjectAsync(projectId, ct) is not { } orgId) return;
        var limits = await GetOrgLimitsAsync(orgId, ct);
        var max = limits.MaxIndexesPerTable ?? defaults.MaxIndexesPerTable;
        var used = await db.Indexes.CountAsync(i => i.TableId == tableId, ct);
        if (used >= max)
            throw Exceeded("table", "indexes", max);
    }

    public async Task EnsureSiteQuotaAsync(string projectId, CancellationToken ct)
    {
        if (await OrgIdForProjectAsync(projectId, ct) is not { } orgId) return;
        var limits = await GetOrgLimitsAsync(orgId, ct);
        var max = limits.MaxSitesPerProject ?? defaults.MaxSitesPerProject;
        var used = await db.Sites.CountAsync(s => s.ProjectId == projectId, ct);
        if (used >= max)
            throw Exceeded("project", "sites", max);
    }

    // ---- storage (Storage Phase 1) --------------------------------------------------------------

    public async Task EnsureBucketQuotaAsync(string projectId, CancellationToken ct)
    {
        if (await OrgIdForProjectAsync(projectId, ct) is not { } orgId) return;
        var limits = await GetOrgLimitsAsync(orgId, ct);
        var max = limits.MaxBucketsPerProject ?? defaults.MaxBucketsPerProject;
        var used = await db.Buckets.CountAsync(x => x.ProjectId == projectId, ct);
        if (used >= max)
            throw Exceeded("project", "buckets", max);
    }

    /// <summary>
    /// The two byte-valued storage dimensions, resolved together because an upload has to enforce
    /// both against the *same* org lookup: the per-file ceiling (also what Kestrel's request-body
    /// limit is raised to) and how much of the project's total storage budget is still free.
    /// Returned rather than thrown so the upload path can enforce them mid-stream — a streaming
    /// upload can pass a start-of-request check and still exceed either limit halfway through, and
    /// the correct answer there is a clean rejection plus rollback, not a truncated file.
    /// </summary>
    public async Task<StorageBudget> GetStorageBudgetAsync(string projectId, CancellationToken ct)
    {
        var orgId = await OrgIdForProjectAsync(projectId, ct);
        var limits = orgId is { } id ? await GetOrgLimitsAsync(id, ct) : OrganizationLimits.Unlimited;
        var maxFile = limits.MaxFileSizeBytes ?? defaults.MaxFileSizeBytes;
        var maxTotal = limits.MaxStorageBytesPerProject ?? defaults.MaxStorageBytesPerProject;
        var used = await UsedStorageBytesAsync(projectId, ct);
        return new StorageBudget(maxFile, maxTotal, used);
    }

    /// <summary>Sum of every stored file's size in the project. No denormalized counter to drift — the files themselves are the record.</summary>
    public async Task<long> UsedStorageBytesAsync(string projectId, CancellationToken ct) =>
        await db.Files
            .Where(f => db.Buckets.Where(x => x.ProjectId == projectId).Select(x => x.Id).Contains(f.BucketId))
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0L;

    /// <summary>
    /// Sites Phase 2: caps concurrent on-demand preview containers per project. Checked on the
    /// request path (a cold preview start), not against a durable counter table like every other
    /// dimension here — <paramref name="currentlyTrackedDeploymentIds"/> is <c>SiteContainerRegistry</c>'s
    /// own in-memory snapshot at call time, so this is a best-effort check: two concurrent cold
    /// starts for different deployments in the same project can both pass before either registers,
    /// the same small race every soft resource guard in this codebase (e.g. WarmPool eviction)
    /// accepts rather than serializing the whole request path over it.
    /// </summary>
    public async Task EnsurePreviewQuotaAsync(
        string projectId, IReadOnlyCollection<Guid> currentlyTrackedDeploymentIds, CancellationToken ct)
    {
        if (currentlyTrackedDeploymentIds.Count == 0) return;
        if (await OrgIdForProjectAsync(projectId, ct) is not { } orgId) return;
        var limits = await GetOrgLimitsAsync(orgId, ct);
        var max = limits.MaxPreviewContainersPerProject ?? defaults.MaxPreviewContainersPerProject;

        var activeIds = await db.Sites
            .Where(s => s.ProjectId == projectId && s.ActiveDeploymentId != null)
            .Select(s => s.ActiveDeploymentId!.Value)
            .ToListAsync(ct);
        var activeSet = activeIds.ToHashSet();

        var trackedInProject = await db.SiteDeployments
            .Where(d => d.ProjectId == projectId && currentlyTrackedDeploymentIds.Contains(d.Id))
            .Select(d => d.Id)
            .ToListAsync(ct);
        var previewCount = trackedInProject.Count(id => !activeSet.Contains(id));

        if (previewCount >= max)
            throw Exceeded("project", "concurrent preview containers", max);
    }

    /// <summary>
    /// The console's usage-vs-limit surfacing, entered by project id: there is no org-id entry
    /// point, so this reports the numbers that matter for the caller's own project.
    /// Per-table/per-database dimensions are reported as "the busiest one in this project" rather
    /// than a per-resource breakdown — enough to show a project is approaching a cap without a new
    /// drill-down screen.
    /// </summary>
    public async Task<QuotaSnapshot> GetSnapshotAsync(string projectId, CancellationToken ct)
    {
        var orgId = await OrgIdForProjectAsync(projectId, ct);
        var limits = orgId is { } id ? await GetOrgLimitsAsync(id, ct) : OrganizationLimits.Unlimited;

        var projectsUsed = orgId is { } oid ? await db.Projects.CountAsync(p => p.OrganizationId == oid, ct) : 0;

        var databaseIds = await db.Databases.Where(d => d.ProjectId == projectId).Select(d => d.Id).ToListAsync(ct);
        var busiestDatabaseTables = databaseIds.Count == 0 ? 0 : await db.Tables
            .Where(t => databaseIds.Contains(t.DatabaseId))
            .GroupBy(t => t.DatabaseId).Select(g => g.Count())
            .OrderDescending().FirstOrDefaultAsync(ct);

        var tableIds = await db.Tables.Where(t => databaseIds.Contains(t.DatabaseId)).Select(t => t.Id).ToListAsync(ct);
        var busiestTableColumns = tableIds.Count == 0 ? 0 : await db.Columns
            .Where(c => tableIds.Contains(c.TableId))
            .GroupBy(c => c.TableId).Select(g => g.Count())
            .OrderDescending().FirstOrDefaultAsync(ct);
        var busiestTableIndexes = tableIds.Count == 0 ? 0 : await db.Indexes
            .Where(i => tableIds.Contains(i.TableId))
            .GroupBy(i => i.TableId).Select(g => g.Count())
            .OrderDescending().FirstOrDefaultAsync(ct);

        var sitesUsed = await db.Sites.CountAsync(s => s.ProjectId == projectId, ct);
        var bucketsUsed = await db.Buckets.CountAsync(x => x.ProjectId == projectId, ct);
        var storageUsed = await UsedStorageBytesAsync(projectId, ct);

        return new QuotaSnapshot(
            projectsUsed, limits.MaxProjects ?? defaults.MaxProjects,
            databaseIds.Count, limits.MaxDatabasesPerProject ?? defaults.MaxDatabasesPerProject,
            busiestDatabaseTables, limits.MaxTablesPerDatabase ?? defaults.MaxTablesPerDatabase,
            busiestTableColumns, limits.MaxColumnsPerTable ?? defaults.MaxColumnsPerTable,
            busiestTableIndexes, limits.MaxIndexesPerTable ?? defaults.MaxIndexesPerTable,
            sitesUsed, limits.MaxSitesPerProject ?? defaults.MaxSitesPerProject,
            bucketsUsed, limits.MaxBucketsPerProject ?? defaults.MaxBucketsPerProject,
            storageUsed, limits.MaxStorageBytesPerProject ?? defaults.MaxStorageBytesPerProject);
    }

    private static PraxyException Exceeded(string scope, string dimension, int max) =>
        new(400, ErrorTypes.GeneralResourceLimitExceeded,
            $"This {scope} already has the maximum of {max} {dimension}.");
}

/// <summary>Used vs. max for every quota dimension, resolved for one project's organization.</summary>
public sealed record QuotaSnapshot(
    int ProjectsUsed, int ProjectsMax,
    int DatabasesUsed, int DatabasesMax,
    int BusiestDatabaseTables, int TablesPerDatabaseMax,
    int BusiestTableColumns, int ColumnsPerTableMax,
    int BusiestTableIndexes, int IndexesPerTableMax,
    int SitesUsed, int SitesMax,
    int BucketsUsed, int BucketsMax,
    long StorageBytesUsed, long StorageBytesMax);

/// <summary>
/// One project's resolved storage limits plus what it has already used. <see cref="Remaining"/> is
/// what an in-flight upload may still write before <c>MaxStorageBytesPerProject</c> is exceeded.
/// </summary>
public sealed record StorageBudget(long MaxFileSizeBytes, long MaxTotalBytes, long UsedBytes)
{
    public long Remaining => Math.Max(0, MaxTotalBytes - UsedBytes);
}
