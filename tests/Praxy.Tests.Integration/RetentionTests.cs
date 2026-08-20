using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// <c>RetentionSweeper</c> (post-v0.1.0 gap #4) has no console or API surface — it is purely a
/// background delete job — so these tests seed rows directly via EF Core (same precedent as
/// <c>SchemaEngineTests</c>' broken-job injection: <c>Factory.Services.CreateScope()</c> then
/// <c>GetRequiredService&lt;PraxyDb&gt;()</c>) and poll the database directly for the sweep's effect
/// rather than through an endpoint.
/// </summary>
public class RetentionTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        // A 1-second sweep interval so the tests don't wait on the production 3600s default;
        // the age windows below are what's actually under test.
        ["Praxy:Retention:SweepIntervalSeconds"] = "1",
        ["Praxy:Retention:EventsMaxAgeDays"] = "30",
        ["Praxy:Retention:WebhookDeliveriesMaxAgeDays"] = "30",
        ["Praxy:Retention:AuditLogMaxAgeDays"] = "30",
    };

    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-31);
    private static readonly DateTimeOffset Recent = DateTimeOffset.UtcNow.AddDays(-1);

    [Fact]
    public async Task An_old_fully_claimed_event_is_deleted_a_partially_claimed_one_is_not()
    {
        var (_, projectId) = await SetupProjectAsync();
        var claimedId = Guid.NewGuid();
        var unclaimedId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
            db.Events.AddRange(
                new OutboxEvent
                {
                    Id = claimedId, ProjectId = projectId, Type = "test.claimed", CreatedAt = Old,
                    WebhooksDispatchedAt = Old, FunctionsDispatchedAt = Old,
                },
                new OutboxEvent
                {
                    // Webhooks claimed it, functions never did — must survive the sweep.
                    Id = unclaimedId, ProjectId = projectId, Type = "test.unclaimed", CreatedAt = Old,
                    WebhooksDispatchedAt = Old, FunctionsDispatchedAt = null,
                });
            await db.SaveChangesAsync();
        }

        await WaitUntilAsync(async () => !await EventExistsAsync(claimedId));

        Assert.True(await EventExistsAsync(unclaimedId));
    }

    [Fact]
    public async Task An_old_terminal_delivery_is_deleted_with_its_attempts_an_in_flight_one_is_not()
    {
        var (_, projectId) = await SetupProjectAsync();
        var terminalId = Guid.NewGuid();
        var inFlightId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
            var subscription = new WebhookSubscription
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Name = "sub", Url = "https://example.test/hook",
                Secret = "shh", CreatedAt = Old, UpdatedAt = Old,
            };
            db.WebhookSubscriptions.Add(subscription);
            db.WebhookDeliveries.AddRange(
                new WebhookDelivery
                {
                    Id = terminalId, SubscriptionId = subscription.Id, ProjectId = projectId,
                    EventId = Guid.NewGuid(), EventType = "test.event", Status = "succeeded", CreatedAt = Old,
                },
                new WebhookDelivery
                {
                    // Still queued/delivering — must never be swept regardless of age.
                    Id = inFlightId, SubscriptionId = subscription.Id, ProjectId = projectId,
                    EventId = Guid.NewGuid(), EventType = "test.event", Status = "delivering", CreatedAt = Old,
                });
            db.WebhookDeliveryAttempts.Add(new WebhookDeliveryAttempt
            {
                Id = attemptId, DeliveryId = terminalId, AttemptNumber = 1, StartedAt = Old, StatusCode = 200,
            });
            await db.SaveChangesAsync();
        }

        await WaitUntilAsync(async () => !await DeliveryExistsAsync(terminalId));

        Assert.False(await AttemptExistsAsync(attemptId));
        Assert.True(await DeliveryExistsAsync(inFlightId));
    }

    [Fact]
    public async Task An_old_audit_log_row_is_deleted_a_recent_one_is_not()
    {
        var (_, projectId) = await SetupProjectAsync();
        var oldId = Guid.NewGuid();
        var recentId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
            db.AuditLog.AddRange(
                new AuditLogEntry
                {
                    Id = oldId, ProjectId = projectId, Actor = "system", Action = "test.old",
                    Resource = "test", CreatedAt = Old,
                },
                new AuditLogEntry
                {
                    Id = recentId, ProjectId = projectId, Actor = "system", Action = "test.recent",
                    Resource = "test", CreatedAt = Recent,
                });
            await db.SaveChangesAsync();
        }

        await WaitUntilAsync(async () => !await AuditLogExistsAsync(oldId));

        Assert.True(await AuditLogExistsAsync(recentId));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<bool> EventExistsAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        return await db.Events.AnyAsync(e => e.Id == id);
    }

    private async Task<bool> DeliveryExistsAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        return await db.WebhookDeliveries.AnyAsync(d => d.Id == id);
    }

    private async Task<bool> AttemptExistsAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        return await db.WebhookDeliveryAttempts.AnyAsync(a => a.Id == id);
    }

    private async Task<bool> AuditLogExistsAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        return await db.AuditLog.AnyAsync(a => a.Id == id);
    }

    /// <summary>Polls a condition until it's true or a generous deadline passes — the 1-second sweep interval above means a handful of iterations is normally enough.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(500);
        }
        throw new TimeoutException("Retention sweep did not delete the expected row in time.");
    }
}
