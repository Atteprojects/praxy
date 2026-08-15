using Microsoft.EntityFrameworkCore;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// The platform allowlist enforced as a CORS origin check. Cross-origin browser calls to the
/// data plane must come from a hostname registered as a platform; anything else is a hard 403
/// <c>general_unknown_origin</c>. Preflights are answered permissively (the project header is
/// not present on them) — enforcement happens on the actual request. Console endpoints are
/// same-origin by design and get no CORS treatment at all.
/// </summary>
public sealed class PlatformCorsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, PraxyDb db)
    {
        if (!ctx.Request.Path.StartsWithSegments("/v1"))
        {
            await next(ctx);
            return;
        }

        var origin = ctx.Request.Headers.Origin.FirstOrDefault();

        if (HttpMethods.IsOptions(ctx.Request.Method) && origin is not null &&
            ctx.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            var headers = ctx.Response.Headers;
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            headers.AccessControlAllowOrigin = origin;
            headers.AccessControlAllowCredentials = "true";
            headers.AccessControlAllowMethods = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
            headers.AccessControlAllowHeaders = ctx.Request.Headers.AccessControlRequestHeaders.Count > 0
                ? ctx.Request.Headers.AccessControlRequestHeaders.ToString()
                : "Content-Type, X-Praxy-Project, X-Praxy-Session, X-Praxy-Key";
            headers.AccessControlMaxAge = "600";
            headers.Vary = "Origin";
            return;
        }

        // No Origin (curl, SDKs, same-site navigation), same-origin XHR, or the same-origin-only
        // console surface: nothing to enforce, nothing to emit.
        if (origin is null || origin == $"{ctx.Request.Scheme}://{ctx.Request.Host}" ||
            ctx.Request.Path.StartsWithSegments("/v1/console"))
        {
            await next(ctx);
            return;
        }

        var projectId = ctx.Request.Headers[Endpoints.DataPlaneEndpoints.ProjectHeader].FirstOrDefault()
                        ?? ctx.Request.Query["project"].FirstOrDefault();
        if (string.IsNullOrEmpty(projectId) || Ids.IsReservedProjectId(projectId))
        {
            // The endpoint itself will refuse the missing/reserved project with the right error.
            await next(ctx);
            return;
        }

        var hostnames = await db.Platforms
            .Where(p => p.ProjectId == projectId && p.Hostname != null)
            .Select(p => p.Hostname!)
            .ToListAsync(ctx.RequestAborted);
        if (!PlatformAllowlist.OriginAllowed(hostnames, origin))
            throw new PraxyException(403, ErrorTypes.GeneralUnknownOrigin,
                $"Origin '{origin}' is not registered as a platform for this project.");

        ctx.Response.Headers.AccessControlAllowOrigin = origin;
        ctx.Response.Headers.AccessControlAllowCredentials = "true";
        ctx.Response.Headers.AccessControlExposeHeaders = "X-Praxy-Request-Id, Retry-After, RateLimit-Limit, RateLimit-Remaining, RateLimit-Reset";
        ctx.Response.Headers.Vary = "Origin";
        await next(ctx);
    }
}
