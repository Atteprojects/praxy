import type { Praxy } from "../client.js";
import type { StoredFile, StoredFileList } from "../models.js";

/**
 * The data-plane storage surface (`/v1/storage`). 5 methods, matching `praxy_core`'s
 * `StorageService` (`sdk/flutter/praxy_core/lib/src/services/storage_service.dart`): upload, list,
 * read metadata, download bytes, delete. Bucket configuration and its permission matrix are a
 * console/operator concern and stay out of this SDK, the same line `TablesService` draws against
 * schema management.
 *
 * Every method is gated by the bucket's own permission grants — empty (deny-by-default) on a new
 * bucket, so a `401` there is expected behavior until an operator grants a role.
 */
export class StorageService {
  constructor(private readonly client: Praxy) {}

  /**
   * Uploads `bytes` as a new file. `mimeType` becomes the stored file's type and is checked against
   * the bucket's allow-list; omitted, the server records `application/octet-stream`.
   *
   * The whole file is sent in one request — there is no resumable protocol — and the server streams
   * it into storage inside a single transaction, so a failed or over-quota upload leaves nothing
   * behind rather than a partial file.
   *
   * `permissions` are the new file's own grants (`read`/`update`/`delete` only — a file can't grant
   * its own creation), accepted only on a bucket with file security on. They travel in the query
   * because the body *is* the bytes. There is no auto-grant to the uploader, exactly as there is
   * none for a row: pass `read("user:<id>")` yourself if that is what you want.
   */
  createFile(
    bucketId: string,
    input: { name: string; bytes: Uint8Array; mimeType?: string; permissions?: string[] },
  ): Promise<StoredFile> {
    const query: Record<string, string[]> = { name: [input.name] };
    if (input.permissions?.length) query.permissions = input.permissions;
    return this.client.request<StoredFile>("POST", this.filesPath(bucketId), {
      query,
      bodyBytes: input.bytes,
      contentType: input.mimeType,
    });
  }

  listFiles(bucketId: string, options: { limit?: number; offset?: number } = {}): Promise<StoredFileList> {
    const query: Record<string, string[]> = {};
    if (options.limit !== undefined) query.limit = [String(options.limit)];
    if (options.offset !== undefined) query.offset = [String(options.offset)];
    return this.client.request<StoredFileList>("GET", this.filesPath(bucketId), { query });
  }

  getFile(bucketId: string, fileId: string): Promise<StoredFile> {
    return this.client.request<StoredFile>("GET", `${this.filesPath(bucketId)}/${encodeURIComponent(fileId)}`);
  }

  /**
   * The file's bytes. Buffered whole here — the server streams them, but `Transport` returns a
   * complete response body, so a very large download is the caller's memory to budget for.
   */
  getFileDownload(bucketId: string, fileId: string): Promise<Uint8Array> {
    return this.client.requestBytes(
      "GET",
      `${this.filesPath(bucketId)}/${encodeURIComponent(fileId)}/download`,
    );
  }

  deleteFile(bucketId: string, fileId: string): Promise<void> {
    return this.client.request<void>("DELETE", `${this.filesPath(bucketId)}/${encodeURIComponent(fileId)}`);
  }

  private filesPath(bucketId: string): string {
    return `/v1/storage/buckets/${encodeURIComponent(bucketId)}/files`;
  }
}
