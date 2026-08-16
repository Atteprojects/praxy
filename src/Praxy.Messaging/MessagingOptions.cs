namespace Praxy.Messaging;

/// <summary>Every knob configurable, per CLAUDE.md's cross-phase rule — bound from <c>Praxy:Messaging:*</c> config in Program.cs, same plain-record-of-defaults shape as <c>WebhookOptions</c>/<c>FunctionsOptions</c>.</summary>
public sealed record MessagingOptions(
    int SendPollIntervalSeconds = 2,
    int MaxSubjectLength = 998,
    int MaxBodyLength = 65536,
    int MaxTargetsPerMessage = 10_000);
