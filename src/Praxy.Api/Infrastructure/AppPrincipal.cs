using Praxy.Auth;
using Praxy.Core.Errors;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Resolves the data-plane caller after <see cref="Endpoints.DataPlaneEndpoints.ProjectGuardFilter"/>:
/// an API key (<c>X-Praxy-Key</c>), an app-user session (<c>praxy_session_&lt;projectId&gt;</c> cookie
/// or <c>X-Praxy-Session</c>), or a guest. A bad explicit key is a hard 401; a stale session
/// degrades to guest so public endpoints keep working.
/// </summary>
public sealed class AppPrincipalFilter(AppAuthService auth, ApiKeyService keys) : IEndpointFilter
{
    public const string KeyHeader = "X-Praxy-Key";
    public const string SessionHeader = "X-Praxy-Session";
    private const string ItemKey = "praxy.principal";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var project = Endpoints.DataPlaneEndpoints.CurrentProject(http);

        RequestPrincipal principal = new RequestPrincipal.Guest();

        var keyToken = http.Request.Headers[KeyHeader].FirstOrDefault();
        if (!string.IsNullOrEmpty(keyToken))
        {
            var apiKey = await keys.ResolveAsync(project.Id, keyToken, http.RequestAborted)
                ?? throw PraxyException.Unauthorized("Invalid API key.");
            principal = new RequestPrincipal.Key(apiKey);
        }
        else
        {
            var sessionToken = http.Request.Headers[SessionHeader].FirstOrDefault()
                               ?? http.Request.Cookies[AppSessionCookie.Name(project.Id)];
            if (!string.IsNullOrEmpty(sessionToken))
            {
                var resolved = await auth.ResolveSessionAsync(project.Id, sessionToken, http.RequestAborted);
                if (resolved is not null)
                    principal = new RequestPrincipal.AppUser(resolved.User, resolved.Session);
            }
        }

        http.Items[ItemKey] = principal;
        return await next(ctx);
    }

    public static RequestPrincipal Current(HttpContext http) =>
        http.Items[ItemKey] as RequestPrincipal ?? new RequestPrincipal.Guest();

    /// <summary>The signed-in app user, or 401.</summary>
    public static RequestPrincipal.AppUser RequireUser(HttpContext http) =>
        Current(http) as RequestPrincipal.AppUser ?? throw PraxyException.Unauthorized();

    /// <summary>An API key holding <paramref name="scope"/>, or 401.</summary>
    public static ApiKey RequireScope(HttpContext http, string scope)
    {
        if (Current(http) is not RequestPrincipal.Key(var apiKey))
            throw PraxyException.Unauthorized("This endpoint requires an API key.");
        return apiKey.Scopes.Contains(scope)
            ? apiKey
            : throw new PraxyException(401, ErrorTypes.GeneralUnauthorizedScope,
                $"The API key is missing the '{scope}' scope.");
    }

    /// <summary>Endpoints accepting either a user session or a key with the scope.</summary>
    public static (RequestPrincipal.AppUser? User, ApiKey? Key) RequireUserOrScope(HttpContext http, string scope) =>
        Current(http) switch
        {
            RequestPrincipal.AppUser user => (user, null),
            RequestPrincipal.Key => (null, RequireScope(http, scope)),
            _ => throw PraxyException.Unauthorized(),
        };
}

public static class AppSessionCookie
{
    public static string Name(string projectId) => $"praxy_session_{projectId}";

    public static void Set(HttpContext ctx, string projectId, string token, DateTimeOffset expiresAt) =>
        ctx.Response.Cookies.Append(Name(projectId), token, new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresAt,
        });

    public static void Clear(HttpContext ctx, string projectId) =>
        ctx.Response.Cookies.Delete(Name(projectId), new CookieOptions { Path = "/" });
}

/// <summary>
/// The caller's resolved permission roles, computed once per request on first use and cached
/// on the request context. The query compiler (Phase 3) and realtime fan-out (Phase 4) read
/// this same value — one resolver, one cache, several consumers.
/// </summary>
public static class RequestRoles
{
    private const string ItemKey = "praxy.roles";

    public static async Task<string[]> GetAsync(HttpContext http, IRoleResolver resolver)
    {
        if (http.Items[ItemKey] is string[] cached)
            return cached;
        var roles = await resolver.ResolveAsync(AppPrincipalFilter.Current(http), http.RequestAborted);
        http.Items[ItemKey] = roles;
        return roles;
    }
}
