namespace Praxy.Functions;

/// <summary>Every knob configurable, per CLAUDE.md's cross-phase rule — bound from <c>Praxy:Functions:*</c> config in Program.cs, same plain-record-of-defaults shape as <c>WebhookOptions</c>/<c>SchemaJobRunnerOptions</c>.</summary>
public sealed record FunctionsOptions(
    string DockerEndpoint = "unix:///var/run/docker.sock",
    string DockerNetwork = "",
    string DartBaseImage = "dart:stable",
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
