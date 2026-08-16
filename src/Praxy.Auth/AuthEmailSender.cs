namespace Praxy.Auth;

/// <summary>
/// The known auth-email template keys. Fixed at exactly these three — Praxy's own auth flows are
/// the only source of transactional email this phase, not an open set of operator-defined keys.
/// </summary>
public static class AuthEmailTemplateKeys
{
    public const string Verification = "verification";
    public const string Recovery = "recovery";
    public const string Invitation = "invitation";

    public static readonly IReadOnlyList<string> All = [Verification, Recovery, Invitation];
}

/// <summary>
/// Auth's seam into Phase 8's Messaging module: renders one of the fixed auth templates for
/// <paramref name="project"/> (falling back to a compiled-in default when the project has no
/// override) and delivers it. <c>Praxy.Messaging</c> implements this and is the DI registration —
/// <see cref="AppAuthService"/>/<see cref="TeamsService"/> depend only on this interface, never on
/// Praxy.Messaging directly, so the dependency arrow stays one-way (Messaging references Auth, not
/// the reverse).
/// </summary>
public interface IAuthEmailSender
{
    Task SendAsync(
        Praxy.Persistence.Entities.Project project, string templateKey, string to,
        IReadOnlyDictionary<string, string> vars, CancellationToken ct = default);
}
