using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Messaging;

/// <summary>
/// Console-facing compose + list/detail for messages. Sending is operator-triggered, not a
/// reaction to <c>praxy.events</c> — this resolves the target set and writes every
/// <see cref="MessageTarget"/> row up front, in the same transaction as the <see cref="Message"/>
/// itself, then <see cref="MessageSendWorker"/> claims and delivers each one; it never routes
/// through the outbox the way row-event consumers (webhooks, function triggers) do.
/// </summary>
public sealed class MessagesService(PraxyDb db, MessagingTargetsService targets, MessageSendSignal signal, MessagingOptions options)
{
    public Task<(int Total, List<Message> Page)> ListAsync(string projectId, int limit, int offset, CancellationToken ct) =>
        PageAsync(db.Messages.Where(m => m.ProjectId == projectId).OrderByDescending(m => m.CreatedAt), limit, offset, ct);

    public async Task<Message> GetAsync(string projectId, Guid id, CancellationToken ct) =>
        await db.Messages.FirstOrDefaultAsync(m => m.Id == id && m.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.MessagingMessageNotFound, "Message not found.");

    public Task<List<MessageTarget>> ListTargetsAsync(Guid messageId, CancellationToken ct) =>
        db.MessageTargets.Where(t => t.MessageId == messageId).OrderBy(t => t.Identifier).ToListAsync(ct);

    public async Task<Message> CreateAsync(
        string projectId, string subject, string body, Guid[] topicIds, Guid[] userIds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > options.MaxSubjectLength)
            throw PraxyException.ArgumentInvalid("Invalid message payload.",
                new Dictionary<string, string[]> { ["subject"] = [$"Must be 1-{options.MaxSubjectLength} characters."] });
        if (string.IsNullOrWhiteSpace(body) || body.Length > options.MaxBodyLength)
            throw PraxyException.ArgumentInvalid("Invalid message payload.",
                new Dictionary<string, string[]> { ["body"] = [$"Must be 1-{options.MaxBodyLength} characters."] });
        if (topicIds.Length == 0 && userIds.Length == 0)
            throw new PraxyException(400, ErrorTypes.MessagingMessageInvalid,
                "Provide at least one topic or user to send to.");

        // Topic subscribers' targets ∪ an explicit target per user, deduplicated by target id so a
        // user reachable both ways (subscribed *and* named explicitly) gets exactly one delivery.
        var resolved = new Dictionary<Guid, MessagingTarget>();
        if (topicIds.Length > 0)
        {
            var subscriberTargets = await (
                from s in db.MessagingSubscribers
                join t in db.MessagingTargets on s.TargetId equals t.Id
                where s.ProjectId == projectId && topicIds.Contains(s.TopicId)
                select t).ToListAsync(ct);
            foreach (var t in subscriberTargets)
                resolved[t.Id] = t;
        }
        foreach (var userId in userIds)
        {
            var target = await targets.GetOrCreateEmailTargetAsync(projectId, userId, ct);
            resolved[target.Id] = target;
        }

        if (resolved.Count == 0)
            throw new PraxyException(400, ErrorTypes.MessagingMessageInvalid,
                "No subscribers or users resolved to a deliverable target.");
        if (resolved.Count > options.MaxTargetsPerMessage)
            throw new PraxyException(400, ErrorTypes.GeneralResourceLimitExceeded,
                $"This message would send to {resolved.Count} targets, over the {options.MaxTargetsPerMessage} limit.");

        var message = new Message
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Type = "email",
            Subject = subject.Trim(),
            Body = body,
            TopicIds = topicIds,
            UserIds = userIds,
        };
        db.Messages.Add(message);
        foreach (var target in resolved.Values)
        {
            db.MessageTargets.Add(new MessageTarget
            {
                Id = Ids.NewUuid(),
                MessageId = message.Id,
                ProjectId = projectId,
                TargetId = target.Id,
                Identifier = target.Identifier,
            });
        }
        await db.SaveChangesAsync(ct);
        signal.Notify();
        return message;
    }

    private static async Task<(int, List<Message>)> PageAsync(
        IQueryable<Message> query, int limit, int offset, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var page = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return (total, page);
    }
}
