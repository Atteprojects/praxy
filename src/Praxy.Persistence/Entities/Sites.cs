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
/// A custom hostname pointed at a site's <em>active</em> deployment (Sites Phase 3) — no preview-URL
/// equivalent, unlike the built-in <c>*.sites.{Domain}</c> pattern. <see cref="Hostname"/> is globally
/// unique (not just per-project): two different sites can't claim the same real-world domain. Starts
/// "pending" and flips to "verified" the moment the first request through it is successfully proxied
/// (see <see cref="Praxy.Sites.SiteProxyMiddleware"/>'s remarks) — that's as strong a proof of DNS
/// control as a dedicated verification record would be, since a proxied request only ever arrives once
/// Caddy's on-demand TLS has already completed a real ACME HTTP-01 challenge against this exact
/// hostname. "Verified" never reverts on its own — a domain that stops resolving just goes quiet, it
/// isn't detected or pruned (a monitoring concern, not this feature's).
/// </summary>
public class SiteDomain
{
    public required Guid Id { get; set; }
    public required Guid SiteId { get; set; }
    public required string ProjectId { get; set; }
    public required string Hostname { get; set; }

    /// <summary>pending | verified</summary>
    public string Status { get; set; } = "pending";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAt { get; set; }
}
