using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Auth.OAuth;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

/// <summary>
/// Operator Google sign-in — browser-navigated, same two-leg shape as <see cref="AccountEndpoints"/>'
/// app-user OAuth, but scoped to the console: no project in the route, no caller-supplied
/// success/failure URL (see <see cref="ConsoleOAuthService"/>'s own remarks on why), and the callback
/// sets the session cookie directly instead of handing back a secret for the client to exchange,
/// since the callback and the console share an origin.
/// </summary>
public static class ConsoleOAuthEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/v1/console/sessions/oauth2/{provider}", Start).RequireRateLimiting("auth")
            .Produces(StatusCodes.Status302Found);
        api.MapGet("/v1/console/sessions/oauth2/callback/{provider}", Callback)
            .Produces(StatusCodes.Status302Found);
    }

    /// <summary>
    /// <c>setupToken</c> is only consulted if the instance is still unclaimed by the time the
    /// callback runs; <c>organizationId</c>/<c>userId</c>/<c>secret</c> — all three or none — mean
    /// "this is an invite accept", the same query string <c>AcceptOrganizationInvitePage</c> already
    /// reads off its own URL.
    /// </summary>
    private static IResult Start(string provider, HttpContext http, ConsoleOAuthService oauth)
    {
        var q = http.Request.Query;
        var result = oauth.Start(
            provider,
            q["setupToken"].FirstOrDefault(),
            q["organizationId"].FirstOrDefault(),
            q["userId"].FirstOrDefault(),
            q["secret"].FirstOrDefault(),
            CallbackUri(http, provider));

        http.Response.Cookies.Append(ConsoleOAuthService.StateCookieName, result.StateCookieValue,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/v1/console/sessions/oauth2",
                MaxAge = ConsoleOAuthService.StateLifetime,
            });
        return Results.Redirect(result.AuthorizeUrl);
    }

    private static async Task<IResult> Callback(
        string provider, HttpContext http, ConsoleOAuthService oauth, SetupTokenService setupTokens,
        PraxyDb db, CancellationToken ct)
    {
        // SetupTokenService lives in this project (Praxy.Api); ConsoleOAuthService (Praxy.Auth)
        // cannot reference it, so the delegate crosses the boundary instead.
        var result = await oauth.HandleCallbackAsync(
            provider,
            http.Request.Query["code"].FirstOrDefault(),
            http.Request.Query["state"].FirstOrDefault(),
            http.Request.Query["error"].FirstOrDefault(),
            http.Request.Cookies[ConsoleOAuthService.StateCookieName],
            CallbackUri(http, provider),
            setupTokens.Validate,
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.UserAgent,
            ct);

        http.Response.Cookies.Delete(ConsoleOAuthService.StateCookieName,
            new CookieOptions { Path = "/v1/console/sessions/oauth2" });

        if (result.Session is not null)
        {
            SessionCookie.Set(http, result.Session.Token, result.Session.ExpiresAt);

            // Parity with the password claim door (ConsoleAuthEndpoints.Claim), which audits
            // instance.claim — accept and ordinary login aren't audited today either (matching
            // ConsoleOrganizationEndpoints.AcceptInvite's own precedent), so only claim gets an entry.
            if (result.Intent == ConsoleOAuthIntent.Claim)
            {
                db.AuditLog.Add(new AuditLogEntry
                {
                    Id = Ids.NewUuid(),
                    Actor = $"admin:{result.Session.Account.Id}",
                    Action = "instance.claim",
                    Resource = "instance",
                    Ip = http.Connection.RemoteIpAddress?.ToString(),
                });
                await db.SaveChangesAsync(ct);
            }
        }

        return Results.Redirect(result.RedirectPath);
    }

    private static string CallbackUri(HttpContext http, string provider) =>
        $"{http.Request.Scheme}://{http.Request.Host}/v1/console/sessions/oauth2/callback/{provider}";
}
