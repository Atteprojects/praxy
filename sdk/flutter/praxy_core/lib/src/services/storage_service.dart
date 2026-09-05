import '../client.dart';
import '../json_utils.dart';
import '../models.dart';

/// Storage Phase 3: on-the-fly resize/crop/format/quality on the download endpoint.
/// Dimensions snap up to a fixed ladder server-side (64/128/256/512/1024/2048) — a
/// storage-amplification control, not an SDK-side detail to replicate — so a size
/// above the top rung is rejected rather than clamped.
final class FileTransform {
  const FileTransform({this.width, this.height, this.format, this.quality, this.gravity});

  final int? width;
  final int? height;

  /// `png` | `jpeg` | `webp`.
  final String? format;
  final int? quality;

  /// The crop anchor when both [width] and [height] are given and the aspect ratios differ:
  /// `center` (the default) | `top-left` | `top` | `top-right` | `left` | `right` | `bottom-left` |
  /// `bottom` | `bottom-right`. Has no effect without both dimensions.
  final String? gravity;

  Map<String, List<String>> toQuery() => {
    if (width != null) 'width': ['$width'],
    if (height != null) 'height': ['$height'],
    if (format != null) 'format': [format!],
    if (quality != null) 'quality': ['$quality'],
    if (gravity != null) 'gravity': [gravity!],
  };
}

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
  ///
  /// [permissions] are the new file's own grants (`read`/`update`/`delete` only —
  /// a file can't grant its own creation), accepted only on a bucket with file
  /// security on. They travel in the query because the body *is* the bytes. There
  /// is no auto-grant to the uploader, exactly as there is none for a row: pass
  /// `read("user:<id>")` yourself if that is what you want.
  Future<StoredFile> createFile(
    String bucketId, {
    required String name,
    required List<int> bytes,
    String? mimeType,
    List<String>? permissions,
  }) async => StoredFile.fromJson(
    requireJson(
      await _client.request(
        method: 'POST',
        path: '/v1/storage/buckets/$bucketId/files',
        query: {
          'name': [name],
          if (permissions != null && permissions.isNotEmpty) 'permissions': permissions,
        },
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

  /// The file's bytes — or, with [transform], a generated derivative's bytes instead.
  /// Buffered whole in memory either way: the server streams them, but `Transport`
  /// returns a complete response body, so a very large download (or a request for the
  /// source's own native size with no [transform] at all) is the caller's memory to
  /// budget for.
  ///
  /// A derivative resolves through exactly the same permission check as the source
  /// file — it is a representation of that file, not a resource with grants of its
  /// own — and Range never applies to one, since a transform request always returns
  /// the whole generated image. Requested dimensions snap up to a fixed ladder
  /// server-side (64/128/256/512/1024/2048); a size above the top rung is rejected
  /// rather than clamped.
  Future<List<int>> getFileDownload(
    String bucketId,
    String fileId, {
    FileTransform? transform,
  }) => _client.requestBytes(
    method: 'GET',
    path: '/v1/storage/buckets/$bucketId/files/$fileId/download',
    query: transform?.toQuery() ?? const {},
  );

  Future<void> deleteFile(String bucketId, String fileId) async {
    await _client.request(method: 'DELETE', path: '/v1/storage/buckets/$bucketId/files/$fileId');
  }
}
