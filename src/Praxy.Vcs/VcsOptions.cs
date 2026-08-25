namespace Praxy.Vcs;

/// <summary>
/// The instance's own GitHub App registration — every self-hosted Praxy instance creates and owns its
/// own App, mirroring Appwrite's own self-host story (there is no shared Praxy-provided App). Bound
/// from <c>Praxy:Vcs:GitHub:*</c> config in Program.cs, same plain-record shape as
/// <c>Praxy.Sites.SitesOptions</c>. Startup-only, not console-rotatable — CLAUDE.md's "every limit is
/// configurable" cross-phase rule is about runtime knobs, not credentials, and there's no concrete
/// need yet to let an operator swap the App's private key without a restart.
/// </summary>
public sealed record GitHubAppOptions(
    string AppId, string ClientId, string ClientSecret, string PrivateKey, string WebhookSecret);

public sealed record VcsOptions(
    GitHubAppOptions GitHub,
    /// <summary>Hard ceiling on a single `git` subprocess call during a clone — a runaway or hung fetch must not wedge the build worker forever.</summary>
    int CloneTimeoutSeconds = 60,
    /// <summary>Caps how much of an inbound webhook request body the endpoint will buffer before verifying its signature — the endpoint is unauthenticated by nature, so this is what stands between it and an unbounded-memory POST. GitHub's own documented payload cap is 25MB.</summary>
    long MaxWebhookBodyBytes = 25_000_000)
{
    /// <summary>
    /// <see cref="GitHubAppOptions.PrivateKey"/> as configured may be the raw multi-line PEM, or (the
    /// documented, recommended path in docs/self-host.md — real newlines don't survive a single-line
    /// `.env` value cleanly) base64 of the PEM text. PEM text always contains '-' characters that
    /// aren't in the standard base64 alphabet, so a base64 decode attempt fails harmlessly on raw PEM
    /// and this just falls through to using it as-is.
    /// </summary>
    public static string DecodePrivateKey(string configured)
    {
        var trimmed = configured.Trim();
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
        }
        catch (FormatException)
        {
            return trimmed;
        }
    }
}
