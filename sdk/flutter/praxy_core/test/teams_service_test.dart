import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

import 'support/fake_transport.dart';

Map<String, dynamic> _teamJson({String id = 't1', String name = 'Engineering', int memberCount = 1}) => {
  'id': id, 'name': name, 'memberCount': memberCount, 'createdAt': '2026-01-01T00:00:00Z',
};

Map<String, dynamic> _membershipJson({
  String id = 'm1',
  String teamId = 't1',
  bool confirmed = true,
  String? invitedAt,
  String? joinedAt = '2026-01-02T00:00:00Z',
}) => {
  'id': id, 'teamId': teamId, 'userId': 'u1', 'userEmail': 'a@b.com', 'userName': 'A',
  'roles': ['member'], 'confirmed': confirmed, 'invitedAt': invitedAt, 'joinedAt': joinedAt,
};

void main() {
  test('create() posts name/roles and decodes the returned Team', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(201, _teamJson());
      }),
    );
    final team = await client.teams.create(name: 'Engineering', roles: const ['owner']);
    expect(captured.method, 'POST');
    expect(captured.path, '/v1/teams');
    expect(captured.body, {'name': 'Engineering', 'roles': ['owner']});
    expect(team.id, 't1');
    expect(team.memberCount, 1);
  });

  test('create() omits roles from the body when not given', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(201, _teamJson());
      }),
    );
    await client.teams.create(name: 'Engineering');
    expect(captured.body, {'name': 'Engineering'});
  });

  test('list() decodes total and every team', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(200, {
        'total': 2,
        'teams': [_teamJson(id: 't1'), _teamJson(id: 't2', name: 'Ops')],
      })),
    );
    final list = await client.teams.list();
    expect(list.total, 2);
    expect(list.teams.map((t) => t.id), ['t1', 't2']);
  });

  test('get() fetches by id', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, _teamJson());
      }),
    );
    final team = await client.teams.get('t1');
    expect(captured.method, 'GET');
    expect(captured.path, '/v1/teams/t1');
    expect(team.name, 'Engineering');
  });

  test('update() PATCHes the new name', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, _teamJson(name: 'Renamed'));
      }),
    );
    final team = await client.teams.update('t1', name: 'Renamed');
    expect(captured.method, 'PATCH');
    expect(captured.path, '/v1/teams/t1');
    expect(captured.body, {'name': 'Renamed'});
    expect(team.name, 'Renamed');
  });

  test('delete() issues a DELETE with no body', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return emptyResponse(204);
      }),
    );
    await client.teams.delete('t1');
    expect(captured.method, 'DELETE');
    expect(captured.path, '/v1/teams/t1');
  });

  test('createMembership() sends email/userId/roles/url and decodes the Membership', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(201, _membershipJson(confirmed: false, invitedAt: '2026-01-01T00:00:00Z', joinedAt: null));
      }),
    );
    final membership = await client.teams.createMembership(
      't1',
      email: 'new@b.com',
      roles: const ['member'],
      url: 'https://app.example/accept',
    );
    expect(captured.method, 'POST');
    expect(captured.path, '/v1/teams/t1/memberships');
    expect(captured.body, {
      'email': 'new@b.com', 'roles': ['member'], 'url': 'https://app.example/accept',
    });
    expect(membership.confirmed, isFalse);
    expect(membership.invitedAt, isNotNull);
    expect(membership.joinedAt, isNull);
  });

  test('listMemberships() decodes total and every membership', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(200, {
        'total': 1,
        'memberships': [_membershipJson()],
      })),
    );
    final list = await client.teams.listMemberships('t1');
    expect(list.total, 1);
    expect(list.memberships.single.id, 'm1');
  });

  test('updateMembershipRoles() PATCHes the new roles list', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, _membershipJson());
      }),
    );
    await client.teams.updateMembershipRoles('t1', 'm1', roles: const ['owner', 'member']);
    expect(captured.method, 'PATCH');
    expect(captured.path, '/v1/teams/t1/memberships/m1');
    expect(captured.body, {'roles': ['owner', 'member']});
  });

  test('acceptInvitation() PATCHes the status endpoint and persists the returned Session', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, {
          'membership': _membershipJson(),
          'session': {
            'user': {
              'id': 'u1', 'email': 'a@b.com', 'name': 'A', 'emailVerified': true, 'status': true,
              'labels': [], 'prefs': {}, 'createdAt': '2026-01-01T00:00:00Z', 'updatedAt': '2026-01-01T00:00:00Z',
            },
            'session': {
              'id': 's1', 'userId': 'u1', 'provider': 'email', 'ip': null, 'userAgent': null,
              'current': true, 'expiresAt': '2027-01-01T00:00:00Z', 'createdAt': '2026-01-01T00:00:00Z',
            },
            'token': 'accept-token',
          },
        });
      }),
    );
    final accepted = await client.teams.acceptInvitation('t1', 'm1', userId: 'u1', secret: 'sekret');
    expect(captured.method, 'PATCH');
    expect(captured.path, '/v1/teams/t1/memberships/m1/status');
    expect(captured.body, {'userId': 'u1', 'secret': 'sekret'});
    expect(accepted.membership.id, 'm1');
    expect(accepted.session.token, 'accept-token');

    final stored = await client.sessionStore.read();
    expect(stored, isNotNull);
    expect(stored!.secret, 'accept-token');
    expect(stored.userId, 'u1');
    expect(stored.sessionId, 's1');
  });

  test('deleteMembership() issues a DELETE with no body', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return emptyResponse(204);
      }),
    );
    await client.teams.deleteMembership('t1', 'm1');
    expect(captured.method, 'DELETE');
    expect(captured.path, '/v1/teams/t1/memberships/m1');
  });

  test('a 401 from a non-owner ownerOnly call maps to PraxyAuthException', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(401, {
        'message': 'This action requires the team\'s owner role.',
        'code': 401, 'type': 'general_unauthorized', 'version': '0.1.0', 'requestId': 'req-1',
      })),
    );
    await expectLater(
      client.teams.update('t1', name: 'x'),
      throwsA(isA<PraxyAuthException>()),
    );
  });
}
