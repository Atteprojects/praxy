namespace Praxy.Core.Errors;

/// <summary>
/// An error that maps directly onto the public error envelope
/// <c>{message, code, type, version, requestId, fields?}</c>.
/// </summary>
public sealed class PraxyException(
    int code,
    string type,
    string message,
    IReadOnlyDictionary<string, string[]>? fields = null) : Exception(message)
{
    public int Code { get; } = code;
    public string Type { get; } = type;
    public IReadOnlyDictionary<string, string[]>? Fields { get; } = fields;

    public static PraxyException Unauthorized(string message = "Missing or invalid session.") =>
        new(401, ErrorTypes.GeneralUnauthorized, message);

    public static PraxyException NotFound(string type, string message) => new(404, type, message);

    public static PraxyException ArgumentInvalid(string message, IReadOnlyDictionary<string, string[]>? fields = null) =>
        new(400, ErrorTypes.GeneralArgumentInvalid, message, fields);
}
