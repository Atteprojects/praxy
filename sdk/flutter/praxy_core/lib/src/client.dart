import 'dart:async';
import 'dart:convert';

import 'errors.dart';
import 'http_transport.dart';
import 'json_utils.dart';
import 'models.dart';
import 'services/account_service.dart';
import 'services/functions_service.dart';
import 'services/tables_service.dart';
import 'services/teams_service.dart';
import 'session_store.dart';
import 'transport.dart';

/// The Praxy client: one instance per project. Owns the [Transport]/[SessionStore]
/// seams, the low-level [request] every service method funnels through, and error
/// mapping from the server's `{message, code, type, version, requestId, fields?}`
/// envelope to the [PraxyException] hierarchy.
final class Praxy {
  Praxy({required String endpoint, required this.projectId, Transport? transport, SessionStore? sessionStore})
    : endpoint = Uri.parse(endpoint),
      sessionStore = sessionStore ?? MemorySessionStore(),
      _transport = transport ?? HttpTransport(endpoint: Uri.parse(endpoint)) {
    account = AccountService(this);
    tables = TablesService(this);
    teams = TeamsService(this);
    functions = FunctionsService(this);
  }

  final Uri endpoint;
  final String projectId;
  final SessionStore sessionStore;
  final Transport _transport;

  late final AccountService account;
  late final TablesService tables;
  late final TeamsService teams;
  late final FunctionsService functions;

  /// Sends one API call and returns the decoded JSON body (`null` for a 204 or an
  /// empty body). Every header/error-mapping rule lives here so service methods stay
  /// pure wire-shape translation.
  Future<Map<String, dynamic>?> request({
    required String method,
    required String path,
    Map<String, List<String>> query = const {},
    Object? body,
  }) async {
    final session = await sessionStore.read();
    final headers = <String, String>{
      'accept': 'application/json',
      'x-praxy-project': projectId,
      if (session != null) 'x-praxy-session': session.secret,
    };

    TransportResponse response;
    try {
      response = await _transport.send(
        TransportRequest(method: method, path: path, headers: headers, query: query, body: body),
      );
    } on PraxyException {
      rethrow;
    } catch (error, stackTrace) {
      throw PraxyNetworkException(cause: error, stackTrace: stackTrace, isTimeout: error is TimeoutException);
    }

    if (response.statusCode >= 400) {
      throw _mapError(response);
    }
    if (response.bodyBytes.isEmpty) return null;

    final Object? decoded;
    try {
      decoded = jsonDecode(utf8.decode(response.bodyBytes));
    } on FormatException catch (error) {
      throw PraxyDecodeException(
        field: path,
        expected: 'a JSON response body',
        message: 'Could not parse the response body as JSON: $error',
      );
    }
    return switch (decoded) {
      null => null,
      Map<String, dynamic> m => m,
      _ => throw PraxyDecodeException(field: path, expected: 'a JSON object', message: 'The response body was not a JSON object.'),
    };
  }

  /// Mints a single-use, 60s realtime ticket (`POST /v1/realtime/ticket`) for the
  /// current session or, if none, fails — `praxy_flutter`'s realtime client uses
  /// this to authenticate its WebSocket the way a browser's cookie handshake would.
  Future<RealtimeTicket> mintRealtimeTicket() async {
    final json = requireJson(
      await request(method: 'POST', path: '/v1/realtime/ticket'),
      '/v1/realtime/ticket',
    );
    return RealtimeTicket.fromJson(json);
  }

  void close() => _transport.close();

  PraxyApiException _mapError(TransportResponse response) {
    var message = 'Request failed with status ${response.statusCode}.';
    var type = 'general_server_error';
    String? requestId = response.headers['x-praxy-request-id'];
    Map<String, List<String>>? fields;

    if (response.bodyBytes.isNotEmpty) {
      try {
        final decoded = jsonDecode(utf8.decode(response.bodyBytes));
        if (decoded is Map<String, dynamic>) {
          message = decoded['message'] as String? ?? message;
          type = decoded['type'] as String? ?? type;
          requestId = decoded['requestId'] as String? ?? requestId;
          final rawFields = decoded['fields'];
          if (rawFields is Map) {
            fields = rawFields.map(
              (key, value) => MapEntry(key as String, (value as List).cast<String>()),
            );
          }
        }
      } catch (_) {
        // Malformed error body — fall back to the generic message built above.
      }
    }

    final status = response.statusCode;
    return switch (status) {
      401 || 403 => PraxyAuthException(message: message, status: status, type: type, requestId: requestId),
      404 => PraxyNotFoundException(message: message, status: status, type: type, requestId: requestId),
      409 => PraxyConflictException(message: message, status: status, type: type, requestId: requestId),
      429 => PraxyRateLimitException(
        message: message,
        status: status,
        type: type,
        requestId: requestId,
        retryAfter: _retryAfter(response),
      ),
      _ when fields != null && fields.isNotEmpty => PraxyValidationException(
        message: message,
        status: status,
        type: type,
        requestId: requestId,
        fields: fields,
      ),
      _ => PraxyApiException(message: message, status: status, type: type, requestId: requestId),
    };
  }

  Duration? _retryAfter(TransportResponse response) {
    final header = response.headers['retry-after'];
    final seconds = header == null ? null : int.tryParse(header);
    return seconds == null ? null : Duration(seconds: seconds);
  }
}
