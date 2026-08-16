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

    /// <summary>Standard 5-field cron expression (parsed by Cronos), or null for no schedule.</summary>
    public string? Schedule { get; set; }

    /// <summary>Maintained by <c>FunctionScheduler</c>'s claim query; null when unscheduled or not yet computed.</summary>
    public DateTimeOffset? NextScheduledRunAt { get; set; }

    /// <summary>The deployment currently serving invocations. Null until a build first succeeds.</summary>
    public Guid? ActiveDeploymentId { get; set; }

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
