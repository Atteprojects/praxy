using Praxy.Auth;
using Praxy.Persistence.Entities;

namespace Praxy.Messaging;

/// <summary>
/// The DI-registered <see cref="IAuthEmailSender"/>: renders the project's effective template for
/// <paramref name="templateKey"/> and delivers it through <see cref="EmailProviderResolver"/>. This
/// is a direct, untracked send — verification/recovery/invitation emails are not <see cref="Message"/>
/// rows, the same way Appwrite keeps its auth "Templates" screen separate from its "Messaging"
/// campaigns: they are transactional, not something the console's per-message delivery view needs
/// to list.
/// </summary>
public sealed class AuthEmailBridge(MessagingTemplatesService templates, EmailProviderResolver resolver) : IAuthEmailSender
{
    public async Task SendAsync(
        Project project, string templateKey, string to, IReadOnlyDictionary<string, string> vars,
        CancellationToken ct = default)
    {
        var (subject, body) = await templates.RenderAsync(project.Id, templateKey, vars, ct);
        var sender = await resolver.ResolveAsync(project.Id, ct);
        await sender.SendAsync(new EmailMessage(to, subject, body), ct);
    }
}
