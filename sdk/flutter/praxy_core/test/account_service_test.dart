import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

import 'support/fake_transport.dart';

Map<String, dynamic> _createdSessionJson({String sessionId = 's1'}) => {
  'user': {
    'id': 'u1', 'email': 'a@b.com', 'name': 'A', 'emailVerified': false, 'status': true,
    'labels': [], 'prefs': {}, 'createdAt': '2026-01-01T00:00:00Z', 'updatedAt': '2026-01-01T00:00:00Z',
  },
  'session': {
    'id': sessionId, 'userId': 'u1', 'provider': 'email', 'ip': null, 'userAgent': null,
    'current': true, 'expiresAt': '2027-01-01T00:00:00Z', 'createdAt': '2026-01-01T00:00:00Z',
  },
  'token': 'opaque-token',
};

Map<String, dynamic> _appUserJson() => {
  'id': 'u1', 'email': 'a@b.com', 'name': 'A', 'emailVerified': false, 'status': true,
  'labels': [], 'prefs': {}, 'createdAt': '2026-01-01T00:00:00Z', 'updatedAt': '2026-01-01T00:00:00Z',
};

void main() {
  test('create() persists a Session into the SessionStore', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(201, _createdSessionJson())),
    );

    final created = await client.account.create(email: 'a@b.com', password: 'hunter2');
    expect(created.token, 'opaque-token');

    final stored = await client.sessionStore.read();
    expect(stored, isNotNull);
    expect(stored!.secret, 'opaque-token');
    expect(stored.userId, 'u1');
    expect(stored.sessionId, 's1');
    expect(stored.projectId, 'proj1');
  });

  test('createEmailSession posts to /v1/account/sessions/email', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(201, _createdSessionJson());
      }),
    );
    await client.account.createEmailSession(email: 'a@b.com', password: 'hunter2');
    expect(captured.method, 'POST');
    expect(captured.path, '/v1/account/sessions/email');
    expect(captured.body, {'email': 'a@b.com', 'password': 'hunter2'});
  });

  test('deleteSession("current") clears the stored session', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => emptyResponse(204)),
    );
    await client.sessionStore.write(Session(
      projectId: 'proj1', userId: 'u1', sessionId: 's1', secret: 'tok', expiresAt: DateTime.now(),
    ));

    await client.account.deleteSession();

    expect(await client.sessionStore.read(), isNull);
  });

  test('deleteSession(otherId) leaves an unrelated stored session alone', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => emptyResponse(204)),
    );
    await client.sessionStore.write(Session(
      projectId: 'proj1', userId: 'u1', sessionId: 's1', secret: 'tok', expiresAt: DateTime.now(),
    ));

    await client.account.deleteSession('s2');

    expect(await client.sessionStore.read(), isNotNull);
  });

  test('createOAuth2Session posts userId/secret to the token-exchange endpoint', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(201, _createdSessionJson());
      }),
    );
    await client.account.createOAuth2Session(userId: 'u1', secret: 'wrapped-secret');
    expect(captured.path, '/v1/account/sessions/token');
    expect(captured.body, {'userId': 'u1', 'secret': 'wrapped-secret'});
  });

  test('updateName() PATCHes /v1/account/name and decodes the returned AppUser', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, {..._appUserJson(), 'name': 'New Name'});
      }),
    );
    final user = await client.account.updateName(name: 'New Name');
    expect(captured.method, 'PATCH');
    expect(captured.path, '/v1/account/name');
    expect(captured.body, {'name': 'New Name'});
    expect(user.name, 'New Name');
  });

  test('updatePassword() sends password and oldPassword when given', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, _appUserJson());
      }),
    );
    await client.account.updatePassword(password: 'new-pw', oldPassword: 'old-pw');
    expect(captured.method, 'PATCH');
    expect(captured.path, '/v1/account/password');
    expect(captured.body, {'password': 'new-pw', 'oldPassword': 'old-pw'});
  });

  test('updatePassword() omits oldPassword from the body when not given', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, _appUserJson());
      }),
    );
    await client.account.updatePassword(password: 'new-pw');
    expect(captured.body, {'password': 'new-pw'});
  });

  test('listSessions() decodes total and each session, surfacing current', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(200, {
        'total': 2,
        'sessions': [
          {
            'id': 's1', 'userId': 'u1', 'provider': 'email', 'ip': null, 'userAgent': null,
            'current': true, 'expiresAt': '2027-01-01T00:00:00Z', 'createdAt': '2026-01-01T00:00:00Z',
          },
          {
            'id': 's2', 'userId': 'u1', 'provider': 'google', 'ip': '1.2.3.4', 'userAgent': 'ua',
            'current': false, 'expiresAt': '2027-01-01T00:00:00Z', 'createdAt': '2026-01-01T00:00:00Z',
          },
        ],
      })),
    );
    final list = await client.account.listSessions();
    expect(list.total, 2);
    expect(list.sessions.map((s) => s.id), ['s1', 's2']);
    expect(list.sessions[0].current, isTrue);
    expect(list.sessions[1].current, isFalse);
  });

  test('sendVerification() POSTs the url and returns void on 204', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return emptyResponse(204);
      }),
    );
    await client.account.sendVerification(url: 'https://app.example/verify');
    expect(captured.method, 'POST');
    expect(captured.path, '/v1/account/verification');
    expect(captured.body, {'url': 'https://app.example/verify'});
  });

  test('confirmVerification() PUTs userId/secret and decodes the returned AppUser', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, {..._appUserJson(), 'emailVerified': true});
      }),
    );
    final user = await client.account.confirmVerification(userId: 'u1', secret: 'sekret');
    expect(captured.method, 'PUT');
    expect(captured.path, '/v1/account/verification');
    expect(captured.body, {'userId': 'u1', 'secret': 'sekret'});
    expect(user.emailVerified, isTrue);
  });

  test('roles() decodes roles/principal/scopes', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(200, {
        'roles': ['any', 'user:u1'],
        'principal': 'user',
        'scopes': null,
      })),
    );
    final resolved = await client.account.roles();
    expect(resolved.roles, ['any', 'user:u1']);
    expect(resolved.principal, 'user');
    expect(resolved.scopes, isNull);
  });

  test('createJwt() posts durationSeconds when given and returns the jwt string', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, {'jwt': 'header.payload.sig'});
      }),
    );
    final jwt = await client.account.createJwt(durationSeconds: 60);
    expect(captured.method, 'POST');
    expect(captured.path, '/v1/account/jwts');
    expect(captured.body, {'durationSeconds': 60});
    expect(jwt, 'header.payload.sig');
  });

  test('createJwt() omits durationSeconds from the body when not given', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, {'jwt': 'header.payload.sig'});
      }),
    );
    await client.account.createJwt();
    expect(captured.body, <String, dynamic>{});
  });
}
