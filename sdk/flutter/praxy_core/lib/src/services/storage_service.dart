import '../client.dart';
import '../json_utils.dart';
import '../models.dart';

/// The data-plane storage surface (`/v1/storage`). Five methods: upload, list,
/// read metadata, download bytes, delete. Bucket configuration and its permission
/// matrix are a console/operator concern and stay out of this SDK, the same line
/// `TablesService` draws against schema management.
///
/// Every method is gated by the bucket's own permission grants — empty
/// (deny-by-default) on a new bucket, so a `401` there is expected behavior until
/// an operator grants a role, not a bug this service works around.
final class StorageService {
  const StorageService(this._client);

  final Praxy _client;

  /// Uploads [bytes] as a new file. [mimeType] becomes the stored file's type and
  /// is checked against the bucket's allow-list; omitted, the server records
  /// `application/octet-stream`.
  ///
  /// The whole file is sent in one request — there is no resumable protocol — and
  /// the server streams it into storage inside a single transaction, so a failed
  /// or over-quota upload leaves nothing behind rather than a partial file.
  Future<StoredFile> createFile(
    String bucketId, {
    required String name,
    required List<int> bytes,
    String? mimeType,
  }) async => StoredFile.fromJson(
    requireJson(
      await _client.request(
        method: 'POST',
        path: '/v1/storage/buckets/$bucketId/files',
        query: {'name': [name]},
        bodyBytes: bytes,
        contentType: mimeType,
      ),
      bucketId,
    ),
  );

  Future<StoredFileList> listFiles(String bucketId, {int? limit, int? offset}) async =>
      StoredFileList.fromJson(
        requireJson(
          await _client.request(
            method: 'GET',
            path: '/v1/storage/buckets/$bucketId/files',
            query: {
              if (limit != null) 'limit': ['$limit'],
              if (offset != null) 'offset': ['$offset'],
            },
          ),
          bucketId,
        ),
      );

  Future<StoredFile> getFile(String bucketId, String fileId) async => StoredFile.fromJson(
    requireJson(
      await _client.request(method: 'GET', path: '/v1/storage/buckets/$bucketId/files/$fileId'),
      fileId,
    ),
  );

  /// The file's bytes. Buffered whole in memory here — the server streams them, but
  /// `Transport` returns a complete response body, so a very large download is the
  /// caller's memory to budget for.
  Future<List<int>> getFileDownload(String bucketId, String fileId) => _client.requestBytes(
    method: 'GET',
    path: '/v1/storage/buckets/$bucketId/files/$fileId/download',
  );

  Future<void> deleteFile(String bucketId, String fileId) async {
    await _client.request(method: 'DELETE', path: '/v1/storage/buckets/$bucketId/files/$fileId');
  }
}
