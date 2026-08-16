namespace Praxy.Persistence.Entities;

/// <summary>
/// A configured delivery channel for a project. Modeled generically per the roadmap ("Providers/SMS
/// /push are additive later — model providers generically now"): <see cref="Type"/> is a discriminator
/// (only <c>email</c> ships this phase), <see cref="Config"/> holds the non-secret driver settings
/// (host/port/username/from/useTls for email) and <see cref="ProtectedSecret"/> the encrypted one
/// (password), reusing <see cref="Praxy.Auth.InstanceKey"/> exactly like
/// <see cref="FunctionEnvVar.ProtectedValue"/> does. A project can hold several providers of the same
/// type; sends use whichever is <see cref="Enabled"/> and <see cref="IsDefault"/> — never a bare
/// <c>Project.Settings</c> blob, because providers are an independently CRUDable list (create, disable,
/// delete) the same way webhook subscriptions and functions are, not a singleton settings record.
/// </summary>
public class MessagingProvider
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Type { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>At most one default per (ProjectId, Type); sends and the auth-email bridge resolve this one.</summary>
    public bool IsDefault { get; set; }

    public string Config { get; set; } = "{}";
    public string? ProtectedSecret { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A named group of subscribers a message can be sent to.</summary>
public class MessagingTopic
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One deliverable address for one app user on one channel. This phase only ever creates the
/// <c>email</c> type, sourced from <see cref="Praxy.Persistence.Entities.User.Email"/> at the moment a
/// user is first subscribed or sent to — get-or-create, not backfilled at signup, so Messaging never
/// has to reach into Phase 1's signup path.
/// </summary>
public class MessagingTarget
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required Guid UserId { get; set; }
    public required string Type { get; set; }
    public required string Identifier { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Join row: one target subscribed to one topic.</summary>
public class MessagingSubscriber
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required Guid TopicId { get; set; }
    public required Guid TargetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One composed send: to a set of topics, a set of explicit users, or both — the roadmap's
/// "send-to-topic and send-to-users". <see cref="Status"/> is a coarse batch-progress indicator
/// (<c>processing</c> until every <see cref="MessageTarget"/> reaches a terminal state, then
/// <c>completed</c>) — per-target outcomes, which is what "per-message delivery status" actually
/// means, live on <see cref="MessageTarget"/> itself, the same split <c>FunctionDeployment</c> and
/// its executions use.
/// </summary>
public class Message
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Type { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public string Status { get; set; } = "processing";
    public Guid[] TopicIds { get; set; } = [];
    public Guid[] UserIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// One resolved recipient of one <see cref="Message"/> — the row the console's per-message delivery
/// view sits on, and the row <c>MessageSendWorker</c> claims and finalizes. Same
/// "the row you queue is the row you query" shape <c>FunctionExecution</c> and
/// <c>WebhookDelivery</c> already use. <see cref="Identifier"/> is a snapshot of the address at send
/// time, same reasoning as <c>WebhookDelivery.Payload</c> snapshotting its event.
/// </summary>
public class MessageTarget
{
    public required Guid Id { get; set; }
    public required Guid MessageId { get; set; }
    public required string ProjectId { get; set; }
    public required Guid TargetId { get; set; }
    public required string Identifier { get; set; }

    /// <summary>queued | sending | sent | failed</summary>
    public string Status { get; set; } = "queued";

    public string? Error { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A per-project override of one of Praxy's own transactional auth emails (verification, recovery,
/// invitation — <c>Praxy.Auth.AuthEmailTemplateKeys</c>). No row means "use the compiled-in default":
/// projects need no backfill, the same way an absent <c>ProjectAuthSettings</c> section means defaults.
/// <see cref="Subject"/>/<see cref="Body"/> carry <c>{{var}}</c> placeholders substituted at send time.
/// </summary>
public class MessagingTemplate
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Channel { get; set; }
    public required string Key { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
