using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Praxy.Persistence;
using Yarp.ReverseProxy.Forwarder;

namespace Praxy.Sites;

/// <summary>
/// Activates only on requests whose <c>Host</c> matches <c>&lt;key&gt;.&lt;projectId&gt;.{Domain}</c>
/// and, when it does, owns the response entirely — everything else (the console, <c>/v1</c>) falls
/// through to <c>next</c> untouched, per the landmine in docs/handoff/sites-phase-1-prompt.md about
/// not shadowing real routes. Not Functions' JSON-envelope <c>DockerExecutor.InvokeAsync</c> model —
/// this streams (headers/body/chunked responses, binary, WebSocket upgrade) via YARP's direct
/// <see cref="IHttpForwarder"/> API, resolving the destination itself (site key/project → active
/// deployment's cached container address) rather than YARP's own route/cluster config, since that
/// resolution is a live DB + in-memory-registry lookup Praxy already owns.
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

    public async Task InvokeAsync(HttpContext ctx, PraxyDb db, SitesOptions options, SiteContainerRegistry registry, IHttpForwarder forwarder)
    {
        var host = ctx.Request.Host.Host;
        if (!SiteHostPattern.TryParse(host, options.Domain, out var key, out var projectId))
        {
            await next(ctx);
            return;
        }

        var site = await db.Sites.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Key == key, ctx.RequestAborted);

        if (site is null || !site.Enabled || site.ActiveDeploymentId is null || !registry.TryGet(site.Id, out var running))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("This site is not currently deployed.", ctx.RequestAborted);
            return;
        }

        var destinationPrefix = $"http://{running.Host}:{running.Port}";
        var error = await forwarder.SendAsync(ctx, destinationPrefix, HttpClient, RequestConfig);
        if (error != ForwarderError.None)
        {
            var errorFeature = ctx.GetForwarderErrorFeature();
            logger.LogWarning(errorFeature?.Exception, "Site proxy forwarding error {Error} for {Host}", error, host);
        }
    }
}
