namespace Praxy.Persistence.Entities;

/// <summary>
/// A deployed function's identity and settings — roughly the "database" of Functions: stable across
/// deployments, points at whichever <see cref="FunctionDeployment"/> is currently serving invocations.
/// </summary>
public class FunctionDef
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }

    /// <summary>dart | node — <see cref="Praxy.Functions.FunctionRuntimes"/> is the source of truth.</summary>
    public required string Runtime { get; set; }

    /// <summary>Relative path within the uploaded tar the runtime wrapper loads, e.g. <c>main.dart</c> / <c>index.js</c>.</summary>
    public required string Entrypoint { get; set; }

    public int TimeoutSeconds { get; set; } = 15;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Event grammar patterns this function triggers on (same matcher as webhooks —
    /// <see cref="Praxy.Realtime.ChannelGrammar.ExpandEventNames"/>). Empty means no event trigger.
    /// </summary>
    public string[] Events { get; set; } = [];

    /// <summary>
    /// Roles permitted to invoke this function over the data plane
    /// (<c>POST /v1/functions/{functionId}/executions</c>). Empty means nobody — deny by default,
    /// per roadmap rule 3, exactly like a freshly created table. Bare roles rather than
    /// <c>action("role")</c> permission strings because a function has only one action; the role
    /// vocabulary itself is the shared one (<c>Praxy.Core.Roles</c>).
    ///
    /// Deliberately does NOT gate the console's own invoke (operator-authenticated, the escape
    /// hatch that keeps a function testable before any role is granted), event triggers, or cron
    /// runs — those are operator-configured server-side paths with no external caller to authorize.
    /// </summary>
    public string[] Execute { get; set; } = [];

    /// <summary>Standard 5-field cron expression (parsed by Cronos), or null for no schedule.</summary>
    public string? Schedule { get; set; }

    /// <summary>Maintained by <c>FunctionScheduler</c>'s claim query; null when unscheduled or not yet computed.</summary>
    public DateTimeOffset? NextScheduledRunAt { get; set; }

    /// <summary>The deployment currently serving invocations. Null until a build first succeeds.</summary>
    public Guid? ActiveDeploymentId { get; set; }

    /// <summary>
    /// The connected GitHub repository, <c>"owner/repo"</c> (Functions git integration) — null until a
    /// git repository is connected via the console. Both this and <see cref="ProductionBranch"/> are
    /// set together (<c>FunctionsService.ConnectRepositoryAsync</c>) and cleared together
    /// (<c>DisconnectRepositoryAsync</c>); a function connects to at most one repository at a time.
    /// </summary>
    public string? RepositoryFullName { get; set; }

    /// <summary>A push to this branch builds and auto-activates; a push to any other branch of the connected repository builds a deployment that finishes `ready` without activating, reachable only via the console's explicit Activate action.</summary>
    public string? ProductionBranch { get; set; }

    /// <summary>
    /// The <see cref="Praxy.Auth.ApiKeyScopes"/> subset an operator has granted this function for
    /// its schedule- and event-triggered executions — the ones with no calling app user to mint a
    /// <c>PRAXY_FUNCTION_JWT</c> for (<c>FunctionExecutionService.BuildEnvAsync</c>). Empty means
    /// no platform credential is injected for those triggers, exactly like before this existed —
    /// deny by default, same posture as <see cref="Execute"/>. Backs <see cref="PlatformApiKeyId"/>.
    /// </summary>
    public string[] PlatformScopes { get; set; } = [];

    /// <summary>
    /// The function-owned <see cref="ApiKey"/> backing <see cref="PlatformScopes"/>, created lazily
    /// the first time an operator grants a scope and updated in place as scopes change — reuses
    /// <c>ApiKeyService</c>/<c>AppPrincipalFilter</c>'s existing <c>X-Praxy-Key</c> authorization
    /// path verbatim rather than inventing a parallel one. No DB-level FK on purpose (same
    /// app-managed-reference style as <see cref="ActiveDeploymentId"/>): if an operator revokes this
    /// key directly from the project's API keys page, <c>FunctionsService.ApplyPlatformScopesAsync</c>
    /// notices on the next scope edit (the targeted update affects zero rows) and transparently
    /// issues a replacement rather than silently keeping a dead reference. Null until a scope is
    /// first granted, and cleared (with the key revoked) the moment scopes go back to empty or the
    /// function itself is deleted — the secret must never outlive the function that owns it.
    /// </summary>
    public Guid? PlatformApiKeyId { get; set; }

    /// <summary>
    /// The above key's secret, encrypted at rest with <see cref="Praxy.Auth.InstanceKey"/> — the
    /// same AES-256-GCM mechanism <see cref="FunctionEnvVar.ProtectedValue"/> already uses. Unlike a
    /// normal <c>ApiKey</c> (hash-only, shown once), this secret has to be recoverable so it can be
    /// re-injected into every matching execution's environment, not just handed out once.
    /// </summary>
    public string? PlatformApiKeySecretProtected { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One environment variable. Value is encrypted at rest with the instance-wide
/// <see cref="Praxy.Auth.InstanceKey"/> (AES-256-GCM) — the same mechanism already used for OAuth
/// provider tokens (<c>Identity.AccessTokenEnc</c>), reused here rather than standing up a second
/// project-key layer. Write-only from the console's perspective: values never round-trip in a GET.
/// </summary>
public class FunctionEnvVar
{
    public required Guid Id { get; set; }
    public required Guid FunctionId { get; set; }
    public required string Key { get; set; }
    public required string ProtectedValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One build: tar upload → image build → ready. <see cref="BuildLog"/> is a queryable, appendable
/// row the console polls/tails — same "the console can always see what happened" principle as
/// <c>SchemaJob</c> and webhook deliveries, not an ephemeral in-memory log.
/// </summary>
public class FunctionDeployment
{
    public required Guid Id { get; set; }
    public required Guid FunctionId { get; set; }
    public required string ProjectId { get; set; }
    public long SourceSizeBytes { get; set; }

    /// <summary>queued | building | ready | failed</summary>
    public string Status { get; set; } = "queued";

    /// <summary>upload | git — an "upload" deployment has a <see cref="FunctionDeploymentSource"/> row; a "git" one instead has <see cref="CommitSha"/>/<see cref="CommitMessage"/>/<see cref="Branch"/> set and is cloned fresh by <c>FunctionBuildWorker</c> at build time.</summary>
    public string Source { get; set; } = "upload";

    public string? CommitSha { get; set; }
    public string? CommitMessage { get; set; }
    public string? Branch { get; set; }

    public string BuildLog { get; set; } = "";
    public string? Error { get; set; }

    /// <summary>The built Docker image tag, set once the build succeeds.</summary>
    public string? ImageTag { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set the moment this deployment becomes (or re-becomes) the function's active one.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }
}

/// <summary>
/// The uploaded build context tar, 1:1 with its deployment but kept in its own table so an EF list
/// query for deployments never accidentally drags a multi-megabyte blob along — CLAUDE.md's
/// "PostgreSQL only, no second datastore" rules out a filesystem build-storage directory (the
/// self-host compose file only persists the Postgres volume), so this is the durable equivalent of
/// open-runtimes' <c>/storage/builds</c>. Deleted once the build finishes (success or failure) —
/// the built image is what matters afterward, not the source that produced it.
/// </summary>
public class FunctionDeploymentSource
{
    public required Guid DeploymentId { get; set; }
    public required byte[] Tar { get; set; }
}

/// <summary>
/// One invocation, sync or async, manual/HTTP/event/schedule-triggered. Async executions are claimed
/// and run by <c>FunctionExecutionWorker</c> off this same table — it doubles as the async queue,
/// same "the row you queue is the row you query" shape webhook deliveries already use.
/// </summary>
public class FunctionExecution
{
    public required Guid Id { get; set; }
    public required Guid FunctionId { get; set; }
    public required string ProjectId { get; set; }
    public Guid? DeploymentId { get; set; }

    /// <summary>http | event | schedule</summary>
    public required string Trigger { get; set; }

    public bool Async { get; set; }

    /// <summary>waiting | processing | completed | failed</summary>
    public string Status { get; set; } = "waiting";

    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public string? RequestBody { get; set; }

    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string Logs { get; set; } = "";
    public string? Errors { get; set; }
    public int? DurationMs { get; set; }

    /// <summary>Whether this invocation paid a container cold-start cost — a pool that's cold when the owner expects it warm should be visible, not a silent latency surprise (CLAUDE.md).</summary>
    public bool ColdStart { get; set; }

    /// <summary>user:&lt;id&gt; | event:&lt;type&gt; | schedule | console</summary>
    public string? TriggeredBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
