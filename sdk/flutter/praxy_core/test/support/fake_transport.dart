import 'dart:convert';

import 'package:praxy_core/praxy_core.dart';

typedef ResponseBuilder = TransportResponse Function(TransportRequest request);

/// A [Transport] test double: replays canned responses (or a handler) instead of
/// hitting the network, and records every request sent through it for assertions.
final class FakeTransport implements Transport {
  FakeTransport(this._handler);

  final ResponseBuilder _handler;
  final List<TransportRequest> requests = [];

  @override
  Future<TransportResponse> send(TransportRequest request) async {
    requests.add(request);
    return _handler(request);
  }

  @override
  void close() {}
}

TransportResponse jsonResponse(int status, Object? body, {Map<String, String> headers = const {}}) {
  final bytes = body == null ? const <int>[] : utf8.encode(jsonEncode(body));
  return TransportResponse(statusCode: status, headers: headers, bodyBytes: bytes);
}

TransportResponse emptyResponse(int status, {Map<String, String> headers = const {}}) =>
    TransportResponse(statusCode: status, headers: headers, bodyBytes: const []);
