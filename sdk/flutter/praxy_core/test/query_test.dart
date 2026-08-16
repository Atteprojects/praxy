import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

const _title = Col<String>('title');
const _done = Col<bool>('done');
const _score = Col<int>('score');

void main() {
  group('Query.toJson', () {
    test('equal encodes method/attribute/values', () {
      expect(Query.equal(_title, 'Hello').toJson(), {
        'method': 'equal',
        'attribute': 'title',
        'values': ['Hello'],
      });
    });

    test('equalAny encodes multiple values under one attribute', () {
      expect(Query.equalAny(_score, [1, 2, 3]).toJson()['values'], [1, 2, 3]);
    });

    test('between encodes two values', () {
      expect(Query.between(_score, 1, 10).toJson(), {
        'method': 'between',
        'attribute': 'score',
        'values': [1, 10],
      });
    });

    test('isNull/isNotNull omit values entirely', () {
      expect(Query.isNull(_title).toJson(), {'method': 'isNull', 'attribute': 'title'});
      expect(Query.isNotNull(_title).toJson(), {'method': 'isNotNull', 'attribute': 'title'});
    });

    test('select encodes column names as values, no attribute', () {
      expect(Query.select([_title, _done]).toJson(), {
        'method': 'select',
        'values': ['title', 'done'],
      });
    });

    test('limit/offset/cursor take no attribute', () {
      expect(Query.limit(10).toJson(), {'method': 'limit', 'values': [10]});
      expect(Query.cursorAfter('abc').toJson(), {'method': 'cursorAfter', 'values': ['abc']});
    });

    test('and/or nest child query objects, not strings', () {
      final q = Query.and([Query.equal(_done, false), Query.greaterThan(_score, 5)]);
      expect(q.toJson(), {
        'method': 'and',
        'values': [
          {'method': 'equal', 'attribute': 'done', 'values': [false]},
          {'method': 'greaterThan', 'attribute': 'score', 'values': [5]},
        ],
      });
    });

    test('DateTime values encode as UTC ISO-8601', () {
      final dt = DateTime.utc(2026, 1, 2, 3, 4, 5);
      expect(Query.equal(Col<DateTime>('at'), dt).toJson()['values'], ['2026-01-02T03:04:05.000Z']);
    });

    test('encode() round-trips through jsonEncode', () {
      final encoded = Query.equal(_title, 'Hello').encode();
      expect(encoded, '{"method":"equal","attribute":"title","values":["Hello"]}');
    });

    test('raw() is a real Query value, not a string', () {
      final q = Query.raw('notBetween', attribute: 'score', values: [1, 10]);
      expect(q, isA<Query>());
      expect(q.toJson()['method'], 'notBetween');
    });
  });
}
