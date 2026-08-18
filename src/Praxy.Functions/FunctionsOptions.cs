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
    int MaxResponseCaptureBytes = 65536,
    long MaxSourceBytes = 26_214_400);
