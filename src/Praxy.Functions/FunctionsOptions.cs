namespace Praxy.Functions;

/// <summary>Every knob configurable, per CLAUDE.md's cross-phase rule — bound from <c>Praxy:Functions:*</c> config in Program.cs, same plain-record-of-defaults shape as <c>WebhookOptions</c>/<c>SchemaJobRunnerOptions</c>.</summary>
public sealed record FunctionsOptions(
    string DockerEndpoint = "unix:///var/run/docker.sock",
    string DockerNetwork = "",
    // Pinned to a real version, not the floating "stable" tag — "stable" silently resolves to
    // whatever Dart most recently cut (irreproducible builds across time) and is meaningless as a
    // version to show in the console's runtime picker. Bump deliberately, not by drift.
    string DartBaseImage = "dart:3.13.0",
    string NodeBaseImage = "node:22-alpine",
    int BuildPollIntervalSeconds = 2,
    int ExecutionPollIntervalSeconds = 2,
    int SchedulePollIntervalSeconds = 5,
    int BuildTimeoutSeconds = 600,
    int ColdStartTimeoutSeconds = 60,
    int MaxSyncTimeoutSeconds = 30,
    int WarmPoolSize = 10,
    int MaxIdleSeconds = 300,
    int PoolSweepIntervalSeconds = 30,
    long MemoryLimitMb = 256,
    double CpuLimit = 1.0,
    // security-review-phase-1: a fork bomb was previously unbounded (verified live — a container
    // with no PidsLimit can spawn processes until the *host* runs out of PIDs). 256 is generous for
    // a single-request Node/Dart invocation, which normally holds a handful of processes at most.
    int PidsLimit = 256,
    // security-review-phase-1 follow-up: an invocation carrying an invocation-scoped credential is
    // never pooled (see WarmPool.AcquireAsync), so it cold-starts a container of its own that
    // WarmPoolSize does not bound — and "triggered by an app user" is the *primary* data-plane path
    // for a BaaS, not an edge case. Without a cap, concurrent user-triggered invocations spawn one
    // container each: the functions rate limiter partitions per caller and is a fixed window, so it
    // bounds arrival rate, not concurrency. 16 x MemoryLimitMb (256) is 4 GB of container limits,
    // sized for a small VPS; raise it on a bigger host.
    // security-review-phase-1 follow-up (findings B/F): the rootfs is read-only and the only
    // writable place is a size-capped tmpfs at /tmp. Docker has no portable way to cap a
    // container's writable layer (--storage-opt needs devicemapper or XFS project quotas the
    // installer can't assume), so instead nothing writes to the layer at all — a function that
    // writes fills a RAM-backed mount bounded by this, not the host's shared disk. Counts against
    // MemoryLimitMb like any other page the container touches.
    int TmpfsSizeMb = 64,
    int MaxConcurrentIsolatedContainers = 16,
    // Wait this long for a slot before giving up with a 503 — smooths a burst instead of failing at
    // the boundary, since a sync invocation holds its slot for at most MaxSyncTimeoutSeconds.
    int IsolatedContainerWaitSeconds = 5,
    int MaxResponseCaptureBytes = 65536,
    long MaxSourceBytes = 26_214_400);
