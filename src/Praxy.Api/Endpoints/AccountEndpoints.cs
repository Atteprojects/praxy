using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Auth.OAuth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record SignupRequest(string Email, string Password, string? Name);

public sealed record AppLoginRequest(string Email, string Password);

public sealed record TokenExchangeRequest(string UserId, string Secret);

public sealed record UpdateNameRequest(string Name);

public sealed record UpdatePasswordRequest(string Password, string? OldPassword);

public sealed record UpdatePrefsRequest(JsonObject Prefs);

public sealed record SendVerificationRequest(string Url);

public sealed record ConfirmTokenRequest(string UserId, string Secret);

public sealed record SendRecoveryRequest(string Email, string Url);

public sealed record ConfirmRecoveryRequest(string UserId, string Secret, string Password);

/// <summary>
/// The app-user account surface (<c>/v1/account</c>) — what SDKs call on behalf of the
/// signed-in (or signing-in) end user. Console operators have their own endpoints.
/// </summary>
public static class AccountEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var account = api.MapGroup("/v1/account")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>();

        account.MapPost("", Signup).RequireRateLimiting("auth");
        account.MapGet("", Get);
        account.MapPatch("/name", UpdateName);
        account.MapPatch("/password", UpdatePassword);
        account.MapPatch("/prefs", UpdatePrefs);

        account.MapPost("/sessions/email", Login).RequireRateLimiting("auth");
        account.MapPost("/sessions/token", ExchangeToken).RequireRateLimiting("auth");
        account.MapGet("/sessions", ListSessions);
        account.MapDelete("/sessions/current", DeleteCurrentSession);
        account.MapDelete("/sessions/{sessionId}", DeleteSession);

        // The role-resolution debug endpoint: what the permission engine will see for this caller.
        account.MapGet("/roles", Roles);

        account.MapPost("/verification", SendVerification).RequireRateLimiting("auth-email");
        account.MapPut("/verification", ConfirmVerification).RequireRateLimiting("auth");
        account.MapPost("/recovery", SendRecovery).RequireRateLimiting("auth-email");
        account.MapPut("/recovery", ConfirmRecovery).RequireRateLimiting("auth");

        // Browser-navigated OAuth endpoints: no headers available, so the project rides the
        // query string / route and the rest of the state rides a signed cookie.
        api.MapGet("/v1/account/sessions/oauth2/{provider}", OAuthStart).RequireRateLimiting("auth");
        api.MapGet("/v1/account/sessions/oauth2/callback/{provider}/{projectId}", OAuthCallback);
    }

    // ---- signup / login / sessions ---------------------------------------------------------

    private static async Task<IResult> Signup(
        SignupRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var session = await auth.SignupAsync(
            project, req.Email, req.Password, req.Name ?? "", ClientIp(http), http.Request.Headers.UserAgent, ct);
        AppSessionCookie.Set(http, project.Id, session.Token, session.Session.ExpiresAt);
        return Results.Created("/v1/account", CreatedSessionResponse.From(session));
    }

    private static async Task<IResult> Login(
        AppLoginRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var session = await auth.LoginAsync(
            project, req.Email, req.Password, ClientIp(http), http.Request.Headers.UserAgent, ct);
        AppSessionCookie.Set(http, project.Id, session.Token, session.Session.ExpiresAt);
        return Results.Created("/v1/account/sessions/current", CreatedSessionResponse.From(session));
    }

    /// <summary>The universal token→session converging point (OAuth today, magic-url/OTP later).</summary>
    private static async Task<IResult> ExchangeToken(
        TokenExchangeRequest req, HttpContext http, AppAuthService auth, OAuthService oauth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        if (!Ids.TryParseWire(req.UserId, out var userId))
            throw new PraxyException(401, ErrorTypes.UserInvalidToken, "Invalid or expired token.");

        var secret = oauth.UnwrapExchangeSecret(req.Secret);
        var session = await auth.ExchangeTokenAsync(
            project, userId, secret, ClientIp(http), http.Request.Headers.UserAgent, ct);
        AppSessionCookie.Set(http, project.Id, session.Token, session.Session.ExpiresAt);
        return Results.Created("/v1/account/sessions/current", CreatedSessionResponse.From(session));
    }

    private static IResult Get(HttpContext http) =>
        Results.Ok(AppUserResponse.From(AppPrincipalFilter.RequireUser(http).User));

    private static async Task<IResult> ListSessions(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var (user, current) = AppPrincipalFilter.RequireUser(http);
        var sessions = await db.Sessions
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new
        {
            total = sessions.Count,
            sessions = sessions.Select(s => SessionResponse.From(s, current.Id)),
        });
    }

    private static async Task<IResult> DeleteCurrentSession(
        HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var (user, session) = AppPrincipalFilter.RequireUser(http);
        await auth.DeleteSessionAsync(project.Id, user, session.Id, ct);
        AppSessionCookie.Clear(http, project.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSession(
        string sessionId, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var (user, current) = AppPrincipalFilter.RequireUser(http);

        Guid target;
        if (sessionId == "current")
            target = current.Id;
        else if (!Ids.TryParseWire(sessionId, out target))
            throw PraxyException.NotFound(ErrorTypes.UserSessionNotFound, "Session not found.");

        await auth.DeleteSessionAsync(project.Id, user, target, ct);
        if (target == current.Id)
            AppSessionCookie.Clear(http, project.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> Roles(HttpContext http, IRoleResolver resolver)
    {
        var principal = AppPrincipalFilter.Current(http);
        var roles = await RequestRoles.GetAsync(http, resolver);
        return Results.Ok(new
        {
            roles,
            principal = principal switch
            {
                RequestPrincipal.AppUser => "user",
                RequestPrincipal.Key => "key",
                _ => "guest",
            },
            scopes = principal is RequestPrincipal.Key(var key) ? key.Scopes : null,
        });
    }

    // ---- account updates ---------------------------------------------------------------------

    private static async Task<IResult> UpdateName(
        UpdateNameRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var user = AppPrincipalFilter.RequireUser(http).User;
        return Results.Ok(AppUserResponse.From(await auth.UpdateNameAsync(project.Id, user, req.Name, ct)));
    }

    private static async Task<IResult> UpdatePassword(
        UpdatePasswordRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var user = AppPrincipalFilter.RequireUser(http).User;
        return Results.Ok(AppUserResponse.From(
            await auth.UpdatePasswordAsync(project, user, req.Password, req.OldPassword, ct)));
    }

    private static async Task<IResult> UpdatePrefs(
        UpdatePrefsRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var user = AppPrincipalFilter.RequireUser(http).User;
        return Results.Ok(AppUserResponse.From(await auth.UpdatePrefsAsync(project.Id, user, req.Prefs, ct)));
    }

    // ---- verification / recovery -------------------------------------------------------------

    private static async Task<IResult> SendVerification(
        SendVerificationRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var user = AppPrincipalFilter.RequireUser(http).User;
        await auth.SendVerificationAsync(project, user, req.Url, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ConfirmVerification(
        ConfirmTokenRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        if (!Ids.TryParseWire(req.UserId, out var userId))
            throw new PraxyException(401, ErrorTypes.UserInvalidToken, "Invalid or expired token.");
        return Results.Ok(AppUserResponse.From(
            await auth.ConfirmVerificationAsync(project, userId, req.Secret, ct)));
    }

    private static async Task<IResult> SendRecovery(
        SendRecoveryRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        await auth.SendRecoveryAsync(project, req.Email, req.Url, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ConfirmRecovery(
        ConfirmRecoveryRequest req, HttpContext http, AppAuthService auth, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        if (!Ids.TryParseWire(req.UserId, out var userId))
            throw new PraxyException(401, ErrorTypes.UserInvalidToken, "Invalid or expired token.");
        await auth.ConfirmRecoveryAsync(project, userId, req.Secret, req.Password, ct);
        return Results.NoContent();
    }

    // ---- OAuth (browser-navigated) -----------------------------------------------------------

    private static async Task<IResult> OAuthStart(
        string provider, HttpContext http, PraxyDb db, OAuthService oauth, CancellationToken ct)
    {
        var project = await ResolveBrowserProjectAsync(
            http.Request.Query["project"].FirstOrDefault()
            ?? http.Request.Headers[DataPlaneEndpoints.ProjectHeader].FirstOrDefault(), db, ct);

        var success = http.Request.Query["success"].FirstOrDefault()
            ?? throw MissingParam("success");
        var failure = http.Request.Query["failure"].FirstOrDefault()
            ?? throw MissingParam("failure");

        var result = await oauth.StartAsync(project, provider, success, failure, CallbackUri(http, provider, project.Id), ct);
        http.Response.Cookies.Append(OAuthService.StateCookieName(project.Id), result.StateCookieValue,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/v1/account/sessions/oauth2",
                MaxAge = OAuthService.StateLifetime,
            });
        return Results.Redirect(result.AuthorizeUrl);
    }

    private static async Task<IResult> OAuthCallback(
        string provider, string projectId, HttpContext http, PraxyDb db, OAuthService oauth, CancellationToken ct)
    {
        var project = await ResolveBrowserProjectAsync(projectId, db, ct);
        var redirect = await oauth.HandleCallbackAsync(
            project, provider,
            http.Request.Query["code"].FirstOrDefault(),
            http.Request.Query["state"].FirstOrDefault(),
            http.Request.Query["error"].FirstOrDefault(),
            http.Request.Cookies[OAuthService.StateCookieName(project.Id)],
            CallbackUri(http, provider, project.Id), ct);
        http.Response.Cookies.Delete(OAuthService.StateCookieName(project.Id),
            new CookieOptions { Path = "/v1/account/sessions/oauth2" });
        return Results.Redirect(redirect);
    }

    /// <summary>Same rules as the data-plane guard, for endpoints reached by browser navigation.</summary>
    private static async Task<Project> ResolveBrowserProjectAsync(string? projectId, PraxyDb db, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(projectId))
            throw MissingParam("project");
        if (Ids.IsReservedProjectId(projectId))
            throw new PraxyException(403, ErrorTypes.ProjectReserved,
                "The console project is not accessible through the data-plane API.");
        return await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.ProjectNotFound, $"Project '{projectId}' not found.");
    }

    private static string CallbackUri(HttpContext http, string provider, string projectId) =>
        $"{http.Request.Scheme}://{http.Request.Host}/v1/account/sessions/oauth2/callback/{provider}/{projectId}";

    private static PraxyException MissingParam(string name) =>
        PraxyException.ArgumentInvalid($"The '{name}' query parameter is required.",
            new Dictionary<string, string[]> { [name] = ["Required."] });

    private static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
