import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

import 'support/fake_transport.dart';

Praxy _clientWith(ResponseBuilder handler) =>
    Praxy(endpoint: 'https://example.test', projectId: 'proj1', transport: FakeTransport(handler));

Map<String, dynamic> _envelope({
  String message = 'nope',
  int code = 400,
  String type = 'general_argument_invalid',
  String requestId = 'req-1',
  Map<String, List<String>>? fields,
}) => {
  'message': message,
  'code': code,
  'type': type,
  'version': '0.1.0',
  'requestId': requestId,
  'fields': ?fields,
};

void main() {
  group('error envelope mapping', () {
    test('401 maps to PraxyAuthException', () async {
      final client = _clientWith((_) => jsonResponse(401, _envelope(type: 'general_unauthorized')));
      await expectLater(
        client.account.get(),
        throwsA(isA<PraxyAuthException>().having((e) => e.type, 'type', 'general_unauthorized')),
      );
    });

    test('404 maps to PraxyNotFoundException', () async {
      final client = _clientWith((_) => jsonResponse(404, _envelope(type: 'row_not_found', code: 404)));
      await expectLater(client.account.get(), throwsA(isA<PraxyNotFoundException>()));
    });

    test('409 maps to PraxyConflictException', () async {
      final client = _clientWith((_) => jsonResponse(409, _envelope(type: 'row_already_exists', code: 409)));
      await expectLater(client.account.get(), throwsA(isA<PraxyConflictException>()));
    });

    test('429 maps to PraxyRateLimitException with Retry-After', () async {
      final client = _clientWith(
        (_) => jsonResponse(
          429,
          _envelope(type: 'general_rate_limit_exceeded', code: 429),
          headers: {'retry-after': '30'},
        ),
      );
      await expectLater(
        client.account.get(),
        throwsA(
          isA<PraxyRateLimitException>().having((e) => e.retryAfter, 'retryAfter', const Duration(seconds: 30)),
        ),
      );
    });

    test('400 with fields maps to PraxyValidationException', () async {
      final client = _clientWith(
        (_) => jsonResponse(
          400,
          _envelope(fields: {'email': ['must be a valid email']}),
        ),
      );
      await expectLater(
        client.account.get(),
        throwsA(
          isA<PraxyValidationException>().having(
            (e) => e.fields,
            'fields',
            {'email': ['must be a valid email']},
          ),
        ),
      );
    });

    test('400 without fields falls back to the base PraxyApiException', () async {
      final client = _clientWith((_) => jsonResponse(400, _envelope()));
      await expectLater(
        client.account.get(),
        throwsA(isA<PraxyApiException>().having((e) => e.runtimeType, 'exact type', PraxyApiException)),
      );
    });

    test('a malformed error body still produces a usable exception', () async {
      final client = _clientWith((_) => TransportResponse(statusCode: 500, headers: const {}, bodyBytes: [0xff, 0xfe]));
      await expectLater(client.account.get(), throwsA(isA<PraxyApiException>()));
    });

    test('a transport-level failure maps to PraxyNetworkException, preserving the cause', () async {
      final cause = Exception('connection refused');
      final client = _clientWith((_) => throw cause);
      await expectLater(
        client.account.get(),
        throwsA(isA<PraxyNetworkException>().having((e) => e.cause, 'cause', cause)),
      );
    });

    test('a 204/empty success body decodes as null without throwing', () async {
      final client = _clientWith((_) => emptyResponse(204));
      await client.account.deleteSession();
      // No throw is the assertion — deleteSession() returns void on success.
    });
  });

  test('requests carry X-Praxy-Project and, once a session exists, X-Praxy-Session', () async {
    late TransportRequest captured;
    final transport = FakeTransport((request) {
      captured = request;
      return jsonResponse(200, {
        'id': 'u1', 'email': 'a@b.com', 'name': 'A', 'emailVerified': true, 'status': true,
        'labels': [], 'prefs': {}, 'createdAt': '2026-01-01T00:00:00Z', 'updatedAt': '2026-01-01T00:00:00Z',
      });
    });
    final client = Praxy(endpoint: 'https://example.test', projectId: 'proj1', transport: transport);
    await client.account.get();
    expect(captured.headers['x-praxy-project'], 'proj1');
    expect(captured.headers.containsKey('x-praxy-session'), isFalse);

    await client.sessionStore.write(Session(
      projectId: 'proj1', userId: 'u1', sessionId: 's1', secret: 'sekret', expiresAt: DateTime.now(),
    ));
    await client.account.get();
    expect(captured.headers['x-praxy-session'], 'sekret');
  });
}
