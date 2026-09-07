namespace Praxy.Auth.OAuth;

/// <summary>
/// The instance's own Google OAuth client, for console operator sign-in. Deliberately separate
/// from <see cref="ProjectAuthSettings"/> (a developer project's own Google credentials, for that
/// project's app users) — operators have no project to hang credentials off of, so this is
/// instance-wide configuration instead, same plain-record shape as
/// <c>Praxy.Vcs.GitHubAppOptions</c>. Bound from <c>Praxy:ConsoleAuth:Google:*</c> in Program.cs.
/// Unset means the feature is off (a clean, typed error, not a crash).
/// </summary>
public sealed record ConsoleOAuthOptions(string GoogleClientId, string GoogleClientSecret)
{
    public bool GoogleConfigured =>
        !string.IsNullOrWhiteSpace(GoogleClientId) && !string.IsNullOrWhiteSpace(GoogleClientSecret);
}
