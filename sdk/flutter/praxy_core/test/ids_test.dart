import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

final _uuidV4 = RegExp(
  r'^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$',
);

void main() {
  group('Uid.unique', () {
    test('produces a dashed, lowercase, version-4 UUID', () {
      expect(Uid.unique(), matches(_uuidV4));
    });

    test('produces distinct values across calls', () {
      expect(Uid.unique(), isNot(Uid.unique()));
    });
  });

  test('Uid.custom is a pass-through', () {
    expect(Uid.custom('my-custom-id'), 'my-custom-id');
  });
}
