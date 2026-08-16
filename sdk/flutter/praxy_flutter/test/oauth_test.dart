import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_web_auth_2/flutter_web_auth_2.dart';
import 'package:praxy_flutter/praxy_flutter.dart';

final class _FakeTransport implements Transport {
  _FakeTransport(this.handler);
  final Future<TransportResponse> Function(TransportRequest) handler;
  final List<TransportRequest> requests = [];

  @override
  Future<TransportResponse> send(TransportRequest request) async {
    requests.add(request);
    return handler(request);
  }

  @override
  void close() {}
}

void main() {
  group('PraxyOAuth', () {
    test('signInWithGoogle builds the authorize URL and exchanges the callback', () async {
      String? capturedUrl;
      String? capturedScheme;
      late TransportRequest exchangeRequest;

      final client = Praxy(
        endpoint: 'https://api.example.com',
        projectId: 'proj1',
        transport: _FakeTransport((request) async {
          exchangeRequest = request;
          return TransportResponse(
            statusCode: 201,
            headers: const {},
            bodyBytes: '''
            {
              "user": {"id":"u1","email":"a@b.com","name":"A","emailVerified":true,"status":true,
                        "labels":[],"prefs":{},"createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z"},
              "session": {"id":"s1","userId":"u1","provider":"google","current":true,
                          "expiresAt":"2027-01-01T00:00:00Z","createdAt":"2026-01-01T00:00:00Z"},
              "token": "opaque"
            }
            '''.codeUnits,
          );
        }),
      );

      final oauth = PraxyOAuth(
        client,
        authenticator: ({required url, required callbackUrlScheme, options = const FlutterWebAuth2Options()}) async {
          capturedUrl = url;
          capturedScheme = callbackUrlScheme;
          return '$callbackUrlScheme://oauth/success?userId=u1&secret=wrapped-secret';
        },
      );

      final session = await oauth.signInWithGoogle(callbackUrlScheme: 'com.praxy.example');

      expect(capturedScheme, 'com.praxy.example');
      final parsed = Uri.parse(capturedUrl!);
      expect(parsed.path, '/v1/account/sessions/oauth2/google');
      expect(parsed.queryParameters['project'], 'proj1');
      expect(parsed.queryParameters['success'], 'com.praxy.example://oauth/success');
      expect(parsed.queryParameters['failure'], 'com.praxy.example://oauth/failure');

      expect(exchangeRequest.path, '/v1/account/sessions/token');
      expect(exchangeRequest.body, {'userId': 'u1', 'secret': 'wrapped-secret'});
      expect(session.token, 'opaque');
    });

    test('a failure-URL callback (provider error) throws PraxyAuthException', () async {
      final client = Praxy(endpoint: 'https://api.example.com', projectId: 'proj1');
      final oauth = PraxyOAuth(
        client,
        authenticator: ({required url, required callbackUrlScheme, options = const FlutterWebAuth2Options()}) async =>
            '$callbackUrlScheme://oauth/failure?error=user_oauth2_provider_error',
      );

      await expectLater(
        oauth.signInWithGoogle(callbackUrlScheme: 'com.praxy.example'),
        throwsA(isA<PraxyAuthException>().having((e) => e.type, 'type', 'user_oauth2_provider_error')),
      );
    });

    test('a callback missing userId/secret throws PraxyDecodeException', () async {
      final client = Praxy(endpoint: 'https://api.example.com', projectId: 'proj1');
      final oauth = PraxyOAuth(
        client,
        authenticator: ({required url, required callbackUrlScheme, options = const FlutterWebAuth2Options()}) async =>
            '$callbackUrlScheme://oauth/success',
      );

      await expectLater(
        oauth.signInWithGoogle(callbackUrlScheme: 'com.praxy.example'),
        throwsA(isA<PraxyDecodeException>()),
      );
    });
  });
}
