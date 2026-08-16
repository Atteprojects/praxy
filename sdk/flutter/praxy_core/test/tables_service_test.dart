import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

import 'support/fake_transport.dart';

final class Todo {
  const Todo({required this.title, required this.done});
  final String title;
  final bool done;

  static Todo _decode(Map<String, dynamic> data, RowMeta meta) =>
      Todo(title: data['title'] as String, done: data['done'] as bool);
  static Map<String, dynamic> _encode(Todo value) => {'title': value.title, 'done': value.done};

  static const codec = RowCodec<Todo>(decode: _decode, encode: _encode);
}

const _todos = TableRef<Todo>('db1', 'todos', codec: Todo.codec);

Map<String, dynamic> _rowJson({String id = 'row1', String title = 'Buy milk', bool done = false}) => {
  r'$id': id,
  r'$tableId': 'todos',
  r'$databaseId': 'db1',
  r'$createdAt': '2026-01-01T00:00:00.000Z',
  r'$updatedAt': '2026-01-01T00:00:00.000Z',
  r'$permissions': ['read("any")'],
  'title': title,
  'done': done,
};

void main() {
  test('create() sends data through the codec and decodes the typed row back', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(201, _rowJson());
      }),
    );

    final todo = await client.tables.create(_todos, data: const Todo(title: 'Buy milk', done: false));

    expect(captured.method, 'POST');
    expect(captured.path, '/v1/databases/db1/tables/todos/rows');
    expect(captured.body, {'data': {'title': 'Buy milk', 'done': false}});
    expect(todo.title, 'Buy milk');
    expect(todo.done, isFalse);
  });

  test('create() with an explicit rowId sends it on the wire', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(201, _rowJson());
      }),
    );
    await client.tables.create(_todos, rowId: 'my-id', data: const Todo(title: 'x', done: false));
    expect(captured.body, {
      'rowId': 'my-id',
      'data': {'title': 'x', 'done': false},
    });
  });

  test('list() sends each Query as one queries[] entry and decodes every row', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, {
          'total': 2,
          'rows': [_rowJson(id: 'row1'), _rowJson(id: 'row2', title: 'Buy eggs')],
        });
      }),
    );

    final page = await client.tables.list(
      _todos,
      queries: [Query.equal(const Col<bool>('done'), false), Query.limit(10)],
    );

    expect(captured.query['queries[]'], [
      '{"method":"equal","attribute":"done","values":[false]}',
      '{"method":"limit","values":[10]}',
    ]);
    expect(page.total, 2);
    expect(page.rows.map((t) => t.title), ['Buy milk', 'Buy eggs']);
  });

  test('list(total: false) sends total=false', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, {'total': null, 'rows': []});
      }),
    );
    await client.tables.list(_todos, total: false);
    expect(captured.query['total'], ['false']);
  });

  test('update() sends only the provided fields as a partial patch', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, _rowJson(done: true));
      }),
    );
    final todo = await client.tables.update(_todos, 'row1', data: {'done': true});
    expect(captured.method, 'PATCH');
    expect(captured.path, '/v1/databases/db1/tables/todos/rows/row1');
    expect(captured.body, {'data': {'done': true}});
    expect(todo.done, isTrue);
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
    await client.tables.delete(_todos, 'row1');
    expect(captured.method, 'DELETE');
    expect(captured.path, '/v1/databases/db1/tables/todos/rows/row1');
  });

  test('a row that fails to decode raises PraxyDecodeException, not a raw TypeError', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(200, {
        r'$id': 'row1', r'$tableId': 'todos', r'$databaseId': 'db1',
        r'$createdAt': '2026-01-01T00:00:00.000Z', r'$updatedAt': '2026-01-01T00:00:00.000Z',
        r'$permissions': <String>[],
        // 'title' missing entirely -> Todo._decode's cast throws.
        'done': false,
      })),
    );
    await expectLater(client.tables.get(_todos, 'row1'), throwsA(isA<PraxyDecodeException>()));
  });
}
