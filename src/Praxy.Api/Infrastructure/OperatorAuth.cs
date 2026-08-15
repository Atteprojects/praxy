using Praxy.Auth;
using Praxy.Core.Errors;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Infrastructure;

public sealed record OperatorContext(Session Session, ConsoleAccount Account);

/// <summary>
/// Resolves the operator session from the <c>praxy_session_console</c> cookie (browsers) or the
/// <c>X-Praxy-Session</c> header, and rejects unauthenticated requests with 401.
/// </summary>
public sealed class RequireOperatorFilter(ConsoleAuthService auth) : IEndpointFilter
{
    public const string CookieName = "praxy_session_console";
    public const string HeaderName = "X-Praxy-Session";
    private const string ItemKey = "praxy.operator";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var token = http.Request.Cookies[CookieName]
                    ?? http.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
            throw PraxyException.Unauthorized();

        var resolved = await auth.ResolveSessionAsync(token, http.RequestAborted);
        if (resolved is null)
            throw PraxyException.Unauthorized();

        http.Items[ItemKey] = new OperatorContext(resolved.Value.Session, resolved.Value.Account);
        return await next(ctx);
    }

    public static OperatorContext Current(HttpContext http) =>
        http.Items[ItemKey] as OperatorContext
        ?? throw new InvalidOperationException("Operator context missing — endpoint not behind RequireOperatorFilter.");
}
