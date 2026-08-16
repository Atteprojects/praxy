namespace Praxy.Webhooks;

/// <summary>Every knob configurable, per CLAUDE.md's cross-phase rule. Bound from <c>Praxy:Webhooks:*</c> config in Program.cs, same plain-record-of-defaults shape as <c>SchemaJobRunnerOptions</c>/<c>RealtimeOptions</c>.</summary>
public sealed record WebhookOptions(
    int DispatchPollIntervalSeconds = 2,
    int DeliveryPollIntervalSeconds = 2,
    int TimeoutSeconds = 15,
    int MaxAttempts = 10,
    double BackoffBaseSeconds = 1,
    double BackoffCapSeconds = 300,
    int DisableAfterConsecutiveFailures = 10,
    bool AllowPrivateNetworkTargets = false,
    int MaxResponseBodyCaptureBytes = 8192);
