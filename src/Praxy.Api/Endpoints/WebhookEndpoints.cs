using System.Text.Json.Nodes;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Webhooks;

namespace Praxy.Api.Endpoints;

public sealed record CreateWebhookRequest(string Name, string Url, string[] Events);

public sealed record UpdateWebhookRequest(string? Name, string? Url, string[]? Events, bool? Enabled);

public sealed record WebhookResponse(
    string Id, string Name, string Url, string[] Events, bool Enabled, string? DisabledReason,
    int ConsecutiveFailures, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static WebhookResponse From(WebhookSubscription w) => new(
        Ids.Wire(w.Id), w.Name, w.Url, w.Events, w.Enabled, w.DisabledReason,
        w.ConsecutiveFailures, w.CreatedAt, w.UpdatedAt);
}

public sealed record WebhookDeliveryResponse(
    string Id, string EventId, string EventType, string Status, int Attempts,
    DateTimeOffset NextAttemptAt, DateTimeOffset? LastAttemptAt, int? LastStatusCode, string? LastError,
    string? RedeliveredFromId, DateTimeOffset CreatedAt)
{
    public static WebhookDeliveryResponse From(WebhookDelivery d) => new(
        Ids.Wire(d.Id), Ids.Wire(d.EventId), d.EventType, d.Status, d.Attempts,
        d.NextAttemptAt, d.LastAttemptAt, d.LastStatusCode, d.LastError,
        d.RedeliveredFromId is { } r ? Ids.Wire(r) : null, d.CreatedAt);
}

public sealed record WebhookDeliveryAttemptResponse(
    int AttemptNumber, DateTimeOffset StartedAt, int DurationMs, int? StatusCode, string? ResponseBody, string? Error)
{
    public static WebhookDeliveryAttemptResponse From(WebhookDeliveryAttempt a) => new(
        a.AttemptNumber, a.StartedAt, a.DurationMs, a.StatusCode, a.ResponseBody, a.Error);
}

/// <summary>
/// Operator-facing webhook subscriptions + their delivery log — everything the Phase 6 console
/// screens sit on. Same filter chain and audit-log convention as <see cref="ConsoleAuthAdminEndpoints"/>;
/// the actual dispatch/delivery pipeline lives in the <c>Praxy.Webhooks</c> hosted services and
/// never routes through HTTP.
/// </summary>
public static class WebhookEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/v1/console/projects/{projectId}/webhooks")
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>();

        admin.MapGet("", ListWebhooks);
        admin.MapPost("", CreateWebhook);
        admin.MapGet("/{webhookId}", GetWebhook);
        admin.MapPatch("/{webhookId}", UpdateWebhook);
        admin.MapDelete("/{webhookId}", DeleteWebhook);

        admin.MapGet("/{webhookId}/deliveries", ListDeliveries);
        admin.MapGet("/{webhookId}/deliveries/{deliveryId}", GetDelivery);
        admin.MapPost("/{webhookId}/deliveries/{deliveryId}/redeliver", Redeliver);
    }

    // ---- subscriptions --------------------------------------------------------------------------

    private static async Task<IResult> ListWebhooks(
        HttpContext http, WebhookSubscriptionsService webhooks, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await webhooks.ListAsync(project.Id, ct);
        return Results.Ok(new { total = list.Count, webhooks = list.Select(WebhookResponse.From) });
    }

    private static async Task<IResult> CreateWebhook(
        CreateWebhookRequest req, HttpContext http, PraxyDb db, WebhookSubscriptionsService webhooks,
        CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var (subscription, secret) = await webhooks.CreateAsync(project.Id, req.Name, req.Url, req.Events, ct);
        await AuditAsync(db, http, project.Id, "webhooks.create", $"webhook/{Ids.Wire(subscription.Id)}", ct);
        // The signing secret appears exactly once — here. No GET ever echoes it back.
        return Results.Created(
            $"/v1/console/projects/{project.Id}/webhooks/{Ids.Wire(subscription.Id)}",
            new { webhook = WebhookResponse.From(subscription), secret });
    }

    private static async Task<IResult> GetWebhook(
        string webhookId, HttpContext http, WebhookSubscriptionsService webhooks, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var subscription = await FindAsync(webhooks, project.Id, webhookId, ct);
        return Results.Ok(WebhookResponse.From(subscription));
    }

    private static async Task<IResult> UpdateWebhook(
        string webhookId, UpdateWebhookRequest req, HttpContext http, PraxyDb db,
        WebhookSubscriptionsService webhooks, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var subscription = await FindAsync(webhooks, project.Id, webhookId, ct);
        var updated = await webhooks.UpdateAsync(subscription, req.Name, req.Url, req.Events, req.Enabled, ct);
        await AuditAsync(db, http, project.Id, "webhooks.update", $"webhook/{webhookId}", ct);
        return Results.Ok(WebhookResponse.From(updated));
    }

    private static async Task<IResult> DeleteWebhook(
        string webhookId, HttpContext http, PraxyDb db, WebhookSubscriptionsService webhooks, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var subscription = await FindAsync(webhooks, project.Id, webhookId, ct);
        await webhooks.DeleteAsync(subscription, ct);
        await AuditAsync(db, http, project.Id, "webhooks.delete", $"webhook/{webhookId}", ct);
        return Results.NoContent();
    }

    // ---- deliveries -----------------------------------------------------------------------------

    private static async Task<IResult> ListDeliveries(
        string webhookId, HttpContext http, WebhookSubscriptionsService webhooks,
        WebhookDeliveriesService deliveries, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var subscription = await FindAsync(webhooks, project.Id, webhookId, ct);
        var (limit, offset) = ListParams(http);
        var (total, page) = await deliveries.ListAsync(subscription.Id, limit, offset, ct);
        return Results.Ok(new { total, deliveries = page.Select(WebhookDeliveryResponse.From) });
    }

    private static async Task<IResult> GetDelivery(
        string webhookId, string deliveryId, HttpContext http, WebhookSubscriptionsService webhooks,
        WebhookDeliveriesService deliveries, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var subscription = await FindAsync(webhooks, project.Id, webhookId, ct);
        var delivery = await FindDeliveryAsync(deliveries, subscription.Id, deliveryId, ct);
        var attempts = await deliveries.ListAttemptsAsync(delivery.Id, ct);
        return Results.Ok(new
        {
            delivery = WebhookDeliveryResponse.From(delivery),
            payload = JsonNode.Parse(delivery.Payload),
            attempts = attempts.Select(WebhookDeliveryAttemptResponse.From),
        });
    }

    private static async Task<IResult> Redeliver(
        string webhookId, string deliveryId, HttpContext http, PraxyDb db, WebhookSubscriptionsService webhooks,
        WebhookDeliveriesService deliveries, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var subscription = await FindAsync(webhooks, project.Id, webhookId, ct);
        var original = await FindDeliveryAsync(deliveries, subscription.Id, deliveryId, ct);
        var redelivered = await deliveries.RedeliverAsync(original, ct);
        await AuditAsync(db, http, project.Id, "webhooks.redeliver", $"webhook/{webhookId}/delivery/{deliveryId}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/webhooks/{webhookId}/deliveries/{Ids.Wire(redelivered.Id)}",
            WebhookDeliveryResponse.From(redelivered));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static async Task<WebhookSubscription> FindAsync(
        WebhookSubscriptionsService webhooks, string projectId, string webhookId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(webhookId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.WebhookNotFound, "Webhook not found.");
        return await webhooks.GetAsync(projectId, parsed, ct);
    }

    private static async Task<WebhookDelivery> FindDeliveryAsync(
        WebhookDeliveriesService deliveries, Guid subscriptionId, string deliveryId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(deliveryId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.WebhookDeliveryNotFound, "Delivery not found.");
        return await deliveries.GetAsync(subscriptionId, parsed, ct);
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
