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
}
