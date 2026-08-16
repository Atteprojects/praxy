import '../client.dart';
import '../json_utils.dart';
import '../models.dart';
import '../session_store.dart';

/// The signed-in (or signing-in) app user's account surface (`/v1/account`).
///
/// Deliberately 8 methods, matching research/flutter-sdk.md's v1 surface exactly.
/// `createAnonymousSession` is *not* here even though the spec lists it: the actual
/// Phase 0-4 API never implemented anonymous sessions — CLAUDE.md's fixed decisions
/// lock app-user auth to email+password and Google OAuth only "until the owner says
/// otherwise," and no `/v1/account/sessions/anonymous`-shaped endpoint exists in
/// `AccountEndpoints.cs`. Documented as a deviation in the phase-5 report rather than
/// silently worked around.
final class AccountService {
  const AccountService(this._client);

  final Praxy _client;

  Future<AppUser> get() async =>
      AppUser.fromJson(requireJson(await _client.request(method: 'GET', path: '/v1/account'), '/v1/account'));

  Future<CreatedSession> create({required String email, required String password, String? name}) =>
      _createSession(
        path: '/v1/account',
        body: {'email': email, 'password': password, 'name': ?name},
      );

  Future<CreatedSession> createEmailSession({required String email, required String password}) =>
      _createSession(
        path: '/v1/account/sessions/email',
        body: {'email': email, 'password': password},
      );

  /// The universal token→session converging point (`POST /v1/account/sessions/token`)
  /// — today only OAuth lands here, driven by `praxy_flutter`'s Google sign-in flow,
  /// which hands this the `userId`/`secret` pair off the callback redirect.
  Future<CreatedSession> createOAuth2Session({required String userId, required String secret}) =>
      _createSession(
        path: '/v1/account/sessions/token',
        body: {'userId': userId, 'secret': secret},
      );

  /// Deletes a session (`"current"` by default) and clears the local [SessionStore]
  /// if the deleted session is the one currently stored.
  Future<void> deleteSession([String sessionId = 'current']) async {
    await _client.request(method: 'DELETE', path: '/v1/account/sessions/$sessionId');
    final stored = await _client.sessionStore.read();
    if (stored != null && (sessionId == 'current' || stored.sessionId == sessionId)) {
      await _client.sessionStore.clear();
    }
  }

  Future<AppUser> updatePrefs(Map<String, dynamic> prefs) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/account/prefs', body: {'prefs': prefs}),
      '/v1/account/prefs',
    ),
  );

  Future<void> createRecovery({required String email, required String url}) async {
    await _client.request(method: 'POST', path: '/v1/account/recovery', body: {'email': email, 'url': url});
  }

  Future<void> updateRecovery({required String userId, required String secret, required String password}) async {
    await _client.request(
      method: 'PUT',
      path: '/v1/account/recovery',
      body: {'userId': userId, 'secret': secret, 'password': password},
    );
  }

  Future<CreatedSession> _createSession({required String path, required Map<String, dynamic> body}) async {
    final created = CreatedSession.fromJson(requireJson(
      await _client.request(method: 'POST', path: path, body: body),
      path,
    ));
    await _client.sessionStore.write(Session(
      projectId: _client.projectId,
      userId: created.user.id,
      sessionId: created.session.id,
      secret: created.token,
      expiresAt: created.session.expiresAt,
    ));
    return created;
  }
}
