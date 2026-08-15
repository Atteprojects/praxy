using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;

namespace Praxy.Auth;

public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>
/// Outbound email seam for verification, recovery, and team invites. A full Messaging module
/// arrives in Phase 8; this is deliberately the minimum.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "praxy@localhost";
    public bool UseTls { get; set; } = true;

    public bool Configured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>Plain SMTP via the framework client — host/port/user/pass/from, nothing more.</summary>
public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        using var client = new SmtpClient(options.Host!, options.Port)
        {
            EnableSsl = options.UseTls,
            Credentials = string.IsNullOrEmpty(options.Username)
                ? null
                : new NetworkCredential(options.Username, options.Password),
        };
        using var mail = new MailMessage(options.From, message.To, message.Subject, message.TextBody);
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>
/// Dev fallback when SMTP is unconfigured: the full message lands in the server log so
/// verification and invite links stay copy-pasteable instead of silently vanishing.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        logger.LogWarning(
            "SMTP is not configured (Praxy:Smtp:Host) — email NOT sent. To: {To} | Subject: {Subject} | Body: {Body}",
            message.To, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }
}
