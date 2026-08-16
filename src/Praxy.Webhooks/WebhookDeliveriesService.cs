using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Webhooks;

/// <summary>The delivery log console screen sits on: listing, the per-attempt log, and redelivery.</summary>
public sealed class WebhookDeliveriesService(PraxyDb db, WebhookDeliverySignal signal)
{
    public async Task<(int Total, List<WebhookDelivery> Deliveries)> ListAsync(
        Guid subscriptionId, int limit, int offset, CancellationToken ct)
    {
        var query = db.WebhookDeliveries.Where(d => d.SubscriptionId == subscriptionId);
        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(d => d.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return (total, page);
    }

    public async Task<WebhookDelivery> GetAsync(Guid subscriptionId, Guid deliveryId, CancellationToken ct) =>
        await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId && d.SubscriptionId == subscriptionId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.WebhookDeliveryNotFound, "Delivery not found.");

    public Task<List<WebhookDeliveryAttempt>> ListAttemptsAsync(Guid deliveryId, CancellationToken ct) =>
        db.WebhookDeliveryAttempts
            .Where(a => a.DeliveryId == deliveryId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync(ct);

    /// <summary>
    /// Clones a fresh delivery pointed at the same event/payload rather than mutating
    /// <paramref name="original"/>, so its attempt history stays intact and the redelivery gets its
    /// own — same reasoning a version-control revert makes a new commit instead of rewriting history.
    /// </summary>
    public async Task<WebhookDelivery> RedeliverAsync(WebhookDelivery original, CancellationToken ct)
    {
        if (original.Status is "queued" or "delivering")
            throw new PraxyException(400, ErrorTypes.WebhookInvalidRedeliverState,
                $"Only succeeded or failed deliveries can be redelivered (this one is '{original.Status}').");

        var clone = new WebhookDelivery
        {
            Id = Ids.NewUuid(),
            SubscriptionId = original.SubscriptionId,
            ProjectId = original.ProjectId,
            EventId = original.EventId,
            EventType = original.EventType,
            Payload = original.Payload,
            RedeliveredFromId = original.Id,
        };
        db.WebhookDeliveries.Add(clone);
        await db.SaveChangesAsync(ct);
        signal.Notify();
        return clone;
    }
}
