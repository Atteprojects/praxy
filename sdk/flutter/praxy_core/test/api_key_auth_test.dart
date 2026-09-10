import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

import 'support/fake_transport.dart';

/// Server-mode authentication: `apiKey` makes [Praxy] authenticate as the project
/// rather than as an end user. The tests that matter here are the negative ones — a
/// server client must never quietly borrow a session.
void main() {
  test('sends the API key as x-praxy-key', () async {
    final transport = FakeTransport((_) => jsonResponse(200, {'total': 0, 'users': []}));
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      apiKey: 'key1.secret',
      transport: transport,
    );

    await client.request(method: 'GET', path: '/v1/users');

    expect(transport.requests.single.headers['x-praxy-key'], 'key1.secret');
    expect(transport.requests.single.headers['x-praxy-project'], 'proj1');
  });

  test('a session client sends no API key header at all', () async {
    final transport = FakeTransport((_) => jsonResponse(200, const <String, dynamic>{}));
    final client = Praxy(endpoint: 'https://example.test', projectId: 'proj1', transport: transport);

    await client.request(method: 'GET', path: '/v1/account');

    expect(transport.requests.single.headers.containsKey('x-praxy-key'), isFalse);
  });

  test('a server client ignores a stored session instead of falling back to it', () async {
    // The failure this prevents: a background job authenticating as whichever end user
    // happened to be persisted in the session store, rather than as the project. The
    // header assertion is the point — "prefers the key" would still send both.
    final store = MemorySessionStore();
    await store.write(
      Session(
        secret: 'someones-session',
        userId: 'u1',
        sessionId: 's1',
        projectId: 'proj1',
        expiresAt: DateTime.utc(2030),
      ),
    );
    final transport = FakeTransport((_) => jsonResponse(200, const <String, dynamic>{}));
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      apiKey: 'key1.secret',
      transport: transport,
      sessionStore: store,
    );

    await client.request(method: 'GET', path: '/v1/users');

    expect(transport.requests.single.headers['x-praxy-key'], 'key1.secret');
    expect(transport.requests.single.headers.containsKey('x-praxy-session'), isFalse);
  });

  test('isServerClient distinguishes the two modes', () {
    final server = Praxy(endpoint: 'https://example.test', projectId: 'p', apiKey: 'k.s');
    final user = Praxy(endpoint: 'https://example.test', projectId: 'p');

    expect(server.isServerClient, isTrue);
    expect(user.isServerClient, isFalse);
  });
}
