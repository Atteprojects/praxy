using Praxy.Core.Errors;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Converts every thrown error into the public envelope. <see cref="PraxyException"/> carries
/// its own status/type; anything else is logged and mapped to 500 <c>general_server_error</c>
/// without leaking internals.
/// </summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (PraxyException ex)
        {
            if (ctx.Response.HasStarted)
                throw;
            ctx.Response.StatusCode = ex.Code;
            await ctx.Response.WriteAsJsonAsync(
                ErrorEnvelope.Create(ctx, ex.Code, ex.Type, ex.Message, ex.Fields));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            if (ctx.Response.HasStarted)
                throw;
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(
                ErrorEnvelope.Create(ctx, 500, ErrorTypes.GeneralServerError, "An unexpected error occurred."));
        }
    }
}
