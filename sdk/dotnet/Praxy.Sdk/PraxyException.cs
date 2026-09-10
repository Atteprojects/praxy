namespace Praxy.Sdk;

/// <summary>
/// Base of every error this SDK raises. The hierarchy below deliberately mirrors
/// <c>praxy_core</c>'s and <c>@praxy/core</c>'s, class for class: an operator who has read one
/// SDK's error handling should not have to relearn another's, and the server's own
/// <see cref="Type"/> strings are the same in all three.
/// </summary>
public abstract class PraxyException(string message) : Exception(message);

/// <summary>
/// An error the API returned as its standard envelope. <see cref="Type"/> is the stable,
/// machine-readable string documented as public API — switch on that, never on
/// <see cref="Exception.Message"/>, which is prose and may be reworded.
/// </summary>
public class PraxyApiException(string message, string type, int code, string? requestId)
    : PraxyException(message)
{
    public string Type { get; } = type;

    /// <summary>The HTTP status, repeated in the envelope.</summary>
    public int Code { get; } = code;

    /// <summary>Matches the <c>X-Praxy-Request-Id</c> response header — quote it in a bug report.</summary>
    public string? RequestId { get; } = requestId;
}

/// <summary>401 — no credential, or one the server rejected.</summary>
public sealed class PraxyAuthException(string message, string type, int code, string? requestId)
    : PraxyApiException(message, type, code, requestId);

/// <summary>404 — including a resource that exists but this key may not see.</summary>
public sealed class PraxyNotFoundException(string message, string type, int code, string? requestId)
    : PraxyApiException(message, type, code, requestId);

/// <summary>409 — a guard refused, e.g. deleting something non-empty without <c>force</c>.</summary>
public sealed class PraxyConflictException(string message, string type, int code, string? requestId)
    : PraxyApiException(message, type, code, requestId);

/// <summary>429. <see cref="RetryAfter"/> comes from the <c>Retry-After</c> header when present.</summary>
public sealed class PraxyRateLimitException(string message, string type, int code, string? requestId, TimeSpan? retryAfter)
    : PraxyApiException(message, type, code, requestId)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>400 with per-field detail, from the envelope's <c>fields</c>.</summary>
public sealed class PraxyValidationException(
    string message, string type, int code, string? requestId, IReadOnlyDictionary<string, string[]> fields)
    : PraxyApiException(message, type, code, requestId)
{
    public IReadOnlyDictionary<string, string[]> Fields { get; } = fields;

    /// <summary>Messages for one field, or empty when the server reported none for it.</summary>
    public IReadOnlyList<string> For(string field) =>
        Fields.TryGetValue(field, out var messages) ? messages : [];
}

/// <summary>The request never produced a response: DNS, connection, TLS, or a timeout.</summary>
public sealed class PraxyNetworkException(string message, Exception? innerException = null)
    : PraxyException(message)
{
    public Exception? Cause { get; } = innerException;
}

/// <summary>A response arrived but was not the shape this SDK expects.</summary>
public sealed class PraxyDecodeException(string message) : PraxyException(message);
