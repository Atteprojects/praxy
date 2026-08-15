using Serilog.Context;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Assigns every request a server-generated id, echoed as <c>X-Praxy-Request-Id</c> on the
/// response and embedded in error envelopes. Response-only — client-supplied values are ignored.
/// </summary>
public sealed class RequestIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Praxy-Request-Id";
    public const string ItemKey = "praxy.request_id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var requestId = Guid.CreateVersion7().ToString("n");
        ctx.Items[ItemKey] = requestId;
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("RequestId", requestId))
        {
            await next(ctx);
        }
    }
}

public static class RequestIdExtensions
{
    public static string GetRequestId(this HttpContext ctx) =>
        ctx.Items[RequestIdMiddleware.ItemKey] as string ?? "";
}
