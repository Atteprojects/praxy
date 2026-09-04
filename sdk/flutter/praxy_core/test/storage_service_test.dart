import 'package:praxy_core/praxy_core.dart';
import 'package:test/test.dart';

import 'support/fake_transport.dart';

Map<String, dynamic> _fileJson({String id = 'f1', String name = 'avatar.png', int size = 3}) => {
  'id': id,
  'bucketId': 'b1',
  'name': name,
  'mimeType': 'image/png',
  'sizeBytes': size,
  'chunkSizeBytes': 524288,
  'chunkCount': 1,
  'checksum': 'abc123',
  'createdAt': '2026-01-01T00:00:00Z',
  'updatedAt': '2026-01-01T00:00:00Z',
};

Praxy _client(ResponseBuilder handler, {void Function(TransportRequest)? capture}) => Praxy(
  endpoint: 'https://example.test',
  projectId: 'proj1',
  transport: FakeTransport((r) {
    capture?.call(r);
    return handler(r);
  }),
);

void main() {
  test('createFile() sends per-file grants in the query, since the body is the bytes', () async {
    late TransportRequest captured;
    const grants = ['read("user:u1")', 'delete("user:u1")'];
    final client = _client(
      (_) => jsonResponse(201, {..._fileJson(), r'$permissions': grants}),
      capture: (r) => captured = r,
    );

    final file = await client.storage.createFile(
      'b1',
      name: 'avatar.png',
      bytes: const [1],
      permissions: grants,
    );

    expect(captured.query, {
      'name': ['avatar.png'],
      'permissions': grants,
    });
    expect(file.permissions, grants);
  });

  test('createFile() omits the permissions key entirely when none are given', () async {
    late TransportRequest captured;
    final client = _client((_) => jsonResponse(201, _fileJson()), capture: (r) => captured = r);

    final file = await client.storage.createFile('b1', name: 'a.png', bytes: const [1], permissions: const []);

    expect(captured.query, {'name': ['a.png']});
    // A server that sends no $permissions at all decodes as "none", never as a crash.
    expect(file.permissions, isEmpty);
  });

  test('createFile() sends the raw bytes as the body, with the name in the query', () async {
    late TransportRequest captured;
    final client = _client((_) => jsonResponse(201, _fileJson()), capture: (r) => captured = r);

    final file = await client.storage.createFile(
      'b1',
      name: 'avatar.png',
      bytes: const [1, 2, 3],
      mimeType: 'image/png',
    );

    expect(captured.method, 'POST');
    expect(captured.path, '/v1/storage/buckets/b1/files');
    expect(captured.query, {'name': ['avatar.png']});
    // The bytes go verbatim — never JSON-encoded, which would corrupt them.
    expect(captured.bodyBytes, [1, 2, 3]);
    expect(captured.body, isNull);
    expect(captured.contentType, 'image/png');
    expect(file.id, 'f1');
    expect(file.checksum, 'abc123');
    expect(file.chunkCount, 1);
  });

  test('createFile() without a mime type leaves the content type to the transport default', () async {
    late TransportRequest captured;
    final client = _client((_) => jsonResponse(201, _fileJson()), capture: (r) => captured = r);

    await client.storage.createFile('b1', name: 'blob.bin', bytes: const [9]);

    expect(captured.contentType, isNull);
  });

  test('createFile() of an empty file still sends an (empty) byte body', () async {
    late TransportRequest captured;
    final client = _client(
      (_) => jsonResponse(201, _fileJson(size: 0)),
      capture: (r) => captured = r,
    );

    final file = await client.storage.createFile('b1', name: 'empty.txt', bytes: const []);

    expect(captured.bodyBytes, isEmpty);
    expect(file.sizeBytes, 0);
  });

  test('listFiles() decodes the list and passes paging through', () async {
    late TransportRequest captured;
    final client = _client(
      (_) => jsonResponse(200, {'total': 2, 'files': [_fileJson(), _fileJson(id: 'f2', name: 'b.png')]}),
      capture: (r) => captured = r,
    );

    final list = await client.storage.listFiles('b1', limit: 10, offset: 20);

    expect(captured.method, 'GET');
    expect(captured.path, '/v1/storage/buckets/b1/files');
    expect(captured.query, {'limit': ['10'], 'offset': ['20']});
    expect(list.total, 2);
    expect(list.files.map((f) => f.name), ['avatar.png', 'b.png']);
  });

  test('listFiles() with no paging sends no query at all', () async {
    late TransportRequest captured;
    final client = _client(
      (_) => jsonResponse(200, {'total': 0, 'files': []}),
      capture: (r) => captured = r,
    );

    final list = await client.storage.listFiles('b1');

    expect(captured.query, isEmpty);
    expect(list.files, isEmpty);
  });

  test('getFile() reads one file\'s metadata', () async {
    late TransportRequest captured;
    final client = _client((_) => jsonResponse(200, _fileJson()), capture: (r) => captured = r);

    final file = await client.storage.getFile('b1', 'f1');

    expect(captured.method, 'GET');
    expect(captured.path, '/v1/storage/buckets/b1/files/f1');
    expect(file.mimeType, 'image/png');
  });

  test('getFileDownload() returns the body bytes untouched', () async {
    late TransportRequest captured;
    final client = _client(
      (_) => const TransportResponse(statusCode: 200, headers: {}, bodyBytes: [0, 255, 10, 13]),
      capture: (r) => captured = r,
    );

    final bytes = await client.storage.getFileDownload('b1', 'f1');

    expect(captured.method, 'GET');
    expect(captured.path, '/v1/storage/buckets/b1/files/f1/download');
    // Bytes that are not valid UTF-8 and include a newline: nothing decoded them on the way through.
    expect(bytes, [0, 255, 10, 13]);
  });

  test('getFileDownload() maps an error status the same way a JSON call does', () async {
    final client = _client((_) => jsonResponse(401, {
      'message': 'Not permitted to read files in this bucket.',
      'code': 401,
      'type': 'general_unauthorized',
      'version': '0.1.0',
      'requestId': 'r1',
    }));

    await expectLater(
      client.storage.getFileDownload('b1', 'f1'),
      throwsA(isA<PraxyAuthException>()),
    );
  });

  test('deleteFile() issues a DELETE and tolerates the 204 empty body', () async {
    late TransportRequest captured;
    final client = _client((_) => emptyResponse(204), capture: (r) => captured = r);

    await client.storage.deleteFile('b1', 'f1');

    expect(captured.method, 'DELETE');
    expect(captured.path, '/v1/storage/buckets/b1/files/f1');
  });
}
