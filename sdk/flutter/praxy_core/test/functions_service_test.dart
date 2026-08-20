import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

import 'support/fake_transport.dart';

Map<String, dynamic> _executionJson({
  String id = 'e1',
  String status = 'completed',
  int? statusCode = 200,
  String? responseBody = '{"ok":true}',
  String? triggeredBy = 'user:u1',
  String? completedAt = '2026-01-01T00:00:01Z',
}) => {
  'id': id, 'trigger': 'http', 'async': false, 'status': status, 'method': 'GET', 'path': '/',
  'statusCode': statusCode, 'responseBody': responseBody, 'logs': '', 'errors': null,
  'durationMs': 12, 'coldStart': false, 'triggeredBy': triggeredBy,
  'createdAt': '2026-01-01T00:00:00Z', 'completedAt': completedAt,
};

void main() {
  test('createExecution() posts method/path/body and decodes a sync FunctionExecution', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, _executionJson());
      }),
    );
    final execution = await client.functions.createExecution(
      'fn1',
      method: 'POST',
      path: '/hello',
      body: '{"name":"world"}',
    );
    expect(captured.method, 'POST');
    expect(captured.path, '/v1/functions/fn1/executions');
    expect(captured.query, isEmpty);
    expect(captured.body, {'method': 'POST', 'path': '/hello', 'body': '{"name":"world"}'});
    expect(execution.status, 'completed');
    expect(execution.statusCode, 200);
    expect(execution.isAsync, isFalse);
  });

  test('createExecution(async: true) sends async=true and decodes the 202 receipt', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(
          202,
          _executionJson(status: 'pending', statusCode: null, responseBody: null, completedAt: null),
        );
      }),
    );
    final execution = await client.functions.createExecution('fn1', async: true);
    expect(captured.query['async'], ['true']);
    expect(captured.body, <String, dynamic>{});
    expect(execution.status, 'pending');
    expect(execution.completedAt, isNull);
  });

  test('getExecution() fetches by id and decodes the completed FunctionExecution', () async {
    late TransportRequest captured;
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((r) {
        captured = r;
        return jsonResponse(200, _executionJson());
      }),
    );
    final execution = await client.functions.getExecution('fn1', 'e1');
    expect(captured.method, 'GET');
    expect(captured.path, '/v1/functions/fn1/executions/e1');
    expect(execution.id, 'e1');
    expect(execution.triggeredBy, 'user:u1');
  });

  test('getExecution() on someone else\'s execution maps the 404 to PraxyNotFoundException', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(404, {
        'message': 'Execution not found.', 'code': 404, 'type': 'function_execution_not_found',
        'version': '0.1.0', 'requestId': 'req-1',
      })),
    );
    await expectLater(
      client.functions.getExecution('fn1', 'not-mine'),
      throwsA(isA<PraxyNotFoundException>()),
    );
  });

  test('invoking a function with no execute role maps the 401 to PraxyAuthException', () async {
    final client = Praxy(
      endpoint: 'https://example.test',
      projectId: 'proj1',
      transport: FakeTransport((_) => jsonResponse(401, {
        'message': 'Not permitted to execute this function.', 'code': 401,
        'type': 'general_unauthorized', 'version': '0.1.0', 'requestId': 'req-1',
      })),
    );
    await expectLater(
      client.functions.createExecution('fn1'),
      throwsA(isA<PraxyAuthException>()),
    );
  });
}
