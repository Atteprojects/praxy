using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Tables.Quotas;
using Yarp.ReverseProxy.Forwarder;

namespace Praxy.Sites;

/// <summary>
/// Activates only on requests whose <c>Host</c> matches <c>&lt;key&gt;.&lt;projectId&gt;.{Domain}</c>
/// (production) or <c>&lt;deploymentRef&gt;.&lt;key&gt;.&lt;projectId&gt;.{Domain}</c> (a single
/// deployment's preview, Phase 2) and, when it does, owns the response entirely — everything else (the
/// console, <c>/v1</c>) falls through to <c>next</c> untouched, per the landmine in
/// docs/handoff/sites-phase-1-prompt.md about not shadowing real routes. Not Functions' JSON-envelope
/// <c>DockerExecutor.InvokeAsync</c> model — this streams (headers/body/chunked responses, binary,
/// WebSocket upgrade) via YARP's direct <see cref="IHttpForwarder"/> API, resolving the destination
/// itself (site key/project → deployment's cached container address) rather than YARP's own
/// route/cluster config, since that resolution is a live DB + in-memory-registry lookup Praxy already
/// owns.
///
/// The production path is unchanged from Phase 1: it only ever reads <see cref="SiteContainerRegistry"/>
/// and 404s if there's no entry — <see cref="SiteReconciler"/> and <see cref="SitesService.ActivateAsync"/>
/// exclusively own starting/stopping that container, and this middleware must never race them. The
/// preview path is new: on a cold (`no registry entry yet`) preview of a `ready` deployment, it starts
/// the container itself, bounded by <see cref="SitesOptions.StartupTimeoutSeconds"/> — a genuinely new
/// pattern for this codebase (starting Docker containers synchronously inside an HTTP request handler,
/// everywhere else that happens in a <c>BackgroundService</c> off the request path), so it's guarded
/// tightly: a per-deployment start lock (<see cref="SiteContainerRegistry.StartOrJoinAsync"/>), a quota
/// on concurrent preview containers per project, and a clear error (not a hang) on a slow or failed
/// cold start.
/// </summary>
public sealed class SiteProxyMiddleware(RequestDelegate next, ILogger<SiteProxyMiddleware> logger)
{
    // A dedicated invoker, not HttpClient — HttpClient buffers responses by default, which breaks
    // streaming and inflates memory/latency for exactly the traffic (Next.js SSR, RSC streaming)
    // this middleware exists to carry. Long-lived and shared: reusing one invoker across requests to
    // the same or different destinations lets the pooled-connection reuse YARP is built around
    // actually happen.
    private static readonly HttpMessageInvoker HttpClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    });

    private static readonly ForwarderRequestConfig RequestConfig = new() { ActivityTimeout = TimeSpan.FromSeconds(100) };

    public async Task InvokeAsync(
        HttpContext ctx, PraxyDb db, SitesOptions options, SiteContainerRegistry registry,
        SiteDockerExecutor docker, SitesService sites, QuotaService quotas, IHttpForwarder forwarder)
    {
        var host = ctx.Request.Host.Host;
        if (!SiteHostPattern.TryParse(host, options.Domain, out var key, out var projectId, out var deploymentRef))
        {
            await next(ctx);
            return;
        }

        var site = await db.Sites.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Key == key, ctx.RequestAborted);

        RunningSiteContainer running;
        if (deploymentRef is null)
        {
            // Production path — unchanged from Phase 1.
            if (site is null || !site.Enabled || site.ActiveDeploymentId is not { } activeId
                || !registry.TryGet(activeId, out running!))
            {
                await WriteNotDeployedAsync(ctx, "This site is not currently deployed.");
                return;
            }
        }
        else
        {
            if (site is null || !site.Enabled || !Ids.TryParseWire(deploymentRef, out var deploymentId))
            {
                await WriteNotDeployedAsync(ctx, "This preview is not available.");
                return;
            }

            if (!registry.TryGet(deploymentId, out running!))
            {
                var deployment = await db.SiteDeployments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == deploymentId && d.SiteId == site.Id, ctx.RequestAborted);
                if (deployment is null || deployment.Status != "ready" || deployment.ImageTag is null)
                {
                    await WriteNotDeployedAsync(ctx, "This preview is not available.");
                    return;
                }

                try
                {
                    await quotas.EnsurePreviewQuotaAsync(
                        site.ProjectId, registry.TrackedDeploymentIds(), ctx.RequestAborted);
                }
                catch (PraxyException ex)
                {
                    ctx.Response.StatusCode = ex.Code;
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.WriteAsync(ex.Message, ctx.RequestAborted);
                    return;
                }

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token);
                try
                {
                    running = await registry.StartOrJoinAsync(deploymentId, async startCt =>
                    {
                        var envVars = await sites.DecryptedEnvVarsAsync(site.Id, startCt);
                        return await docker.StartContainerAsync(deployment.ImageTag!, envVars, deployment.Id.ToString(), startCt);
                    }, linked.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ctx.RequestAborted.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Cold start failed for preview deployment {DeploymentId} ({Host})", deploymentId, host);
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.WriteAsync(
                        "This preview is starting up and did not become ready in time. Try again shortly.",
                        ctx.RequestAborted);
                    return;
                }
            }
        }

        var destinationPrefix = $"http://{running.Host}:{running.Port}";
        var error = await forwarder.SendAsync(ctx, destinationPrefix, HttpClient, RequestConfig);
        if (error != ForwarderError.None)
        {
            var errorFeature = ctx.GetForwarderErrorFeature();
            logger.LogWarning(errorFeature?.Exception, "Site proxy forwarding error {Error} for {Host}", error, host);
        }
    }

    private static async Task WriteNotDeployedAsync(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync(message, ctx.RequestAborted);
    }
}
