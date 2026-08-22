namespace Praxy.Persistence.Entities;

/// <summary>
/// A hosted Next.js site's identity and settings — stable across deployments, points at whichever
/// <see cref="SiteDeployment"/> is currently publicly reachable. No <c>framework</c> column: only
/// Next.js exists in Phase 1, so there is nothing to select between yet.
/// </summary>
public class Site
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }

    /// <summary>Relative subdirectory within the uploaded tar treated as the Next.js app root, or "" for the tar's own root (monorepo support, best-effort — see docs/handoff/sites-phase-1-report.md).</summary>
    public string RootDirectory { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>The deployment currently serving public traffic. Null until a build first succeeds.</summary>
    public Guid? ActiveDeploymentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One environment variable, injected at both build time (as a Docker <c>--build-arg</c>, so
/// Next.js can inline <c>NEXT_PUBLIC_*</c> values) and container runtime. Encrypted at rest with
/// the instance-wide <see cref="Praxy.Auth.InstanceKey"/>, same mechanism as
/// <c>FunctionEnvVar.ProtectedValue</c>. Write-only from the console's perspective.
/// </summary>
public class SiteEnvVar
{
    public required Guid Id { get; set; }
    public required Guid SiteId { get; set; }
    public required string Key { get; set; }
    public required string ProtectedValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One build: tar upload → image build → a long-lived container started on activation. Unlike a
/// <c>FunctionDeployment</c>, a <em>ready</em> site deployment that is also the site's active one has
/// an actually-running container — <see cref="ContainerId"/> is set once that container starts, not
/// once the image finishes building. <see cref="BuildLog"/> is the same queryable, appendable,
/// console-tailed row Functions already uses.
/// </summary>
public class SiteDeployment
{
    public required Guid Id { get; set; }
    public required Guid SiteId { get; set; }
    public required string ProjectId { get; set; }
    public long SourceSizeBytes { get; set; }

    /// <summary>queued | building | ready | failed</summary>
    public string Status { get; set; } = "queued";

    public string BuildLog { get; set; } = "";
    public string? Error { get; set; }

    /// <summary>The built Docker image tag, set once the build succeeds.</summary>
    public string? ImageTag { get; set; }

    /// <summary>
    /// The running container's id while this deployment is active — cleared when it's superseded or
    /// stopped. The container's current host/port on the Docker network is ephemeral runtime state,
    /// not persisted here; <c>SiteContainerRegistry</c> holds it in memory (same precedent as
    /// Functions' <c>WarmPool</c>), rebuilt by <c>SiteReconciler</c> on every <c>api</c> startup.
    /// </summary>
    public string? ContainerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>
    /// Set once <see cref="SiteScreenshotWorker"/> has captured (or given up capturing —
    /// see <see cref="ScreenshotAttempts"/>) this deployment's live preview. Null forever means
    /// "not attempted yet" or "still retrying"; the console renders a placeholder either way, so the
    /// two are not distinguished any further than this.
    /// </summary>
    public DateTimeOffset? ScreenshotCapturedAt { get; set; }

    /// <summary>Bounds <see cref="SiteScreenshotWorker"/>'s retry loop — see <c>SitesOptions.ScreenshotMaxAttempts</c>.</summary>
    public int ScreenshotAttempts { get; set; }
}

/// <summary>
/// The uploaded build context tar, split out of <see cref="SiteDeployment"/> for the same reason as
/// <c>FunctionDeploymentSource</c>: an EF list query for deployments must never drag a multi-megabyte
/// blob along. Deleted once the build finishes, success or failure.
/// </summary>
public class SiteDeploymentSource
{
    public required Guid DeploymentId { get; set; }
    public required byte[] Tar { get; set; }
}

/// <summary>
/// A deployment's captured preview screenshot (PNG), split out of <see cref="SiteDeployment"/> for
/// the exact same reason as <see cref="SiteDeploymentSource"/> — the sites list/deployments list
/// queries must never drag image bytes along just to render a status table. One row per deployment
/// that has ever been successfully captured; never updated in place, since a deployment's content
/// never changes after activation (a redeploy creates a new deployment, and thus a new row here).
/// </summary>
public class SiteDeploymentScreenshot
{
    public required Guid DeploymentId { get; set; }
    public required byte[] Png { get; set; }
}
