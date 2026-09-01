import 'dart:convert';
import 'dart:io';

import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:praxy_codegen/praxy_codegen.dart';
import 'package:test/test.dart';

http.Response _json(Object body) => http.Response(jsonEncode(body), 200);

void main() {
  test('generate() resolves database/table by key and writes typed Col constants', () async {
    final client = MockClient((request) async {
      expect(request.headers['x-praxy-project'], 'proj1');
      expect(request.headers['x-praxy-key'], 'key1');

      if (request.url.path == '/v1/databases') {
        return _json({
          'total': 1,
          'databases': [
            {'id': 'db1id', 'key': 'main', 'name': 'Main', 'createdAt': '2026-01-01T00:00:00Z'},
          ],
        });
      }
      if (request.url.path == '/v1/databases/db1id/tables') {
        return _json({
          'total': 1,
          'tables': [
            {
              'id': 'tbl1id', 'databaseId': 'db1id', 'key': 'todos', 'name': 'Todos',
              'rowSecurity': false, 'enabled': true,
              'createdAt': '2026-01-01T00:00:00Z', 'updatedAt': '2026-01-01T00:00:00Z',
            },
          ],
        });
      }
      if (request.url.path == '/v1/databases/db1id/tables/tbl1id/columns') {
        return _json({
          'total': 2,
          'columns': [
            {
              'id': 'c1', 'tableId': 'tbl1id', 'key': 'title', 'type': 'string', 'required': true,
              'array': false, 'size': 255, 'status': 'available', 'position': 0,
              'createdAt': '2026-01-01T00:00:00Z', 'updatedAt': '2026-01-01T00:00:00Z',
            },
            {
              'id': 'c2', 'tableId': 'tbl1id', 'key': 'tags', 'type': 'string', 'required': false,
              'array': true, 'size': 64, 'status': 'available', 'position': 1,
              'createdAt': '2026-01-01T00:00:00Z', 'updatedAt': '2026-01-01T00:00:00Z',
            },
            {
              'id': 'c3', 'tableId': 'tbl1id', 'key': 'author_id', 'type': 'relationship',
              'required': false, 'array': false, 'targetTableId': 'usersid', 'status': 'available',
              'position': 2, 'createdAt': '2026-01-01T00:00:00Z', 'updatedAt': '2026-01-01T00:00:00Z',
            },
          ],
        });
      }
      return http.Response('not found', 404);
    });

    final dir = await Directory.systemTemp.createTemp('praxy_codegen_test');
    final outputPath = '${dir.path}/todos_columns.dart';
    addTearDown(() => dir.delete(recursive: true));

    await generate(
      CodegenOptions(
        endpoint: 'http://localhost:5090',
        projectId: 'proj1',
        apiKey: 'key1',
        databaseKey: 'main',
        tableKey: 'todos',
        outputPath: outputPath,
      ),
      client: client,
    );

    final content = await File(outputPath).readAsString();
    expect(content, contains('abstract final class TodosColumns {'));
    expect(content, contains(r"static const id = Col<String>(r'$id');"));
    expect(content, contains("static const title = Col<String>('title');"));
    expect(content, contains("static const tags = Col<List<String>>('tags');"));
    // relationship: raw id passthrough (String/List<String>) — no cross-table graph awareness,
    // out of scope for the whole relationships feature (docs/research/table-relationships.md).
    expect(content, contains("static const authorId = Col<String>('author_id');"));
  });

  test('a database key with no match throws CodegenException', () async {
    final client = MockClient((request) async => _json({'total': 0, 'databases': []}));
    await expectLater(
      generate(
        CodegenOptions(
          endpoint: 'http://localhost:5090',
          projectId: 'proj1',
          apiKey: 'key1',
          databaseKey: 'missing',
          tableKey: 'todos',
          outputPath: '${Directory.systemTemp.path}/unused.dart',
        ),
        client: client,
      ),
      throwsA(isA<CodegenException>()),
    );
  });
}
