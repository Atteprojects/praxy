using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Messaging;

/// <summary>
/// Resolves an app user's deliverable address into a <see cref="MessagingTarget"/> row, creating it
/// on first use rather than backfilling one at signup — the roadmap's "a target is an app user's
/// email address" needs no participation from <c>Praxy.Auth</c> beyond reading
/// <see cref="Persistence.Entities.User.Email"/>.
/// </summary>
public sealed class MessagingTargetsService(PraxyDb db)
{
    public async Task<MessagingTarget> GetOrCreateEmailTargetAsync(string projectId, Guid userId, CancellationToken ct)
    {
        var existing = await db.MessagingTargets.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.UserId == userId && t.Type == "email", ct);
        if (existing is not null)
            return existing;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.ProjectId == projectId, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.UserNotFound, "User not found.");

        var target = new MessagingTarget
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            UserId = user.Id,
            Type = "email",
            Identifier = user.Email,
        };
        db.MessagingTargets.Add(target);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent get-or-create for the same (projectId, userId, type) —
            // the unique index means the other write won; read back its row instead of failing.
            db.Entry(target).State = EntityState.Detached;
            return await db.MessagingTargets.FirstAsync(
                t => t.ProjectId == projectId && t.UserId == userId && t.Type == "email", ct);
        }
        return target;
    }
}
