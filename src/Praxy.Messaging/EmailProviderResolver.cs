using Microsoft.EntityFrameworkCore;
using Praxy.Auth;
using Praxy.Persistence;

namespace Praxy.Messaging;

/// <summary>
/// Resolves the <see cref="IEmailSender"/> a project's email sends should go through: its enabled
/// default <c>email</c>-type <see cref="Praxy.Persistence.Entities.MessagingProvider"/> if it has
/// one, built via <see cref="SmtpEmailSender"/> — Phase 1's own class, reused verbatim, never
/// reimplemented — or the instance-wide singleton (<c>Praxy:Smtp:*</c>, or the dev
/// <see cref="LoggingEmailSender"/> fallback) otherwise. This is what makes per-project provider
/// configuration additive: a project that never visits the Providers screen keeps working exactly
/// as it did before Messaging existed.
/// </summary>
public sealed class EmailProviderResolver(PraxyDb db, InstanceKey key, IEmailSender fallback)
{
    public async Task<IEmailSender> ResolveAsync(string projectId, CancellationToken ct)
    {
        var provider = await db.MessagingProviders.AsNoTracking().FirstOrDefaultAsync(
            p => p.ProjectId == projectId && p.Type == "email" && p.Enabled && p.IsDefault, ct);
        if (provider is null)
            return fallback;

        var config = EmailProviderConfig.Parse(provider.Config);
        var password = provider.ProtectedSecret is null ? null : key.Decrypt(provider.ProtectedSecret);
        return new SmtpEmailSender(new SmtpOptions
        {
            Host = config.Host,
            Port = config.Port,
            Username = config.Username,
            Password = password,
            From = config.From,
            UseTls = config.UseTls,
        });
    }
}
