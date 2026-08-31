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

    /// <summary>
    /// The connected GitHub repository, <c>"owner/repo"</c> (Sites Phase 4) — null until a git
    /// repository is connected via the console. Both this and <see cref="ProductionBranch"/> are set
    /// together (<c>SitesService.ConnectRepositoryAsync</c>) and cleared together
    /// (<c>DisconnectRepositoryAsync</c>); a site connects to at most one repository at a time.
    /// </summary>
    public string? RepositoryFullName { get; set; }

    /// <summary>A push to this branch builds and auto-activates; a push to any other branch of the connected repository builds a deployment that stays on its Phase 2 preview URL without touching production.</summary>
    public string? ProductionBranch { get; set; }

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

    /// <summary>upload | git — an "upload" deployment has a <see cref="SiteDeploymentSource"/> row; a "git" one instead has <see cref="CommitSha"/>/<see cref="CommitMessage"/>/<see cref="Branch"/> set and is cloned fresh by <c>SiteBuildWorker</c> at build time.</summary>
    public string Source { get; set; } = "upload";

    public string? CommitSha { get; set; }
    public string? CommitMessage { get; set; }
    public string? Branch { get; set; }

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

/// <summary>
/// One proxied request to a site's container — metadata only, per
/// docs/handoff/sites-request-logs-prompt.md's non-goals (no request/response bodies; that's a much
/// larger privacy/volume concern here than <see cref="FunctionExecution"/>'s bodies already
/// accept, since Sites traffic is arbitrary end-user web traffic, not an explicitly invoked call).
/// Written by <c>Praxy.Sites.SiteRequestLogWorker</c> off a bounded in-memory channel
/// <c>Praxy.Sites.SiteProxyMiddleware</c> feeds — never a synchronous insert on the request path, and
/// never a durability guarantee: under sustained overload a row can be silently dropped rather than
/// slow down or fail real site traffic. High-volume by design (every request to every deployed site,
/// unconditionally) — retention-eligible from day one, see <c>RetentionSweeper</c>, unlike
/// <see cref="FunctionExecution"/> which deferred that question.
/// </summary>
public class SiteRequestLog
{
    public required Guid Id { get; set; }
    public required Guid SiteId { get; set; }
    public required string ProjectId { get; set; }

    /// <summary>The deployment that served this request — production or preview, whichever the request actually resolved to. Null is not a real state today (every logged request went through <c>ForwardToContainerAsync</c> with a resolved deployment); nullable only so a future dispatch path that logs without one doesn't need a migration.</summary>
    public Guid? DeploymentId { get; set; }

    public required string Method { get; set; }
    public required string Path { get; set; }
    public int StatusCode { get; set; }
    public int DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
