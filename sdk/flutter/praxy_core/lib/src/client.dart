import 'dart:async';
import 'dart:convert';

import 'errors.dart';
import 'http_transport.dart';
import 'json_utils.dart';
import 'models.dart';
import 'services/account_service.dart';
import 'services/functions_service.dart';
import 'services/storage_service.dart';
import 'services/tables_service.dart';
import 'services/teams_service.dart';
import 'session_store.dart';
import 'transport.dart';

/// The Praxy client: one instance per project. Owns the [Transport]/[SessionStore]
/// seams, the low-level [request] every service method funnels through, and error
/// mapping from the server's `{message, code, type, version, requestId, fields?}`
/// envelope to the [PraxyException] hierarchy.
final class Praxy {
  /// [apiKey] makes this a **server** client: it authenticates every request with a
  /// project API key (`X-Praxy-Key`) and never reads or writes [sessionStore] at all.
  ///
  /// > **Never construct a client with an `apiKey` inside an app you ship.** A key in a
  /// > Flutter binary or a browser bundle is extractable, and a project API key is not
  /// > scoped to one end user the way a session is. `PraxyFlutter` deliberately offers no
  /// > way to pass one, which is the reason this warning lives here rather than there —
  /// > this class is the one place a shipped app could still reach for it.
  ///
  /// The same package serves both audiences, matching `@praxy/core`'s dual-mode client,
  /// rather than splitting into separate client and server packages the way Appwrite's
  /// `appwrite`/`node-appwrite` do. That split is still available later if this warning
  /// turns out not to be enough.
  Praxy({
    required String endpoint,
    required this.projectId,
    String? apiKey,
    Transport? transport,
    SessionStore? sessionStore,
  }) : endpoint = Uri.parse(endpoint),
       _apiKey = apiKey,
       sessionStore = sessionStore ?? MemorySessionStore(),
       _transport = transport ?? HttpTransport(endpoint: Uri.parse(endpoint)) {
    account = AccountService(this);
    tables = TablesService(this);
    teams = TeamsService(this);
    functions = FunctionsService(this);
    storage = StorageService(this);
  }

  final Uri endpoint;
  final String projectId;
  final SessionStore sessionStore;
  final Transport _transport;
  final String? _apiKey;

  /// Whether this client authenticates as a server (an API key) rather than as an end
  /// user (a session). Callers that behave differently for the two — minting a realtime
  /// ticket, for instance — branch on this rather than inspecting the key itself.
  bool get isServerClient => _apiKey != null;

  late final AccountService account;
  late final TablesService tables;
  late final TeamsService teams;
  late final FunctionsService functions;
  late final StorageService storage;

  /// Sends one API call and returns the decoded JSON body (`null` for a 204 or an
  /// empty body). Every header/error-mapping rule lives here so service methods stay
  /// pure wire-shape translation.
  ///
  /// [bodyBytes]/[contentType] send a raw byte body instead of JSON — a storage
  /// upload, where the bytes *are* the request. Set at most one of [body] and
  /// [bodyBytes].
  Future<Map<String, dynamic>?> request({
    required String method,
    required String path,
    Map<String, List<String>> query = const {},
    Object? body,
    List<int>? bodyBytes,
    String? contentType,
  }) async {
    final response = await _send(
      method: method,
      path: path,
      query: query,
      body: body,
      bodyBytes: bodyBytes,
      contentType: contentType,
    );
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

  /// Sends one API call and returns the response body as raw bytes — a storage
  /// download, whose body is a file rather than JSON. Shares every header and
  /// error-mapping rule with [request]; only the success path differs.
  Future<List<int>> requestBytes({
    required String method,
    required String path,
    Map<String, List<String>> query = const {},
  }) async =>
      (await _send(method: method, path: path, query: query)).bodyBytes;

  Future<TransportResponse> _send({
    required String method,
    required String path,
    Map<String, List<String>> query = const {},
    Object? body,
    List<int>? bodyBytes,
    String? contentType,
  }) async {
    // An API-key client never touches the session store — not "prefers the key over a
    // session", but never reads one. Falling back to a session would make a server
    // client's identity depend on whatever happened to be persisted, which is how a
    // background job silently starts acting as the last user who signed in.
    final session = _apiKey == null ? await sessionStore.read() : null;
    final headers = <String, String>{
      'accept': 'application/json',
      'x-praxy-project': projectId,
      'x-praxy-key': ?_apiKey,
      if (session != null) 'x-praxy-session': session.secret,
    };

    TransportResponse response;
    try {
      response = await _transport.send(
        TransportRequest(
          method: method,
          path: path,
          headers: headers,
          query: query,
          body: body,
          bodyBytes: bodyBytes,
          contentType: contentType,
        ),
      );
    } on PraxyException {
      rethrow;
    } catch (error, stackTrace) {
      throw PraxyNetworkException(cause: error, stackTrace: stackTrace, isTimeout: error is TimeoutException);
    }

    if (response.statusCode >= 400) {
      throw _mapError(response);
    }
    return response;
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
