using Praxy.Core;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// The public error shape: <c>{message, code, type, version, requestId, fields?}</c>.
/// <c>type</c> strings are stable public API (see <see cref="Praxy.Core.Errors.ErrorTypes"/>).
/// </summary>
public sealed record ErrorEnvelope(
    string Message,
    int Code,
    string Type,
    string Version,
    string RequestId,
    IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static ErrorEnvelope Create(HttpContext ctx, int code, string type, string message,
        IReadOnlyDictionary<string, string[]>? fields = null) =>
        new(message, code, type, PraxyVersion.Current, ctx.GetRequestId(), fields);
}
