import 'dart:convert';

import 'package:http/http.dart' as http;

import 'transport.dart';

/// The default [Transport]: `package:http` against a fixed base [endpoint]. Works
/// identically on the Dart VM and in Flutter — no platform-specific dependency.
final class HttpTransport implements Transport {
  HttpTransport({required this.endpoint, http.Client? client})
    : _client = client ?? http.Client();

  final Uri endpoint;
  final http.Client _client;

  @override
  Future<TransportResponse> send(TransportRequest request) async {
    final uri = _resolve(request);
    final headers = {...request.headers};
    List<int>? body;
    if (request.bodyBytes != null) {
      // Sent verbatim: a storage upload's body *is* the file, and JSON-encoding it
      // would corrupt it.
      body = request.bodyBytes;
      headers['content-type'] = request.contentType ?? 'application/octet-stream';
    } else if (request.body != null) {
      body = utf8.encode(jsonEncode(request.body));
      headers['content-type'] = 'application/json';
    }

    final response = await switch (request.method) {
      'GET' => _client.get(uri, headers: headers),
      'POST' => _client.post(uri, headers: headers, body: body),
      'PATCH' => _client.patch(uri, headers: headers, body: body),
      'PUT' => _client.put(uri, headers: headers, body: body),
      'DELETE' => _client.delete(uri, headers: headers, body: body),
      _ => throw ArgumentError.value(request.method, 'method', 'Unsupported HTTP method.'),
    };

    return TransportResponse(
      statusCode: response.statusCode,
      headers: response.headers,
      bodyBytes: response.bodyBytes,
    );
  }

  Uri _resolve(TransportRequest request) {
    final path = request.path.startsWith('/') ? request.path.substring(1) : request.path;
    final base = endpoint.resolve(path);
    if (request.query.isEmpty) return base;
    return base.replace(queryParameters: {...base.queryParametersAll, ...request.query});
  }

  @override
  void close() => _client.close();
}
