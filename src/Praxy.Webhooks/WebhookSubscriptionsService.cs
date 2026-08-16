using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Webhooks;

/// <summary>Console-facing CRUD for webhook subscriptions — the delivery pipeline (dispatcher/worker) never touches this directly, same separation as <c>SchemaJobsService</c> vs. <c>SchemaJobRunner</c>.</summary>
public sealed class WebhookSubscriptionsService(PraxyDb db)
{
    public Task<List<WebhookSubscription>> ListAsync(string projectId, CancellationToken ct) =>
        db.WebhookSubscriptions.Where(w => w.ProjectId == projectId).OrderByDescending(w => w.CreatedAt).ToListAsync(ct);

    public async Task<WebhookSubscription> GetAsync(string projectId, Guid id, CancellationToken ct) =>
        await db.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.WebhookNotFound, "Webhook not found.");

    /// <summary>Returns the plaintext secret alongside the resource — the only time it is ever handed back (same reveal-once shape as <c>ApiKeyService.CreateAsync</c>).</summary>
    public async Task<(WebhookSubscription Subscription, string Secret)> CreateAsync(
        string projectId, string name, string url, string[] events, CancellationToken ct)
    {
        var fields = Validate(name, url, events);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid webhook payload.", fields);

        var secret = WebhookSecrets.Generate();
        var subscription = new WebhookSubscription
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Name = name.Trim(),
            Url = url.Trim(),
            Events = [.. events.Select(e => e.Trim())],
            Secret = secret,
        };
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
        return (subscription, secret);
    }

    public async Task<WebhookSubscription> UpdateAsync(
        WebhookSubscription subscription, string? name, string? url, string[]? events, bool? enabled,
        CancellationToken ct)
    {
        var fields = Validate(name ?? subscription.Name, url ?? subscription.Url, events ?? subscription.Events);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid webhook payload.", fields);

        if (name is not null)
            subscription.Name = name.Trim();
        if (url is not null)
            subscription.Url = url.Trim();
        if (events is not null)
            subscription.Events = [.. events.Select(e => e.Trim())];
        if (enabled is { } value)
        {
            subscription.Enabled = value;
            // Re-enabling (manually, or after fixing the endpoint) clears the auto-disable trail —
            // otherwise one more failure would immediately re-trip the threshold.
            if (value)
            {
                subscription.DisabledReason = null;
                subscription.ConsecutiveFailures = 0;
            }
        }
        subscription.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task DeleteAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        db.WebhookSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(ct);
    }

    private static Dictionary<string, string[]> Validate(string name, string url, string[] events)
    {
        var fields = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];

        if (url.Length > 2048 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            fields["url"] = ["Must be an absolute http:// or https:// URL, 2048 characters or fewer."];

        if (events.Length == 0)
            fields["events"] = ["At least one event pattern is required."];
        else if (events.Any(e => !IsValidPattern(e)))
            fields["events"] = ["Each pattern must be dot-separated segments of letters, digits, underscore or '*' (e.g. 'databases.*.tables.*.rows.*.create')."];

        return fields;
    }

    private static bool IsValidPattern(string pattern) =>
        !string.IsNullOrWhiteSpace(pattern) &&
        pattern.Split('.').All(part => part.Length > 0 &&
            part.All(c => c == '*' || char.IsAsciiLetterOrDigit(c) || c == '_'));
}
