import '../client.dart';
import '../json_utils.dart';
import '../models.dart';
import '../session_store.dart';

/// Teams and their memberships (`/v1/teams`) — the client-facing surface
/// `TeamEndpoints.cs` exposes to sessions and scoped API keys, not the separate
/// console-operator admin surface. 10 methods: team CRUD (5) plus membership CRUD
/// including invitation acceptance (5).
///
/// A session's authorization for most of these is membership-based (an `ownerOnly`
/// call like [update]/[delete]/[createMembership]/[updateMembershipRoles] needs the
/// caller to hold the team's `owner` role) rather than key-scope-based — this service
/// doesn't replicate that check client-side. A `401` from a non-owner, or a `404` on a
/// team the caller isn't a member of, is expected server behavior to let surface
/// through the usual [PraxyException] mapping, not a bug to route around.
final class TeamsService {
  const TeamsService(this._client);

  final Praxy _client;

  Future<Team> create({required String name, List<String>? roles}) async => Team.fromJson(
    requireJson(
      await _client.request(method: 'POST', path: '/v1/teams', body: {'name': name, 'roles': ?roles}),
      '/v1/teams',
    ),
  );

  Future<TeamList> list() async =>
      TeamList.fromJson(requireJson(await _client.request(method: 'GET', path: '/v1/teams'), '/v1/teams'));

  Future<Team> get(String teamId) async => Team.fromJson(
    requireJson(await _client.request(method: 'GET', path: '/v1/teams/$teamId'), teamId),
  );

  Future<Team> update(String teamId, {required String name}) async => Team.fromJson(
    requireJson(
      await _client.request(method: 'PATCH', path: '/v1/teams/$teamId', body: {'name': name}),
      teamId,
    ),
  );

  Future<void> delete(String teamId) async {
    await _client.request(method: 'DELETE', path: '/v1/teams/$teamId');
  }

  /// A session invitation requires [url] (the acceptance-email redirect target) and
  /// emails the invite; a key-driven call adds the member directly and ignores [url].
  /// Which happens is decided server-side by the caller's principal, not by this method.
  Future<Membership> createMembership(
    String teamId, {
    String? email,
    String? userId,
    List<String>? roles,
    String? url,
  }) async => Membership.fromJson(
    requireJson(
      await _client.request(
        method: 'POST',
        path: '/v1/teams/$teamId/memberships',
        body: {'email': ?email, 'userId': ?userId, 'roles': ?roles, 'url': ?url},
      ),
      teamId,
    ),
  );

  Future<MembershipList> listMemberships(String teamId) async => MembershipList.fromJson(
    requireJson(await _client.request(method: 'GET', path: '/v1/teams/$teamId/memberships'), teamId),
  );

  Future<Membership> updateMembershipRoles(
    String teamId,
    String membershipId, {
    required List<String> roles,
  }) async => Membership.fromJson(
    requireJson(
      await _client.request(
        method: 'PATCH',
        path: '/v1/teams/$teamId/memberships/$membershipId',
        body: {'roles': roles},
      ),
      membershipId,
    ),
  );

  /// Accepts an emailed invitation — authenticated by [secret], not by a session — and
  /// persists the resulting [Session] into the [SessionStore], the same way
  /// `account.create`/`createEmailSession` do, since accepting also signs the user in.
  Future<AcceptedMembership> acceptInvitation(
    String teamId,
    String membershipId, {
    required String userId,
    required String secret,
  }) async {
    final accepted = AcceptedMembership.fromJson(
      requireJson(
        await _client.request(
          method: 'PATCH',
          path: '/v1/teams/$teamId/memberships/$membershipId/status',
          body: {'userId': userId, 'secret': secret},
        ),
        membershipId,
      ),
    );
    await _client.sessionStore.write(
      Session(
        projectId: _client.projectId,
        userId: accepted.session.user.id,
        sessionId: accepted.session.session.id,
        secret: accepted.session.token,
        expiresAt: accepted.session.session.expiresAt,
      ),
    );
    return accepted;
  }

  /// Self-removal is always allowed; removing another member takes a team owner or a
  /// scoped key — enforced server-side, same as every other `ownerOnly` call here.
  Future<void> deleteMembership(String teamId, String membershipId) async {
    await _client.request(method: 'DELETE', path: '/v1/teams/$teamId/memberships/$membershipId');
  }
}
