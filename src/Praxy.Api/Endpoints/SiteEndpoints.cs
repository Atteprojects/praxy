using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Sites;
using Praxy.Tables.Quotas;

namespace Praxy.Api.Endpoints;

public sealed record CreateSiteRequest(string Key, string Name, string? RootDirectory);

public sealed record UpdateSiteRequest(string? Name, string? RootDirectory, bool? Enabled);

public sealed record SetSiteEnvVarRequest(string Value);

public sealed record SiteResponse(
    string Id, string Key, string Name, string RootDirectory, bool Enabled, string? ActiveDeploymentId,
    bool IsRunning, string PublicUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static SiteResponse From(Site s, bool isRunning, string publicUrl) => new(
        Ids.Wire(s.Id), s.Key, s.Name, s.RootDirectory, s.Enabled,
        s.ActiveDeploymentId is { } d ? Ids.Wire(d) : null, isRunning, publicUrl, s.CreatedAt, s.UpdatedAt);
}

public sealed record SiteEnvVarResponse(string Key, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static SiteEnvVarResponse From(SiteEnvVar v) => new(v.Key, v.CreatedAt, v.UpdatedAt);
}

public sealed record SiteDeploymentResponse(
    string Id, string Status, long SourceSizeBytes, string BuildLog, string? Error, string? ImageTag,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ActivatedAt)
{
    public static SiteDeploymentResponse From(SiteDeployment d) => new(
        Ids.Wire(d.Id), d.Status, d.SourceSizeBytes, d.BuildLog, d.Error, d.ImageTag, d.CreatedAt, d.UpdatedAt, d.ActivatedAt);
}

public sealed record SiteListResponse(int Total, IReadOnlyList<SiteResponse> Sites);
public sealed record SiteEnvVarListResponse(int Total, IReadOnlyList<SiteEnvVarResponse> Vars);
public sealed record SiteDeploymentListResponse(int Total, IReadOnlyList<SiteDeploymentResponse> Deployments);

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

        admin.MapGet("/{siteId}/deployments", ListDeployments).Produces<SiteDeploymentListResponse>();
        admin.MapPost("/{siteId}/deployments", CreateDeployment).Produces<SiteDeploymentResponse>(StatusCodes.Status201Created);
        admin.MapGet("/{siteId}/deployments/{deploymentId}", GetDeployment).Produces<SiteDeploymentResponse>();
        admin.MapPost("/{siteId}/deployments/{deploymentId}/activate", ActivateDeployment).Produces<SiteDeploymentResponse>();

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

    // ---- deployments ----------------------------------------------------------------------------

    private static async Task<IResult> ListDeployments(string siteId, HttpContext http, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var list = await sites.ListDeploymentsAsync(site.Id, ct);
        return Results.Ok(new SiteDeploymentListResponse(list.Count, [.. list.Select(SiteDeploymentResponse.From)]));
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
            SiteDeploymentResponse.From(deployment));
    }

    private static async Task<IResult> GetDeployment(string siteId, string deploymentId, HttpContext http, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var deployment = await FindDeploymentAsync(sites, site.Id, deploymentId, ct);
        return Results.Ok(SiteDeploymentResponse.From(deployment));
    }

    private static async Task<IResult> ActivateDeployment(
        string siteId, string deploymentId, HttpContext http, PraxyDb db, SitesService sites, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var site = await FindAsync(sites, project.Id, siteId, ct);
        var deployment = await FindDeploymentAsync(sites, site.Id, deploymentId, ct);
        var activated = await sites.ActivateAsync(site, deployment, ct);
        await AuditAsync(db, http, project.Id, "sites.deployments.activate", $"site/{siteId}/deployment/{deploymentId}", ct);
        return Results.Ok(SiteDeploymentResponse.From(activated));
    }

    // ---- ask-tls --------------------------------------------------------------------------------

    /// <summary>
    /// Caddy calls <c>GET /v1/sites/_ask-tls?domain=&lt;host&gt;</c> before issuing each on-demand
    /// cert, treating any 2xx as authorization to proceed. Returns <c>204</c> (no body — there is
    /// nothing to say beyond the status) only for a hostname that parses as
    /// <c>&lt;key&gt;.&lt;projectId&gt;.{Domain}</c> AND resolves to a real, enabled site AND that
    /// site has an active <c>ready</c> deployment — anything else is <c>404</c>. All three checks
    /// matter: skipping the enabled/deployed check would let an operator who disabled a site (or
    /// never finished deploying it) still have Caddy mint it a public cert; skipping the DB lookup
    /// entirely (accepting any hostname merely shaped like the pattern) turns this into an open
    /// oracle for anyone who points DNS at the box, which can also burn through Let's Encrypt's rate
    /// limits. Deliberately does not distinguish its failure reasons in the response — a 404 here
    /// must not become a way to enumerate which site keys exist.
    /// </summary>
    private static async Task<IResult> AskTls(HttpContext http, PraxyDb db, SitesOptions options, CancellationToken ct)
    {
        var domain = http.Request.Query["domain"].FirstOrDefault();
        if (string.IsNullOrEmpty(domain) || !SiteHostPattern.TryParse(domain, options.Domain, out var key, out var projectId))
            return Results.NotFound();

        var deployable = await db.Sites.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.Key == key && s.Enabled && s.ActiveDeploymentId != null)
            .Join(db.SiteDeployments.AsNoTracking(), s => s.ActiveDeploymentId, d => d.Id, (s, d) => d.Status)
            .AnyAsync(status => status == "ready", ct);

        return deployable ? Results.NoContent() : Results.NotFound();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string PublicUrl(Site site, SitesOptions options)
    {
        var scheme = options.Domain.EndsWith("localhost", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        return $"{scheme}://{site.Key}.{site.ProjectId}.{options.Domain}";
    }

    private static async Task<Site> FindAsync(SitesService sites, string projectId, string siteId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(siteId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.SiteNotFound, "Site not found.");
        return await sites.GetAsync(projectId, parsed, ct);
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
