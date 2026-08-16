using Microsoft.EntityFrameworkCore;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Messaging;

public sealed record RenderedTemplate(string Subject, string Body, bool Overridden);

/// <summary>
/// The project-overridable template system behind "templates for the auth emails moved here": one
/// row per (project, channel, key) override; no row means the compiled-in <see cref="Defaults"/>
/// text applies, so every existing project keeps rendering its verification/recovery/invitation
/// emails exactly as before Messaging shipped, with no backfill migration needed — same "absent
/// means default" shape <c>ProjectAuthSettings</c> already uses for its own jsonb section.
/// </summary>
public sealed class MessagingTemplatesService(PraxyDb db)
{
    public const string EmailChannel = "email";

    /// <summary>The exact text <c>AppAuthService</c>/<c>TeamsService</c> sent inline before this phase, now parameterized.</summary>
    public static readonly IReadOnlyDictionary<string, (string Subject, string Body)> Defaults =
        new Dictionary<string, (string, string)>
        {
            [AuthEmailTemplateKeys.Verification] = (
                "Verify your email for {{project}}",
                "Follow this link to verify your email address:\n\n{{url}}\n\n" +
                "The link expires in {{expiryMinutes}} minutes. If you did not request this, you can ignore this message."),
            [AuthEmailTemplateKeys.Recovery] = (
                "Reset your password for {{project}}",
                "Follow this link to reset your password:\n\n{{url}}\n\n" +
                "The link expires in {{expiryMinutes}} minutes. If you did not request this, you can ignore this message."),
            [AuthEmailTemplateKeys.Invitation] = (
                "You have been invited to join {{teamName}}",
                "Follow this link to join the team \"{{teamName}}\" on {{project}}:\n\n{{url}}\n\n" +
                "If you did not expect this invitation, you can ignore this message."),
        };

    public async Task<List<(string Key, RenderedTemplate Template)>> ListAsync(string projectId, CancellationToken ct)
    {
        var overrides = await db.MessagingTemplates
            .Where(t => t.ProjectId == projectId && t.Channel == EmailChannel)
            .ToDictionaryAsync(t => t.Key, ct);

        return [.. AuthEmailTemplateKeys.All.Select(k => (k, Effective(k, overrides.GetValueOrDefault(k))))];
    }

    public async Task<RenderedTemplate> GetAsync(string projectId, string key, CancellationToken ct)
    {
        RequireKnownKey(key);
        var row = await db.MessagingTemplates.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.Channel == EmailChannel && t.Key == key, ct);
        return Effective(key, row);
    }

    public async Task<RenderedTemplate> SetAsync(string projectId, string key, string subject, string body, CancellationToken ct)
    {
        RequireKnownKey(key);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 998 || string.IsNullOrWhiteSpace(body) || body.Length > 65536)
            throw PraxyException.ArgumentInvalid("Invalid template payload.",
                new Dictionary<string, string[]>
                {
                    ["subject"] = ["Must be 1-998 characters."],
                    ["body"] = ["Must be 1-65536 characters."],
                });

        var row = await db.MessagingTemplates.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.Channel == EmailChannel && t.Key == key, ct);
        if (row is null)
        {
            row = new MessagingTemplate
            {
                Id = Ids.NewUuid(), ProjectId = projectId, Channel = EmailChannel, Key = key,
                Subject = subject, Body = body,
            };
            db.MessagingTemplates.Add(row);
        }
        else
        {
            row.Subject = subject;
            row.Body = body;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return new RenderedTemplate(subject, body, true);
    }

    public async Task<RenderedTemplate> ResetAsync(string projectId, string key, CancellationToken ct)
    {
        RequireKnownKey(key);
        await db.MessagingTemplates
            .Where(t => t.ProjectId == projectId && t.Channel == EmailChannel && t.Key == key)
            .ExecuteDeleteAsync(ct);
        return Effective(key, null);
    }

    /// <summary>Effective subject/body for <paramref name="key"/>, with <c>{{var}}</c> placeholders substituted.</summary>
    public async Task<(string Subject, string Body)> RenderAsync(
        string projectId, string key, IReadOnlyDictionary<string, string> vars, CancellationToken ct)
    {
        var effective = await GetAsync(projectId, key, ct);
        return (TemplateText.Substitute(effective.Subject, vars), TemplateText.Substitute(effective.Body, vars));
    }

    private static RenderedTemplate Effective(string key, MessagingTemplate? row)
    {
        if (row is not null)
            return new RenderedTemplate(row.Subject, row.Body, true);
        var (subject, body) = Defaults[key];
        return new RenderedTemplate(subject, body, false);
    }

    private static void RequireKnownKey(string key)
    {
        if (!AuthEmailTemplateKeys.All.Contains(key))
            throw new PraxyException(400, ErrorTypes.MessagingTemplateInvalid,
                $"Unknown template key '{key}'. Must be one of: {string.Join(", ", AuthEmailTemplateKeys.All)}.");
    }
}
