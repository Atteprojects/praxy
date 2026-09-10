import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

import 'support/fake_transport.dart';

Map<String, dynamic> _userJson({String id = 'u1'}) => {
  'id': id, 'email': 'a@b.com', 'name': 'A', 'emailVerified': false, 'status': true,
  'labels': const [], 'prefs': const {}, 'createdAt': '2026-01-01T00:00:00Z',
  'updatedAt': '2026-01-01T00:00:00Z',
};

/// The generated server surface, exercised rather than inspected. `praxy_sdk_gen`'s
/// own tests assert the emitted *text*; these assert that the emitted code actually
/// sends the right request and decodes the right response — which is the part a
/// template change can silently break while still looking correct in a diff.
Praxy _server(FakeTransport transport) =>
    Praxy(endpoint: 'https://example.test', projectId: 'proj1', apiKey: 'k.s', transport: transport);

void main() {
  test('list decodes into the generated AppUserList, reusing the hand-written AppUser', () {
    final transport = FakeTransport(
      (_) => jsonResponse(200, {'total': 2, 'users': [_userJson(), _userJson(id: 'u2')]}),
    );

    return _server(transport).users.list().then((page) {
      expect(page.total, 2);
      expect(page.users, hasLength(2));
      expect(page.users.first, isA<AppUser>());
      expect(page.users.last.id, 'u2');
      expect(transport.requests.single.method, 'GET');
      expect(transport.requests.single.path, '/v1/users');
    });
  });

  test('create omits an unset optional field entirely rather than sending null', () async {
    // WhenWritingNull on the server side has a mirror here: sending an explicit null
    // is not the same as omitting the key, and the generated `?name` spread is what
    // keeps them apart.
    final transport = FakeTransport((_) => jsonResponse(201, _userJson()));

    await _server(transport).users.create(email: 'a@b.com', password: 'hunter2');

    final body = transport.requests.single.body! as Map<String, dynamic>;
    expect(body, {'email': 'a@b.com', 'password': 'hunter2'});
    expect(body.containsKey('name'), isFalse);
  });

  test('a path parameter lands in the URL, not the body', () async {
    final transport = FakeTransport((_) => jsonResponse(200, _userJson()));

    await _server(transport).users.updateEmail('u1', email: 'new@b.com');

    expect(transport.requests.single.path, '/v1/users/u1/email');
    expect(transport.requests.single.body, {'email': 'new@b.com'});
  });

  test('a 204 method resolves without trying to decode a body', () async {
    final transport = FakeTransport((_) => emptyResponse(204));

    await _server(transport).users.deleteSession('u1', 's1');

    expect(transport.requests.single.method, 'DELETE');
    expect(transport.requests.single.path, '/v1/users/u1/sessions/s1');
  });

  test('sessions decode into the hand-written SessionList', () async {
    final transport = FakeTransport(
      (_) => jsonResponse(200, {
        'total': 1,
        'sessions': [
          {
            'id': 's1', 'userId': 'u1', 'provider': 'email', 'ip': null, 'userAgent': null,
            'current': true, 'expiresAt': '2027-01-01T00:00:00Z', 'createdAt': '2026-01-01T00:00:00Z',
          },
        ],
      }),
    );

    final sessions = await _server(transport).users.listSessions('u1');

    expect(sessions, isA<SessionList>());
    expect(sessions.sessions.single.id, 's1');
  });

  test('an error response still maps to the typed exception hierarchy', () async {
    // Generated methods funnel through Praxy.request like every hand-written one, so
    // they inherit error mapping rather than reimplementing it.
    final transport = FakeTransport(
      (_) => jsonResponse(401, {
        'message': 'API key required.', 'code': 401, 'type': 'unauthorized', 'version': '1',
        'requestId': 'r1',
      }),
    );

    await expectLater(
      _server(transport).users.list(),
      throwsA(isA<PraxyAuthException>()),
    );
  });
}
