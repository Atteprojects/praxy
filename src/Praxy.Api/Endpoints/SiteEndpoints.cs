using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Sites;
using Praxy.Tables.Quotas;
using Praxy.Vcs;

namespace Praxy.Api.Endpoints;

public sealed record CreateSiteRequest(string Key, string Name, string? RootDirectory);

public sealed record UpdateSiteRequest(string? Name, string? RootDirectory, bool? Enabled);

public sealed record SetSiteEnvVarRequest(string Value);

public sealed record CreateSiteDomainRequest(string Hostname);

public sealed record ConnectSiteGitRequest(string RepositoryFullName, string ProductionBranch);

public sealed record SiteResponse(
    string Id, string Key, string Name, string RootDirectory, bool Enabled, string? ActiveDeploymentId,
    bool IsRunning, string PublicUrl, string? RepositoryFullName, string? ProductionBranch,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static SiteResponse From(Site s, bool isRunning, string publicUrl) => new(
        Ids.Wire(s.Id), s.Key, s.Name, s.RootDirectory, s.Enabled,
        s.ActiveDeploymentId is { } d ? Ids.Wire(d) : null, isRunning, publicUrl,
        s.RepositoryFullName, s.ProductionBranch, s.CreatedAt, s.UpdatedAt);
}

public sealed record SiteEnvVarResponse(string Key, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static SiteEnvVarResponse From(SiteEnvVar v) => new(v.Key, v.CreatedAt, v.UpdatedAt);
}

public sealed record SiteDeploymentResponse(
    string Id, string Status, long SourceSizeBytes, string Source, string? CommitSha, string? CommitMessage,
    string? Branch, string BuildLog, string? Error, string? ImageTag, string? PreviewUrl,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ActivatedAt)
{
    public static SiteDeploymentResponse From(SiteDeployment d, string? previewUrl) => new(
        Ids.Wire(d.Id), d.Status, d.SourceSizeBytes, d.Source, d.CommitSha, d.CommitMessage, d.Branch,
        d.BuildLog, d.Error, d.ImageTag, previewUrl, d.CreatedAt, d.UpdatedAt, d.ActivatedAt);
}

public sealed record SiteDomainResponse(string Id, string Hostname, string Status, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt)
{
    public static SiteDomainResponse From(SiteDomain d) => new(Ids.Wire(d.Id), d.Hostname, d.Status, d.CreatedAt, d.VerifiedAt);
}

public sealed record SiteGitBranchesResponse(IReadOnlyList<string> Branches);

public sealed record SiteRequestResponse(
    string Id, string Method, string Path, int StatusCode, int DurationMs, DateTimeOffset CreatedAt)
{
    public static SiteRequestResponse From(SiteRequestLog r) =>
        new(Ids.Wire(r.Id), r.Method, r.Path, r.StatusCode, r.DurationMs, r.CreatedAt);
}

public sealed record SiteListResponse(int Total, IReadOnlyList<SiteResponse> Sites);
public sealed record SiteEnvVarListResponse(int Total, IReadOnlyList<SiteEnvVarResponse> Vars);
public sealed record SiteDeploymentListResponse(int Total, IReadOnlyList<SiteDeploymentResponse> Deployments);
public sealed record SiteDomainListResponse(int Total, IReadOnlyList<SiteDomainResponse> Domains);
public sealed record SiteRequestListResponse(int Total, IReadOnlyList<SiteRequestResponse> Requests);

/// <summary>
/// Sites Phase 1: console admin surface for deploying/configuring hosted Next.js sites, plus the
/// unauthenticated <c>_ask-tls</c> endpoint Caddy calls before minting each on-demand certificate.
/// No data-plane surface — unlike a Function, a site isn't invoked through the API, it's browsed
/// directly at its own subdomain (<see cref="Praxy.Sites.SiteProxyMiddleware"/> handles that, as an
/// early branch in Program.cs's pipeline, never touching this group's <c>/v1/console/...</c> routes).
/// </summary>
public static class SiteEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/v1/console/projects/{projectId}/sites")
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>();

        admin.MapGet("", ListSites).Produces<SiteListResponse>();
        admin.MapPost("", CreateSite).Produces<SiteResponse>(StatusCodes.Status201Created);
        admin.MapGet("/{siteId}", GetSite).Produces<SiteResponse>();
        admin.MapPatch("/{siteId}", UpdateSite).Produces<SiteResponse>();
        admin.MapDelete("/{siteId}", DeleteSite).Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/{siteId}/env", ListEnvVars).Produces<SiteEnvVarListResponse>();
        admin.MapPut("/{siteId}/env/{envKey}", SetEnvVar).Produces<SiteEnvVarResponse>();
        admin.MapDelete("/{siteId}/env/{envKey}", DeleteEnvVar).Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/{siteId}/domains", ListDomains).Produces<SiteDomainListResponse>();
        admin.MapPost("/{siteId}/domains", AddDomain).Produces<SiteDomainResponse>(StatusCodes.Status201Created);
        admin.MapDelete("/{siteId}/domains/{domainId}", DeleteDomain).Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/{siteId}/git/branches", ListGitBranches).Produces<SiteGitBranchesResponse>();
        admin.MapPost("/{siteId}/git", ConnectGit).Produces<SiteResponse>();
        admin.MapDelete("/{siteId}/git", DisconnectGit).Produces<SiteResponse>();

        admin.MapGet("/{siteId}/deployments", ListDeployments).Produces<SiteDeploymentListResponse>();
        admin.MapPost("/{siteId}/deployments", CreateDeployment).Produces<SiteDeploymentResponse>(StatusCodes.Status201Created);
        admin.MapPost("/{siteId}/deployments/from-starter-template", CreateDeploymentFromStarterTemplate)
            .Produces<SiteDeploymentResponse>(StatusCodes.Status201Created);
        admin.MapGet("/{siteId}/deployments/{deploymentId}", GetDeployment).Produces<SiteDeploymentResponse>();
        admin.MapPost("/{siteId}/deployments/{deploymentId}/activate", ActivateDeployment).Produces<SiteDeploymentResponse>();

        admin.MapGet("/{siteId}/requests", ListRequests).Produces<SiteRequestListResponse>();

        // Unauthenticated: Caddy's on_demand_tls "ask" directive calls this before every cert
        // issuance. A permissive implementation here turns the box into an open cert-minting oracle
        // for anyone who points DNS at it, so this is a strict allow-list, not a formality — see
        // AskTls's own remarks.
        api.MapGet("/v1/sites/_ask-tls", AskTls).Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
    }

    // ---- sites ----------------------------------------------------------------------------------

    private static async Task<IResult> ListSites(HttpContext http, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await sites.ListAsync(project.Id, ct);
        return Results.Ok(new SiteListResponse(
            list.Count, [.. list.Select(s => SiteResponse.From(s, sites.IsRunning(s), PublicUrl(s, options)))]));
    }

    private static async Task<IResult> CreateSite(
        CreateSiteRequest req, HttpContext http, PraxyDb db, SitesService sites, SitesOptions options,
        QuotaService quotas, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        await quotas.EnsureSiteQuotaAsync(project.Id, ct);
        var site = await sites.CreateAsync(project.Id, req.Key, req.Name, req.RootDirectory ?? "", ct);
        await AuditAsync(db, http, project.Id, "sites.create", $"site/{Ids.Wire(site.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/sites/{Ids.Wire(site.Id)}",
            SiteResponse.From(site, false, PublicUrl(site, options)));
    }

    private static async Task<IResult> GetSite(string siteId, HttpContext http, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        return Results.Ok(SiteResponse.From(site, sites.IsRunning(site), PublicUrl(site, options)));
    }

    private static async Task<IResult> UpdateSite(
        string siteId, UpdateSiteRequest req, HttpContext http, PraxyDb db, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var updated = await sites.UpdateAsync(site, req.Name, req.RootDirectory, req.Enabled, ct);
        await AuditAsync(db, http, project.Id, "sites.update", $"site/{siteId}", ct);
        return Results.Ok(SiteResponse.From(updated, sites.IsRunning(updated), PublicUrl(updated, options)));
    }

    private static async Task<IResult> DeleteSite(string siteId, HttpContext http, PraxyDb db, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        await sites.DeleteAsync(site, ct);
        await AuditAsync(db, http, project.Id, "sites.delete", $"site/{siteId}", ct);
        return Results.NoContent();
    }

    // ---- env vars -------------------------------------------------------------------------------

    private static async Task<IResult> ListEnvVars(string siteId, HttpContext http, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var list = await sites.ListEnvVarsAsync(site.Id, ct);
        return Results.Ok(new SiteEnvVarListResponse(list.Count, [.. list.Select(SiteEnvVarResponse.From)]));
    }

    private static async Task<IResult> SetEnvVar(
        string siteId, string envKey, SetSiteEnvVarRequest req, HttpContext http, PraxyDb db, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var v = await sites.SetEnvVarAsync(site.Id, envKey, req.Value, ct);
        await AuditAsync(db, http, project.Id, "sites.env.set", $"site/{siteId}/env/{envKey}", ct);
        return Results.Ok(SiteEnvVarResponse.From(v));
    }

    private static async Task<IResult> DeleteEnvVar(string siteId, string envKey, HttpContext http, PraxyDb db, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        await sites.DeleteEnvVarAsync(site.Id, envKey, ct);
        await AuditAsync(db, http, project.Id, "sites.env.delete", $"site/{siteId}/env/{envKey}", ct);
        return Results.NoContent();
    }

    // ---- custom domains (Sites Phase 3) ----------------------------------------------------------

    private static async Task<IResult> ListDomains(string siteId, HttpContext http, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var list = await sites.ListDomainsAsync(site.Id, ct);
        return Results.Ok(new SiteDomainListResponse(list.Count, [.. list.Select(SiteDomainResponse.From)]));
    }

    private static async Task<IResult> AddDomain(
        string siteId, CreateSiteDomainRequest req, HttpContext http, PraxyDb db, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var domain = await sites.AddDomainAsync(site, req.Hostname, ct);
        await AuditAsync(db, http, project.Id, "sites.domains.create", $"site/{siteId}/domain/{Ids.Wire(domain.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/sites/{siteId}/domains/{Ids.Wire(domain.Id)}",
            SiteDomainResponse.From(domain));
    }

    private static async Task<IResult> DeleteDomain(
        string siteId, string domainId, HttpContext http, PraxyDb db, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        if (!Ids.TryParseWire(domainId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.SiteDomainNotFound, "Custom domain not found.");
        await sites.DeleteDomainAsync(site.Id, parsed, ct);
        await AuditAsync(db, http, project.Id, "sites.domains.delete", $"site/{siteId}/domain/{domainId}", ct);
        return Results.NoContent();
    }

    // ---- git repository (Sites Phase 4) ------------------------------------------------------

    private static async Task<IResult> ListGitBranches(
        string siteId, string repository, HttpContext http, SitesService sites, GitHubAppService github, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        await FindAsync(sites, project.Id, siteId, ct);
        var branches = await github.ListBranchesForRepositoryAsync(repository, ct);
        return Results.Ok(new SiteGitBranchesResponse(branches));
    }

    private static async Task<IResult> ConnectGit(
        string siteId, ConnectSiteGitRequest req, HttpContext http, PraxyDb db, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var connected = await sites.ConnectRepositoryAsync(site, req.RepositoryFullName, req.ProductionBranch, ct);
        await AuditAsync(db, http, project.Id, "sites.git.connect", $"site/{siteId}", ct);
        return Results.Ok(SiteResponse.From(connected, sites.IsRunning(connected), PublicUrl(connected, options)));
    }

    private static async Task<IResult> DisconnectGit(
        string siteId, HttpContext http, PraxyDb db, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var disconnected = await sites.DisconnectRepositoryAsync(site, ct);
        await AuditAsync(db, http, project.Id, "sites.git.disconnect", $"site/{siteId}", ct);
        return Results.Ok(SiteResponse.From(disconnected, sites.IsRunning(disconnected), PublicUrl(disconnected, options)));
    }

    // ---- deployments ----------------------------------------------------------------------------

    private static async Task<IResult> ListDeployments(
        string siteId, HttpContext http, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var list = await sites.ListDeploymentsAsync(site.Id, ct);
        return Results.Ok(new SiteDeploymentListResponse(
            list.Count, [.. list.Select(d => SiteDeploymentResponse.From(d, PreviewUrl(site, d, options)))]));
    }

    private static async Task<IResult> CreateDeployment(
        string siteId, HttpContext http, PraxyDb db, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var tar = await ReadCappedAsync(http.Request.Body, options.MaxSourceBytes, ct);
        var deployment = await sites.CreateDeploymentAsync(site, tar, ct);
        await AuditAsync(db, http, project.Id, "sites.deployments.create", $"site/{siteId}/deployment/{Ids.Wire(deployment.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/sites/{siteId}/deployments/{Ids.Wire(deployment.Id)}",
            SiteDeploymentResponse.From(deployment, PreviewUrl(site, deployment, options)));
    }

    /// <summary>
    /// Deploys the bundled Next.js starter template (<see cref="SiteStarterTemplate"/>) as this
    /// site's first deployment — lets a brand-new user see a real, working site with one click
    /// instead of needing their own Next.js app ready first. Goes through the exact same
    /// build/activate pipeline as a real upload; the only difference is where the tar bytes come
    /// from.
    /// </summary>
    private static async Task<IResult> CreateDeploymentFromStarterTemplate(
        string siteId, HttpContext http, PraxyDb db, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var tar = await SiteStarterTemplate.BuildTarAsync(ct);
        var deployment = await sites.CreateDeploymentAsync(site, tar, ct);
        await AuditAsync(db, http, project.Id, "sites.deployments.create", $"site/{siteId}/deployment/{Ids.Wire(deployment.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/sites/{siteId}/deployments/{Ids.Wire(deployment.Id)}",
            SiteDeploymentResponse.From(deployment, PreviewUrl(site, deployment, options)));
    }

    private static async Task<IResult> GetDeployment(
        string siteId, string deploymentId, HttpContext http, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var deployment = await FindDeploymentAsync(sites, site.Id, deploymentId, ct);
        return Results.Ok(SiteDeploymentResponse.From(deployment, PreviewUrl(site, deployment, options)));
    }

    private static async Task<IResult> ActivateDeployment(
        string siteId, string deploymentId, HttpContext http, PraxyDb db, SitesService sites, SitesOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var deployment = await FindDeploymentAsync(sites, site.Id, deploymentId, ct);
        var activated = await sites.ActivateAsync(site, deployment, ct);
        await AuditAsync(db, http, project.Id, "sites.deployments.activate", $"site/{siteId}/deployment/{deploymentId}", ct);
        return Results.Ok(SiteDeploymentResponse.From(activated, PreviewUrl(site, activated, options)));
    }

    // ---- request logs (docs/handoff/sites-request-logs-prompt.md) -------------------------------

    private static async Task<IResult> ListRequests(
        string siteId, HttpContext http, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var (limit, offset) = ListParams(http);
        var (total, page) = await sites.ListRequestsAsync(site.Id, limit, offset, ct);
        return Results.Ok(new SiteRequestListResponse(total, [.. page.Select(SiteRequestResponse.From)]));
    }

    // ---- ask-tls --------------------------------------------------------------------------------

    /// <summary>
    /// Caddy calls <c>GET /v1/sites/_ask-tls?domain=&lt;host&gt;</c> before issuing each on-demand
    /// cert, treating any 2xx as authorization to proceed. Returns <c>204</c> (no body — there is
    /// nothing to say beyond the status) for a hostname that parses as either the production shape
    /// (<c>&lt;key&gt;.&lt;projectId&gt;.{Domain}</c>, requiring the site's <em>active</em> deployment
    /// to be <c>ready</c>) or a preview shape (<c>&lt;deploymentId&gt;.&lt;key&gt;.&lt;projectId&gt;.{Domain}</c>,
    /// Phase 2 — requiring only that specific deployment to belong to the site and be <c>ready</c>,
    /// active or not) AND resolves to a real, enabled site — anything else is <c>404</c>. All the
    /// checks matter: skipping the enabled/ready check would let an operator who disabled a site (or
    /// a deployment that never finished building) still have Caddy mint it a public cert; skipping
    /// the DB lookup entirely (accepting any hostname merely shaped like either pattern) turns this
    /// into an open oracle for anyone who points DNS at the box, which can also burn through Let's
    /// Encrypt's rate limits. Deliberately does not distinguish its failure reasons in the response —
    /// a 404 here must not become a way to enumerate which site keys, deployment ids, or registered
    /// custom domains exist.
    ///
    /// A hostname that doesn't parse against <see cref="SiteHostPattern"/> at all falls through to a
    /// <see cref="SiteCustomDomainLookup"/> exact match (Sites Phase 3) before giving up — this is
    /// the more security-sensitive half of that phase: it's the only thing standing between the box
    /// and answering an on-demand-TLS "ask" for <em>any</em> hostname an attacker points DNS at, not
    /// just within the built-in wildcard suffix, so it gets exactly the same enabled + ready-active-
    /// deployment strictness as the built-in production path, no preview-URL equivalent.
    /// </summary>
    private static async Task<IResult> AskTls(HttpContext http, PraxyDb db, SitesOptions options, CancellationToken ct)
    {
        var domain = http.Request.Query["domain"].FirstOrDefault();
        if (string.IsNullOrEmpty(domain))
            return Results.NotFound();

        if (!SiteHostPattern.TryParse(domain, options.Domain, out var key, out var projectId, out var deploymentRef))
        {
            var customSite = await SiteCustomDomainLookup.ResolveEnabledSiteAsync(db, domain, ct);
            return customSite is not null && await HasReadyActiveDeploymentAsync(db, customSite, ct)
                ? Results.NoContent() : Results.NotFound();
        }

        var site = await db.Sites.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Key == key && s.Enabled, ct);
        if (site is null)
            return Results.NotFound();

        bool deployable;
        if (deploymentRef is null)
        {
            deployable = await HasReadyActiveDeploymentAsync(db, site, ct);
        }
        else
        {
            deployable = Ids.TryParseWire(deploymentRef, out var deploymentId)
                && await db.SiteDeployments.AsNoTracking()
                    .AnyAsync(d => d.Id == deploymentId && d.SiteId == site.Id && d.Status == "ready", ct);
        }

        return deployable ? Results.NoContent() : Results.NotFound();
    }

    private static Task<bool> HasReadyActiveDeploymentAsync(PraxyDb db, Site site, CancellationToken ct) =>
        site.ActiveDeploymentId is { } activeId
            ? db.SiteDeployments.AsNoTracking().AnyAsync(d => d.Id == activeId && d.Status == "ready", ct)
            : Task.FromResult(false);

    // ---- helpers --------------------------------------------------------------------------------

    private static string PublicUrl(Site site, SitesOptions options) =>
        SiteUrl(site.Key, site.ProjectId, options);

    /// <summary>A deployment's preview URL — only meaningful once it's built (<c>ready</c>); null otherwise, matching "every ready deployment gets its own reachable URL" (Sites Phase 2).</summary>
    private static string? PreviewUrl(Site site, SiteDeployment deployment, SitesOptions options) =>
        deployment.Status == "ready" ? SiteUrl($"{Ids.Wire(deployment.Id)}.{site.Key}", site.ProjectId, options) : null;

    private static string SiteUrl(string hostPrefix, string projectId, SitesOptions options)
    {
        var scheme = options.Domain.EndsWith("localhost", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        return $"{scheme}://{hostPrefix}.{projectId}.{options.Domain}";
    }

    private static async Task<Site> FindAsync(SitesService sites, string projectId, string siteId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(siteId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.SiteNotFound, "Site not found.");
        return await sites.GetAsync(projectId, parsed, ct);
    }

    /// <summary>Same shape as FunctionEndpoints' own private helper — kept local rather than shared, matching how small this logic is.</summary>
    private static (int Limit, int Offset) ListParams(HttpContext http)
    {
        var limit = int.TryParse(http.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 100) : 25;
        var offset = int.TryParse(http.Request.Query["offset"], out var o) ? Math.Max(0, o) : 0;
        return (limit, offset);
    }

    private static async Task<SiteDeployment> FindDeploymentAsync(SitesService sites, Guid siteId, string deploymentId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(deploymentId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.SiteDeploymentNotFound, "Deployment not found.");
        return await sites.GetDeploymentAsync(siteId, parsed, ct);
    }

    private static async Task<byte[]> ReadCappedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new PraxyException(400, ErrorTypes.SiteInvalidSource,
                    $"Upload exceeds the {maxBytes / (1024 * 1024)}MB limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task AuditAsync(PraxyDb db, HttpContext http, string projectId, string action, string resource, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Actor = $"admin:{op.Account.Id}",
            Action = action,
            Resource = resource,
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }
}
