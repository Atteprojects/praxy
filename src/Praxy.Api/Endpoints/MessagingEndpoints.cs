using System.Text.Json.Serialization;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Messaging;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record CreateProviderRequest(
    string Type, string Name, string Host, int Port, string? Username, string From, bool UseTls,
    string? Secret, bool? IsDefault);

public sealed record UpdateProviderRequest(
    string? Name, string? Host, int? Port, string? Username, string? From, bool? UseTls,
    string? Secret, bool? ClearSecret, bool? Enabled, bool? IsDefault);

public sealed record MessagingProviderResponse(
    string Id, string Type, string Name, bool Enabled, bool IsDefault,
    string Host, int Port, string? Username, [property: JsonPropertyName("from")] string SenderAddress, bool UseTls,
    bool HasSecret, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static MessagingProviderResponse From(MessagingProvider p)
    {
        var config = EmailProviderConfig.Parse(p.Config);
        return new(
            Ids.Wire(p.Id), p.Type, p.Name, p.Enabled, p.IsDefault,
            config.Host, config.Port, config.Username, config.From, config.UseTls, p.ProtectedSecret is not null,
            p.CreatedAt, p.UpdatedAt);
    }
}

public sealed record CreateTopicRequest(string Key, string Name, string? Description);
public sealed record UpdateTopicRequest(string? Name, string? Description);

public sealed record MessagingTopicResponse(
    string Id, string Key, string Name, string? Description, int SubscriberCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static MessagingTopicResponse From(MessagingTopic t, int subscriberCount) => new(
        Ids.Wire(t.Id), t.Key, t.Name, t.Description, subscriberCount, t.CreatedAt, t.UpdatedAt);
}

public sealed record SubscribeRequest(string UserId);

public sealed record MessagingSubscriberResponse(string Id, string UserId, string Email, DateTimeOffset CreatedAt)
{
    public static MessagingSubscriberResponse From(MessagingSubscriber s, MessagingTarget t) => new(
        Ids.Wire(s.Id), Ids.Wire(t.UserId), t.Identifier, s.CreatedAt);
}

public sealed record SetTemplateRequest(string Subject, string Body);

public sealed record MessagingTemplateResponse(string Key, string Subject, string Body, bool Overridden)
{
    public static MessagingTemplateResponse From(string key, RenderedTemplate t) => new(key, t.Subject, t.Body, t.Overridden);
}

public sealed record CreateMessageRequest(string Subject, string Body, string[]? TopicIds, string[]? UserIds);

public sealed record MessageResponse(
    string Id, string Type, string Subject, string Body, string Status,
    string[] TopicIds, string[] UserIds, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static MessageResponse From(Message m) => new(
        Ids.Wire(m.Id), m.Type, m.Subject, m.Body, m.Status,
        [.. m.TopicIds.Select(Ids.Wire)], [.. m.UserIds.Select(Ids.Wire)], m.CreatedAt, m.CompletedAt);
}

public sealed record MessageTargetResponse(
    string Id, string Identifier, string Status, string? Error, DateTimeOffset? DeliveredAt, DateTimeOffset CreatedAt)
{
    public static MessageTargetResponse From(MessageTarget t) => new(
        Ids.Wire(t.Id), t.Identifier, t.Status, t.Error, t.DeliveredAt, t.CreatedAt);
}

/// <summary>
/// Operator-facing surface for Messaging (Phase 8): providers, topics + subscribers, templates,
/// messages + per-target delivery status. Same operator-filter chain and audit-log convention as
/// <see cref="WebhookEndpoints"/>/<see cref="FunctionEndpoints"/>; entirely console-admin — no
/// data-plane endpoints this phase, same boundary Webhooks drew (sending is an operator action, not
/// something app users or API keys trigger).
/// </summary>
public static class MessagingEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/v1/console/projects/{projectId}/messaging")
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>();

        admin.MapGet("/providers", ListProviders);
        admin.MapPost("/providers", CreateProvider);
        admin.MapGet("/providers/{providerId}", GetProvider);
        admin.MapPatch("/providers/{providerId}", UpdateProvider);
        admin.MapDelete("/providers/{providerId}", DeleteProvider);

        admin.MapGet("/topics", ListTopics);
        admin.MapPost("/topics", CreateTopic);
        admin.MapGet("/topics/{topicId}", GetTopic);
        admin.MapPatch("/topics/{topicId}", UpdateTopic);
        admin.MapDelete("/topics/{topicId}", DeleteTopic);
        admin.MapGet("/topics/{topicId}/subscribers", ListSubscribers);
        admin.MapPost("/topics/{topicId}/subscribers", Subscribe);
        admin.MapDelete("/topics/{topicId}/subscribers/{subscriberId}", Unsubscribe);

        admin.MapGet("/templates", ListTemplates);
        admin.MapGet("/templates/{key}", GetTemplate);
        admin.MapPut("/templates/{key}", SetTemplate);
        admin.MapDelete("/templates/{key}", ResetTemplate);

        admin.MapGet("/messages", ListMessages);
        admin.MapPost("/messages", CreateMessage);
        admin.MapGet("/messages/{messageId}", GetMessage);
    }

    // ---- providers ------------------------------------------------------------------------------

    private static async Task<IResult> ListProviders(HttpContext http, MessagingProvidersService providers, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await providers.ListAsync(project.Id, ct);
        return Results.Ok(new { total = list.Count, providers = list.Select(MessagingProviderResponse.From) });
    }

    private static async Task<IResult> CreateProvider(
        CreateProviderRequest req, HttpContext http, PraxyDb db, MessagingProvidersService providers, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var config = new EmailProviderConfig(req.Host, req.Port, req.Username, req.From, req.UseTls);
        var provider = await providers.CreateAsync(project.Id, req.Type, req.Name, config, req.Secret, req.IsDefault ?? false, ct);
        await AuditAsync(db, http, project.Id, "messaging.providers.create", $"messaging_provider/{Ids.Wire(provider.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/messaging/providers/{Ids.Wire(provider.Id)}", MessagingProviderResponse.From(provider));
    }

    private static async Task<IResult> GetProvider(
        string providerId, HttpContext http, MessagingProvidersService providers, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var provider = await FindProviderAsync(providers, project.Id, providerId, ct);
        return Results.Ok(MessagingProviderResponse.From(provider));
    }

    private static async Task<IResult> UpdateProvider(
        string providerId, UpdateProviderRequest req, HttpContext http, PraxyDb db, MessagingProvidersService providers,
        CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var provider = await FindProviderAsync(providers, project.Id, providerId, ct);

        EmailProviderConfig? config = null;
        if (req.Host is not null || req.Port is not null || req.Username is not null || req.From is not null || req.UseTls is not null)
        {
            var current = EmailProviderConfig.Parse(provider.Config);
            config = new EmailProviderConfig(
                req.Host ?? current.Host, req.Port ?? current.Port, req.Username ?? current.Username,
                req.From ?? current.From, req.UseTls ?? current.UseTls);
        }

        var updated = await providers.UpdateAsync(
            provider, req.Name, config, req.Secret, req.ClearSecret ?? false, req.Enabled, req.IsDefault, ct);
        await AuditAsync(db, http, project.Id, "messaging.providers.update", $"messaging_provider/{providerId}", ct);
        return Results.Ok(MessagingProviderResponse.From(updated));
    }

    private static async Task<IResult> DeleteProvider(
        string providerId, HttpContext http, PraxyDb db, MessagingProvidersService providers, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var provider = await FindProviderAsync(providers, project.Id, providerId, ct);
        await providers.DeleteAsync(provider, ct);
        await AuditAsync(db, http, project.Id, "messaging.providers.delete", $"messaging_provider/{providerId}", ct);
        return Results.NoContent();
    }

    // ---- topics ---------------------------------------------------------------------------------

    private static async Task<IResult> ListTopics(HttpContext http, MessagingTopicsService topics, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await topics.ListAsync(project.Id, ct);
        return Results.Ok(new { total = list.Count, topics = list.Select(x => MessagingTopicResponse.From(x.Topic, x.SubscriberCount)) });
    }

    private static async Task<IResult> CreateTopic(
        CreateTopicRequest req, HttpContext http, PraxyDb db, MessagingTopicsService topics, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var topic = await topics.CreateAsync(project.Id, req.Key, req.Name, req.Description, ct);
        await AuditAsync(db, http, project.Id, "messaging.topics.create", $"messaging_topic/{Ids.Wire(topic.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/messaging/topics/{Ids.Wire(topic.Id)}", MessagingTopicResponse.From(topic, 0));
    }

    private static async Task<IResult> GetTopic(string topicId, HttpContext http, MessagingTopicsService topics, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var topic = await FindTopicAsync(topics, project.Id, topicId, ct);
        var subscribers = await topics.ListSubscribersAsync(topic.Id, ct);
        return Results.Ok(MessagingTopicResponse.From(topic, subscribers.Count));
    }

    private static async Task<IResult> UpdateTopic(
        string topicId, UpdateTopicRequest req, HttpContext http, PraxyDb db, MessagingTopicsService topics, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var topic = await FindTopicAsync(topics, project.Id, topicId, ct);
        var updated = await topics.UpdateAsync(topic, req.Name, req.Description, ct);
        var subscribers = await topics.ListSubscribersAsync(updated.Id, ct);
        await AuditAsync(db, http, project.Id, "messaging.topics.update", $"messaging_topic/{topicId}", ct);
        return Results.Ok(MessagingTopicResponse.From(updated, subscribers.Count));
    }

    private static async Task<IResult> DeleteTopic(
        string topicId, HttpContext http, PraxyDb db, MessagingTopicsService topics, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var topic = await FindTopicAsync(topics, project.Id, topicId, ct);
        await topics.DeleteAsync(topic, ct);
        await AuditAsync(db, http, project.Id, "messaging.topics.delete", $"messaging_topic/{topicId}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListSubscribers(
        string topicId, HttpContext http, MessagingTopicsService topics, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var topic = await FindTopicAsync(topics, project.Id, topicId, ct);
        var list = await topics.ListSubscribersAsync(topic.Id, ct);
        return Results.Ok(new
        {
            total = list.Count,
            subscribers = list.Select(x => MessagingSubscriberResponse.From(x.Subscriber, x.Target)),
        });
    }

    private static async Task<IResult> Subscribe(
        string topicId, SubscribeRequest req, HttpContext http, PraxyDb db, MessagingTopicsService topics, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var topic = await FindTopicAsync(topics, project.Id, topicId, ct);
        if (!Ids.TryParseWire(req.UserId, out var userId))
            throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");
        var subscriber = await topics.SubscribeAsync(project.Id, topic, userId, ct);
        var target = await db.MessagingTargets.FindAsync([subscriber.TargetId], ct)
            ?? throw new InvalidOperationException("Target vanished immediately after being subscribed.");
        await AuditAsync(db, http, project.Id, "messaging.topics.subscribe", $"messaging_topic/{topicId}/subscriber/{Ids.Wire(subscriber.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/messaging/topics/{topicId}/subscribers/{Ids.Wire(subscriber.Id)}",
            MessagingSubscriberResponse.From(subscriber, target));
    }

    private static async Task<IResult> Unsubscribe(
        string topicId, string subscriberId, HttpContext http, PraxyDb db, MessagingTopicsService topics, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var topic = await FindTopicAsync(topics, project.Id, topicId, ct);
        if (!Ids.TryParseWire(subscriberId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.MessagingSubscriberNotFound, "Subscriber not found.");
        await topics.UnsubscribeAsync(topic.Id, parsed, ct);
        await AuditAsync(db, http, project.Id, "messaging.topics.unsubscribe", $"messaging_topic/{topicId}/subscriber/{subscriberId}", ct);
        return Results.NoContent();
    }

    // ---- templates ------------------------------------------------------------------------------

    private static async Task<IResult> ListTemplates(HttpContext http, MessagingTemplatesService templates, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await templates.ListAsync(project.Id, ct);
        return Results.Ok(new { templates = list.Select(x => MessagingTemplateResponse.From(x.Key, x.Template)) });
    }

    private static async Task<IResult> GetTemplate(
        string key, HttpContext http, MessagingTemplatesService templates, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var rendered = await templates.GetAsync(project.Id, key, ct);
        return Results.Ok(MessagingTemplateResponse.From(key, rendered));
    }

    private static async Task<IResult> SetTemplate(
        string key, SetTemplateRequest req, HttpContext http, PraxyDb db, MessagingTemplatesService templates, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var rendered = await templates.SetAsync(project.Id, key, req.Subject, req.Body, ct);
        await AuditAsync(db, http, project.Id, "messaging.templates.set", $"messaging_template/{key}", ct);
        return Results.Ok(MessagingTemplateResponse.From(key, rendered));
    }

    private static async Task<IResult> ResetTemplate(
        string key, HttpContext http, PraxyDb db, MessagingTemplatesService templates, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var rendered = await templates.ResetAsync(project.Id, key, ct);
        await AuditAsync(db, http, project.Id, "messaging.templates.reset", $"messaging_template/{key}", ct);
        return Results.Ok(MessagingTemplateResponse.From(key, rendered));
    }

    // ---- messages -------------------------------------------------------------------------------

    private static async Task<IResult> ListMessages(HttpContext http, MessagesService messages, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var (limit, offset) = ListParams(http);
        var (total, page) = await messages.ListAsync(project.Id, limit, offset, ct);
        return Results.Ok(new { total, messages = page.Select(MessageResponse.From) });
    }

    private static async Task<IResult> CreateMessage(
        CreateMessageRequest req, HttpContext http, PraxyDb db, MessagesService messages, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var topicIds = ParseIds(req.TopicIds, "topicIds");
        var userIds = ParseIds(req.UserIds, "userIds");
        var message = await messages.CreateAsync(project.Id, req.Subject, req.Body, topicIds, userIds, ct);
        await AuditAsync(db, http, project.Id, "messaging.messages.create", $"message/{Ids.Wire(message.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/messaging/messages/{Ids.Wire(message.Id)}", MessageResponse.From(message));
    }

    private static async Task<IResult> GetMessage(
        string messageId, HttpContext http, MessagesService messages, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var message = await FindMessageAsync(messages, project.Id, messageId, ct);
        var targets = await messages.ListTargetsAsync(message.Id, ct);
        return Results.Ok(new { message = MessageResponse.From(message), targets = targets.Select(MessageTargetResponse.From) });
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task<MessagingProvider> FindProviderAsync(
        MessagingProvidersService providers, string projectId, string providerId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(providerId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.MessagingProviderNotFound, "Provider not found.");
        return await providers.GetAsync(projectId, parsed, ct);
    }

    private static async Task<MessagingTopic> FindTopicAsync(
        MessagingTopicsService topics, string projectId, string topicId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(topicId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.MessagingTopicNotFound, "Topic not found.");
        return await topics.GetAsync(projectId, parsed, ct);
    }

    private static async Task<Message> FindMessageAsync(
        MessagesService messages, string projectId, string messageId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(messageId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.MessagingMessageNotFound, "Message not found.");
        return await messages.GetAsync(projectId, parsed, ct);
    }

    private static Guid[] ParseIds(string[]? wireIds, string field)
    {
        if (wireIds is null || wireIds.Length == 0)
            return [];
        var parsed = new Guid[wireIds.Length];
        for (var i = 0; i < wireIds.Length; i++)
        {
            if (!Ids.TryParseWire(wireIds[i], out var id))
                throw PraxyException.ArgumentInvalid("Invalid message payload.",
                    new Dictionary<string, string[]> { [field] = [$"'{wireIds[i]}' is not a valid id."] });
            parsed[i] = id;
        }
        return parsed;
    }

    private static (int Limit, int Offset) ListParams(HttpContext http)
    {
        var limit = int.TryParse(http.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 100) : 25;
        var offset = int.TryParse(http.Request.Query["offset"], out var o) ? Math.Max(0, o) : 0;
        return (limit, offset);
    }

    private static async Task AuditAsync(
        PraxyDb db, HttpContext http, string projectId, string action, string resource, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Actor = $"admin:{op.Account.Id}",
            Action = action,
            Resource = resource,
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }
}
