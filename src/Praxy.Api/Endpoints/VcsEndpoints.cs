using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence.Entities;
using Praxy.Sites;
using Praxy.Vcs;

namespace Praxy.Api.Endpoints;

public sealed record VcsInstallationResponse(string Id, long InstallationId, string AccountLogin, string AccountType, DateTimeOffset CreatedAt)
{
    public static VcsInstallationResponse From(VcsInstallation i) =>
        new(Ids.Wire(i.Id), i.InstallationId, i.AccountLogin, i.AccountType, i.CreatedAt);
}

public sealed record VcsInstallationListResponse(int Total, IReadOnlyList<VcsInstallationResponse> Installations);

public sealed record VcsInstallUrlResponse(string Url);

/// <summary>
/// Sites Phase 4's instance-wide GitHub App surface — owned by <c>Praxy.Vcs</c>, not
/// <c>Praxy.Sites</c>: console install-status endpoints, plus the two unauthenticated endpoints GitHub
/// itself calls directly (the App's installation-flow redirect target, and its webhook delivery
/// target). No <c>/sites/</c> prefix anywhere here — <see cref="SiteEndpoints"/> owns the Sites-specific
/// half of this phase (a site's own connect/disconnect/branches routes), which calls into
/// <c>Praxy.Vcs</c> the same way this file does rather than the other way around.
/// </summary>
public static class VcsEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var console = api.MapGroup("/v1/console/vcs/github").AddEndpointFilter<RequireOperatorFilter>();
        console.MapGet("/installations", ListInstallations).Produces<VcsInstallationListResponse>();
        console.MapGet("/install-url", GetInstallUrl).Produces<VcsInstallUrlResponse>();

        // Both public — GitHub calls these directly, no operator session exists on either request.
        api.MapGet("/v1/vcs/github/callback", InstallCallback).Produces(StatusCodes.Status302Found);
        api.MapPost("/v1/vcs/github/webhook", Webhook)
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> ListInstallations(GitHubAppService github, CancellationToken ct)
    {
        var list = await github.ListInstallationsAsync(ct);
        return Results.Ok(new VcsInstallationListResponse(list.Count, [.. list.Select(VcsInstallationResponse.From)]));
    }

    private static async Task<IResult> GetInstallUrl(GitHubAppService github, CancellationToken ct)
    {
        var url = await github.GetInstallUrlAsync(ct);
        return Results.Ok(new VcsInstallUrlResponse(url.ToString()));
    }

    /// <summary>
    /// The GitHub App's own "Setup URL" — GitHub redirects the operator's browser here once they
    /// finish the installation flow on github.com, carrying <c>installation_id</c> as a query param.
    /// No session, no <c>state</c> param to check: the only thing this can do is look up an
    /// installation id GitHub itself supplied and, only if GitHub's own JWT-authenticated API confirms
    /// it actually belongs to this App, upsert a row — there's no forgeable action beyond that to
    /// protect against.
    /// </summary>
    private static async Task<IResult> InstallCallback(HttpContext http, GitHubAppService github, CancellationToken ct)
    {
        var raw = http.Request.Query["installation_id"].FirstOrDefault();
        if (!long.TryParse(raw, out var installationId))
            return Results.Redirect("/?vcs=invalid");

        await github.HandleInstallCallbackAsync(installationId, ct);
        return Results.Redirect("/?vcs=connected");
    }

    /// <summary>
    /// GitHub's webhook delivery target. The raw body is read in full and verified against
    /// <c>X-Hub-Signature-256</c> before anything parses it as JSON — letting ASP.NET Core's model
    /// binding deserialize first would consume (and potentially re-encode) the stream before the HMAC
    /// could be computed against the exact bytes GitHub signed, the landmine
    /// docs/handoff/sites-phase-4-prompt.md calls out explicitly.
    /// </summary>
    private static async Task<IResult> Webhook(HttpContext http, VcsOptions options, SitesService sites, CancellationToken ct)
    {
        var raw = await ReadCappedAsync(http.Request.Body, options.MaxWebhookBodyBytes, ct);
        var signature = http.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!GitHubWebhookSignature.Verify(options.GitHub.WebhookSecret, raw, signature))
            throw new PraxyException(401, ErrorTypes.VcsWebhookInvalidSignature, "Invalid webhook signature.");

        // A signed delivery for an event type we don't act on (GitHub sends "ping" the moment the
        // webhook is configured, and could in principle be reconfigured to send others) — the
        // signature already proved authenticity, so this is a clean no-op, not an error.
        if (http.Request.Headers["X-GitHub-Event"].FirstOrDefault() != "push")
            return Results.NoContent();

        GitHubPushEvent evt;
        try
        {
            evt = GitHubPushEventParser.Parse(raw);
        }
        catch (GitHubPushPayloadException ex)
        {
            throw PraxyException.ArgumentInvalid(ex.Message);
        }

        // A push for a repository no connected site references is a no-op, not an error — HandleGitPushAsync's own query simply matches zero rows.
        await sites.HandleGitPushAsync(evt, ct);
        return Results.NoContent();
    }

    private static async Task<byte[]> ReadCappedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw PraxyException.ArgumentInvalid($"Webhook payload exceeds the {maxBytes / (1024 * 1024)}MB limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
