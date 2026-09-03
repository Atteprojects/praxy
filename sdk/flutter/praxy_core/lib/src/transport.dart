/// One HTTP call a [Transport] must perform. [query] uses `List<String>` values
/// so repeated params (`queries[]`) round-trip without a separate encoding path.
final class TransportRequest {
  const TransportRequest({
    required this.method,
    required this.path,
    this.headers = const {},
    this.query = const {},
    this.body,
    this.bodyBytes,
    this.contentType,
  });

  final String method;
  final String path;
  final Map<String, String> headers;
  final Map<String, List<String>> query;

  /// JSON-encodable request body, or `null` for no body.
  final Object? body;

  /// A raw byte body — a storage upload, where the bytes *are* the request. Never
  /// set together with [body]; a [Transport] sends these verbatim rather than
  /// JSON-encoding anything.
  final List<int>? bodyBytes;

  /// The `content-type` for [bodyBytes]. Meaningless for a JSON [body], which is
  /// always `application/json`.
  final String? contentType;
}

final class TransportResponse {
  const TransportResponse({
    required this.statusCode,
    required this.headers,
    required this.bodyBytes,
  });

  final int statusCode;

  /// Lower-cased header names, matching `package:http`'s own normalization.
  final Map<String, String> headers;
  final List<int> bodyBytes;
}

/// The platform seam: everything network-shaped goes through this so the pure-Dart
/// core never depends on a platform HTTP stack directly, and a host app can swap in
/// its own client (proxying, certificate pinning, request logging) without forking.
abstract interface class Transport {
  Future<TransportResponse> send(TransportRequest request);
  void close();
}
