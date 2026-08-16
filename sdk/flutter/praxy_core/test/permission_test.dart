import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

void main() {
  group('Permission', () {
    test('formats action("role") verbatim', () {
      expect(Permission.read('any'), 'read("any")');
      expect(Permission.write('users'), 'write("users")');
    });
  });

  group('Role', () {
    test('any/guests/users', () {
      expect(Role.any(), 'any');
      expect(Role.guests(), 'guests');
      expect(Role.users(), 'users');
      expect(Role.users(verified: true), 'users/verified');
    });

    test('user with and without verified suffix', () {
      expect(Role.user('abc123'), 'user:abc123');
      expect(Role.user('abc123', verified: true), 'user:abc123/verified');
    });

    test('team with and without a role suffix', () {
      expect(Role.team('t1'), 'team:t1');
      expect(Role.team('t1', 'editor'), 'team:t1/editor');
    });

    test('member and label', () {
      expect(Role.member('m1'), 'member:m1');
      expect(Role.label('vip'), 'label:vip');
    });
  });
}
