using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Messaging;

/// <summary>Console-facing CRUD for topics and their subscribers.</summary>
public sealed class MessagingTopicsService(PraxyDb db, MessagingTargetsService targets)
{
    public Task<List<(MessagingTopic Topic, int SubscriberCount)>> ListAsync(string projectId, CancellationToken ct) =>
        (from t in db.MessagingTopics
         where t.ProjectId == projectId
         orderby t.CreatedAt descending
         select new { t, Count = db.MessagingSubscribers.Count(s => s.TopicId == t.Id) })
        .ToListAsync(ct)
        .ContinueWith(task => task.Result.Select(x => (x.t, x.Count)).ToList(), ct);

    public async Task<MessagingTopic> GetAsync(string projectId, Guid id, CancellationToken ct) =>
        await db.MessagingTopics.FirstOrDefaultAsync(t => t.Id == id && t.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.MessagingTopicNotFound, "Topic not found.");

    public async Task<MessagingTopic> CreateAsync(
        string projectId, string key_, string name, string? description, CancellationToken ct)
    {
        var fields = Validate(key_, name);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid topic payload.", fields);

        var topic = new MessagingTopic
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Key = key_.Trim(),
            Name = name.Trim(),
            Description = description?.Trim() is { Length: > 0 } d ? d : null,
        };
        db.MessagingTopics.Add(topic);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new PraxyException(409, ErrorTypes.MessagingTopicAlreadyExists,
                $"A topic with key '{key_}' already exists in this project.");
        }
        return topic;
    }

    public async Task<MessagingTopic> UpdateAsync(
        MessagingTopic topic, string? name, string? description, CancellationToken ct)
    {
        var fields = Validate(topic.Key, name ?? topic.Name);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid topic payload.", fields);

        if (name is not null)
            topic.Name = name.Trim();
        if (description is not null)
            topic.Description = description.Trim() is { Length: > 0 } d ? d : null;
        topic.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return topic;
    }

    public async Task DeleteAsync(MessagingTopic topic, CancellationToken ct)
    {
        db.MessagingTopics.Remove(topic);
        await db.SaveChangesAsync(ct);
    }

    // ---- subscribers ------------------------------------------------------------------------------

    public Task<List<(MessagingSubscriber Subscriber, MessagingTarget Target)>> ListSubscribersAsync(
        Guid topicId, CancellationToken ct) =>
        (from s in db.MessagingSubscribers
         join t in db.MessagingTargets on s.TargetId equals t.Id
         where s.TopicId == topicId
         orderby s.CreatedAt descending
         select new { s, t })
        .ToListAsync(ct)
        .ContinueWith(task => task.Result.Select(x => (x.s, x.t)).ToList(), ct);

    public async Task<MessagingSubscriber> SubscribeAsync(
        string projectId, MessagingTopic topic, Guid userId, CancellationToken ct)
    {
        var target = await targets.GetOrCreateEmailTargetAsync(projectId, userId, ct);
        var subscriber = new MessagingSubscriber
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            TopicId = topic.Id,
            TargetId = target.Id,
        };
        db.MessagingSubscribers.Add(subscriber);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new PraxyException(409, ErrorTypes.MessagingSubscriberAlreadyExists,
                "This user is already subscribed to this topic.");
        }
        return subscriber;
    }

    public async Task UnsubscribeAsync(Guid topicId, Guid subscriberId, CancellationToken ct)
    {
        var deleted = await db.MessagingSubscribers
            .Where(s => s.Id == subscriberId && s.TopicId == topicId)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw PraxyException.NotFound(ErrorTypes.MessagingSubscriberNotFound, "Subscriber not found.");
    }

    private static Dictionary<string, string[]> Validate(string key_, string name)
    {
        var fields = new Dictionary<string, string[]>();
        if (!Ids.IsValidCustomId(key_))
            fields["key"] = ["1-36 chars, lowercase alphanumeric or hyphen, must start alphanumeric."];
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];
        return fields;
    }
}
