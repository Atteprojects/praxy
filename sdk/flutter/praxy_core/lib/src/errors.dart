/// The Praxy SDK's error hierarchy. Sealed so a `switch` over the top-level type is
/// exhaustive — a caller can tell "offline" from "server rejected you" from "we
/// couldn't parse what the server sent back" without string-matching anything.
sealed class PraxyException implements Exception {
  const PraxyException({required this.message});

  final String message;

  @override
  String toString() => message;
}

/// The server answered with an error envelope (`{message, code, type, version,
/// requestId, fields?}`). [type] is Praxy's stable, snake_case, public error code —
/// switch on it, not on [message], which is free to change wording at any time.
class PraxyApiException extends PraxyException {
  const PraxyApiException({
    required super.message,
    required this.status,
    required this.type,
    this.requestId,
  });

  final int status;
  final String type;
  final String? requestId;

  @override
  String toString() => 'PraxyApiException($status $type): $message';
}

/// 401/403 — missing, invalid, or insufficiently-scoped credentials.
final class PraxyAuthException extends PraxyApiException {
  const PraxyAuthException({
    required super.message,
    required super.status,
    required super.type,
    super.requestId,
  });
}

/// 404 — the resource doesn't exist, or the caller can't see it (the two are
/// indistinguishable by design; see architecture.md's permission model).
final class PraxyNotFoundException extends PraxyApiException {
  const PraxyNotFoundException({
    required super.message,
    required super.status,
    required super.type,
    super.requestId,
  });
}

/// 409 — e.g. creating a row with an id that already exists.
final class PraxyConflictException extends PraxyApiException {
  const PraxyConflictException({
    required super.message,
    required super.status,
    required super.type,
    super.requestId,
  });
}

/// 429 — [retryAfter], when the server sent one, is the `Retry-After` header.
final class PraxyRateLimitException extends PraxyApiException {
  const PraxyRateLimitException({
    required super.message,
    required super.status,
    required super.type,
    super.requestId,
    this.retryAfter,
  });

  final Duration? retryAfter;
}

/// 400 with a structured `fields` map — the error envelope's per-field messages,
/// keyed exactly as the server named them (a row column key, a request field name).
final class PraxyValidationException extends PraxyApiException {
  const PraxyValidationException({
    required super.message,
    required super.status,
    required super.type,
    super.requestId,
    required this.fields,
  });

  final Map<String, List<String>> fields;
}

/// The request never got a response to interpret: DNS failure, connection refused,
/// TLS failure, timeout. [cause] is the original error object — never collapsed to
/// a string — and [stackTrace] is preserved so it isn't lost at the wrapping point.
final class PraxyNetworkException extends PraxyException {
  PraxyNetworkException({
    required this.cause,
    required this.stackTrace,
    this.isTimeout = false,
  }) : super(message: 'Network error: $cause');

  final Object cause;
  final StackTrace stackTrace;
  final bool isTimeout;
}

/// The server responded, but the body wasn't the shape this SDK call expected —
/// malformed JSON, a missing field, or a row that failed its [RowCodec].
final class PraxyDecodeException extends PraxyException {
  PraxyDecodeException({required this.field, required this.expected, String? message})
    : super(message: message ?? "Failed to decode '$field': expected $expected.");

  final String field;
  final String expected;
}
