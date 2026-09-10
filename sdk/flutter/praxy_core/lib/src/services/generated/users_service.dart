// GENERATED — do not edit by hand.
//
// Produced by `node sdk/generator/bin/generate.mjs` from docs/openapi/v1.json. Edit the API's
// endpoint definitions (or the generator) and regenerate; CI fails the build if this file and the
// document disagree.

import '../../client.dart';
import '../../json_utils.dart';
import '../../models.dart';

/// Server-side app-user administration (`/v1/users`) — the surface an API key reaches and an end-user session never should. Create, inspect, update and delete a project's app users, and revoke their sessions.
///
/// Requires a client authenticated with an API key; every method here needs the `users.read`/`users.write` key scopes.
///
/// GENERATED from the OpenAPI document — see sdk/generator.
final class UsersService {
  const UsersService(this._client);

  final Praxy _client;

  /// Creates an app user directly, without the sign-up flow or an email verification step.
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

  /// Deletes an app user and everything scoped to them, including their sessions.
  Future<void> delete(String userId) async {
    await _client.request(method: 'DELETE', path: '/v1/users/$userId');
  }

  /// Revokes every session an app user holds, signing them out everywhere.
  Future<void> deleteAllSessions(String userId) async {
    await _client.request(method: 'DELETE', path: '/v1/users/$userId/sessions');
  }

  /// Revokes one of an app user's sessions.
  Future<void> deleteSession(String userId, String sessionId) async {
    await _client.request(method: 'DELETE', path: '/v1/users/$userId/sessions/$sessionId');
  }

  /// Fetches one app user by id.
  Future<AppUser> get(String userId) async => AppUser.fromJson(
    requireJson(await _client.request(method: 'GET', path: '/v1/users/$userId'), '/v1/users/{userId}'),
  );

  /// Lists the project's app users, newest first.
  Future<AppUserList> list({int? limit, int? offset, String? search}) async => AppUserList.fromJson(
    requireJson(
      await _client.request(
        method: 'GET',
        path: '/v1/users',
        query: {
          if (limit != null) 'limit': ['$limit'],
          if (offset != null) 'offset': ['$offset'],
          if (search != null) 'search': [search],
        },
      ),
      '/v1/users',
    ),
  );

  /// Lists an app user's active sessions.
  Future<SessionList> listSessions(String userId) async => SessionList.fromJson(
    requireJson(
      await _client.request(method: 'GET', path: '/v1/users/$userId/sessions'),
      '/v1/users/{userId}/sessions',
    ),
  );

  /// Mirrors the console's change-email: the address moves and verified-ness resets with it. A collision inside the project is the existing user_already_exists, not a 500.
  Future<AppUser> updateEmail(String userId, {required String email}) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/users/$userId/email', body: {'email': email}),
      '/v1/users/{userId}/email',
    ),
  );

  /// Replaces an app user's labels wholesale. Labels are the operator-assigned strings the permission engine can grant roles from.
  Future<AppUser> updateLabels(String userId, {required List<String> labels}) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/users/$userId/labels', body: {'labels': labels}),
      '/v1/users/{userId}/labels',
    ),
  );

  /// Changes an app user's display name.
  Future<AppUser> updateName(String userId, {required String name}) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/users/$userId/name', body: {'name': name}),
      '/v1/users/{userId}/name',
    ),
  );

  /// Sets a password without the old one — and revokes every session, as the console does.
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

  /// Enables or disables an app user. A disabled user keeps their data but cannot authenticate.
  Future<AppUser> updateStatus(String userId, {required bool status}) async => AppUser.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/users/$userId/status', body: {'status': status}),
      '/v1/users/{userId}/status',
    ),
  );

  /// Marks an app user's email verified or unverified without sending them anything.
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
