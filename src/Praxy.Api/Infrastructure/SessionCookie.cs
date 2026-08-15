namespace Praxy.Api.Infrastructure;

public static class SessionCookie
{
    public static void Set(HttpContext ctx, string token, DateTimeOffset expiresAt) =>
        ctx.Response.Cookies.Append(RequireOperatorFilter.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresAt,
        });

    public static void Clear(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(RequireOperatorFilter.CookieName, new CookieOptions { Path = "/" });
}
