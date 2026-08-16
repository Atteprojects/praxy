/// A signed-in app user's session, as persisted by a [SessionStore]. Praxy uses a
/// token flow — never a cookie jar — so this is the entire session-restore payload.
final class Session {
  const Session({
    required this.projectId,
    required this.userId,
    required this.sessionId,
    required this.secret,
    required this.expiresAt,
  });

  final String projectId;
  final String userId;
  final String sessionId;

  /// The opaque secret sent as `X-Praxy-Session`.
  final String secret;
  final DateTime expiresAt;
}

/// The session-persistence seam. `praxy_core` ships [MemorySessionStore] (tests,
/// server-side use); `praxy_flutter` ships `SecureSessionStore` (Keychain/Keystore).
abstract interface class SessionStore {
  Future<Session?> read();
  Future<void> write(Session session);
  Future<void> clear();
}

/// Process-lifetime only — nothing survives an app restart. The right default for
/// server-side Dart and for tests; Flutter apps should use `SecureSessionStore`.
final class MemorySessionStore implements SessionStore {
  Session? _session;

  @override
  Future<Session?> read() async => _session;

  @override
  Future<void> write(Session session) async => _session = session;

  @override
  Future<void> clear() async => _session = null;
}
