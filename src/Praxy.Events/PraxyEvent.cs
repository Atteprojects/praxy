using System.Text.Json.Nodes;

namespace Praxy.Events;

/// <summary>
/// One event on the shared stream. Realtime fans these out in-process (Phase 4);
/// webhooks consume the durable outbox copy (Phase 6).
/// </summary>
public sealed record PraxyEvent(
    string Id,
    DateTimeOffset At,
    string ProjectId,
    string Type,
    string[] Permissions,
    JsonNode? Payload);
