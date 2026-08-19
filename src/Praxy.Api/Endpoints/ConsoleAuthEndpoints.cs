using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record ClaimRequest(string Email, string Password, string? Name, string? SetupToken);

public sealed record LoginRequest(string Email, string Password);

/// <summary>The session token is returned in the body as well as the cookie — SDKs use the header form.</summary>
public sealed record ConsoleSessionResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record ConsoleSignInResponse(ConsoleAccount Account, ConsoleSessionResponse Session);

public static class ConsoleAuthEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var console = api.MapGroup("/v1/console");

        console.MapPost("/claim", Claim).Produces<ConsoleSignInResponse>(StatusCodes.Status201Created);
        console.MapPost("/sessions", Login).Produces<ConsoleSignInResponse>(StatusCodes.Status201Created);

        var authed = console.MapGroup("")
            .AddEndpointFilter<RequireOperatorFilter>();
        authed.MapGet("/account", Account).Produces<ConsoleAccount>();
        authed.MapDelete("/sessions/current", Logout).Produces(StatusCodes.Status204NoContent);
    }

    /// <summary>First account wins the instance. Closed (409) forever after.</summary>
    private static async Task<IResult> Claim(
        ClaimRequest req, HttpContext http, ConsoleAuthService auth, SetupTokenService setupTokens, PraxyDb db,
        CancellationToken ct)
    {
        if (!setupTokens.Validate(req.SetupToken))
            throw new PraxyException(401, ErrorTypes.InstanceSetupTokenInvalid,
                "A valid setup token is required to claim this instance. It is printed in the server logs at startup.");

        var session = await auth.ClaimAsync(
            req.Email, req.Password, req.Name ?? "", ClientIp(http), http.Request.Headers.UserAgent, ct);

        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            Actor = $"admin:{session.Account.Id}",
            Action = "instance.claim",
            Resource = "instance",
            Ip = ClientIp(http),
        });
        await db.SaveChangesAsync(ct);

        SessionCookie.Set(http, session.Token, session.ExpiresAt);
        return Results.Created("/v1/console/account", new ConsoleSignInResponse(
            session.Account, new ConsoleSessionResponse(session.Token, session.ExpiresAt)));
    }

    private static async Task<IResult> Login(
        LoginRequest req, HttpContext http, ConsoleAuthService auth, CancellationToken ct)
    {
        var session = await auth.LoginAsync(
            req.Email, req.Password, ClientIp(http), http.Request.Headers.UserAgent, ct);
        SessionCookie.Set(http, session.Token, session.ExpiresAt);
        return Results.Created("/v1/console/account", new ConsoleSignInResponse(
            session.Account, new ConsoleSessionResponse(session.Token, session.ExpiresAt)));
    }

    private static IResult Account(HttpContext http) =>
        Results.Ok(RequireOperatorFilter.Current(http).Account);

    private static async Task<IResult> Logout(HttpContext http, ConsoleAuthService auth, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        await auth.DeleteSessionAsync(op.Session.Id, ct);
        SessionCookie.Clear(http);
        return Results.NoContent();
    }

    private static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
