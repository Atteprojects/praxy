// GENERATED — do not edit by hand.
//
// Produced by `dart run praxy_sdk_gen` from docs/openapi/v1.json. Edit the API's
// endpoint definitions (or the generator) and regenerate; `check:sdk-gen` fails the
// build if this file and the document disagree.

import '../../client.dart';
import '../../json_utils.dart';
import '../../models.dart';

/// Server-side app-user administration (`/v1/users`) — the surface an API key reaches and an end-user session never should. Create, inspect, update and delete a project's app users, and revoke their sessions.
///
/// Requires a client constructed with an `apiKey`; every method here needs the
/// `users.read`/`users.write` key scopes. GENERATED from the OpenAPI document.
final class UsersService {
  const UsersService(this._client);

  final Praxy _client;

  Future<AppUser> create({required String email, String? password, String? name}) async => AppUser.fromJson(
    requireJson(
      await _client.request(
        method: 'POST',
        path: '/v1/users',
        body: {'email': email, 'password': ?password, 'name': ?name},
      ),
      '/v1/users',
    ),
  );

  Future<void> delete(String userId) async {
    await _client.request(method: 'DELETE', path: '/v1/users/$userId');
  }

  Future<void> deleteAllSessions(String userId) async {
    await _client.request(method: 'DELETE', path: '/v1/users/$userId/sessions');
  }

  Future<void> deleteSession(String userId, String sessionId) async {
    await _client.request(method: 'DELETE', path: '/v1/users/$userId/sessions/$sessionId');
  }

  Future<AppUser> get(String userId) async => AppUser.fromJson(
    requireJson(await _client.request(method: 'GET', path: '/v1/users/$userId'), '/v1/users/{userId}'),
  );

  /// Known gap in the OpenAPI document this is generated from: `limit`, `offset` and `search` query parameters are not described, so this method cannot page or filter and always returns the server default page. `UsersServerEndpoints.List` reads them from `HttpContext` rather than binding them, which makes them invisible to OpenAPI generation.
  Future<AppUserList> list() async =>
      AppUserList.fromJson(requireJson(await _client.request(method: 'GET', path: '/v1/users'), '/v1/users'));

  Future<SessionList> listSessions(String userId) async => SessionList.fromJson(
    requireJson(
      await _client.request(method: 'GET', path: '/v1/users/$userId/sessions'),
      '/v1/users/{userId}/sessions',
    ),
  );

  Future<AppUser> updateEmail(String userId, {required String email}) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/users/$userId/email', body: {'email': email}),
      '/v1/users/{userId}/email',
    ),
  );

  Future<AppUser> updateLabels(String userId, {required List<String> labels}) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/users/$userId/labels', body: {'labels': labels}),
      '/v1/users/{userId}/labels',
    ),
  );

  Future<AppUser> updateName(String userId, {required String name}) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/users/$userId/name', body: {'name': name}),
      '/v1/users/{userId}/name',
    ),
  );

  Future<AppUser> updatePassword(String userId, {required String password}) async => AppUser.fromJson(
    requireJson(
      await _client.request(
        method: 'PATCH',
        path: '/v1/users/$userId/password',
        body: {'password': password},
      ),
      '/v1/users/{userId}/password',
    ),
  );

  Future<AppUser> updateStatus(String userId, {required bool status}) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/users/$userId/status', body: {'status': status}),
      '/v1/users/{userId}/status',
    ),
  );

  Future<AppUser> updateVerification(String userId, {required bool emailVerified}) async => AppUser.fromJson(
    requireJson(
      await _client.request(
        method: 'PATCH',
        path: '/v1/users/$userId/verification',
        body: {'emailVerified': emailVerified},
      ),
      '/v1/users/{userId}/verification',
    ),
  );
}

/// Wire shape of `AppUserListResponse`.
final class AppUserList {
  const AppUserList({required this.total, required this.users});

  final int total;
  final List<AppUser> users;

  factory AppUserList.fromJson(Map<String, dynamic> json) => AppUserList(
    total: (json['total'] is String ? int.parse(json['total'] as String) : json['total'] as int),
    users: [for (final e in (json['users'] as List).cast<Map<String, dynamic>>()) AppUser.fromJson(e)],
  );
}
