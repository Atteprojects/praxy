# Session task — Storage, Phase 1 (the primitive)

## Why this exists

Praxy has no file storage at all — this phase adds the pillar. Read `docs/research/storage.md` in full
before writing any code; it is the complete architecture and it settles the decisions that matter
(bytes chunked in Postgres rather than on disk and why, the resource model deliberately mirroring
Tables, the permission reuse rule, what the design costs operationally). This prompt assumes you've
read it and doesn't re-explain what's settled there. Work on a new branch off `main`. Read `CLAUDE.md`
first — especially the fixed decisions and the cross-phase rules, both of which this feature touches
directly.

This is Phase 1 of three (see `docs/roadmap.md`'s Storage section). Ship the primitive well; don't pull
Phase 2/3 work forward.

## Non-goals — do not build these

- **No per-file permissions.** Bucket-level only in this phase. Per-file is Phase 2 and follows the
  existing row-security shape; don't improvise a different one now.
- **No HTTP Range requests.** Full-file download only. The chunk layout is chosen to make Range easy
  later — don't half-implement partial content here.
- **No image transforms**, no resumable/multi-part upload protocol, no encryption at rest, no antivirus,
  no signed URLs, no CDN.
- **No second datastore, and no files on disk.** `CLAUDE.md`'s fixed decision. If you find yourself
  wanting a filesystem path, re-read the design doc's first section — the interface seam exists so that
  can be revisited later, deliberately, not improvised now.
- **No second authorization concept.** Bucket access resolves through the existing `IRoleResolver` and
  the same intersect-the-roles check tables already use.

## Scope

1. **`src/Praxy.Storage` project** — new, sibling to `Praxy.Tables`/`Praxy.Sites`, referenced from
   `Praxy.Api`. Follow the existing project's conventions for DI registration and options binding.
2. **Entities + migration** (`src/Praxy.Persistence`): `Bucket`, `BucketPermission`, `StoredFile`,
   `FileChunk`, in the `praxy` schema. Field lists are in the design doc's resource-model section.
   Two things there are load-bearing and easy to get wrong:
   - `chunk_size_bytes` lives **on the file**, not only in config. Changing the configured default must
     never invalidate existing files.
   - The chunk `data` column needs `ALTER COLUMN data SET STORAGE EXTERNAL` in the migration. The
     default (`EXTENDED`) burns CPU trying to LZ-compress already-compressed media for no gain.
     Confirm the setting actually applied (`\d+` on the table, or `pg_attribute.attstorage`) rather
     than assuming the migration did what you meant.
   - `FileChunk` PK is `(file_id, index)`, with `ON DELETE CASCADE` from the file so deleting a file
     removes its bytes in the same statement.
3. **`IFileStore` seam + its Postgres-chunked implementation.** Narrow on purpose: open a write stream,
   open a read stream, delete a file's bytes. Everything Postgres-specific lives behind it. Nothing
   above it should know a chunk exists.
4. **Bucket CRUD + permissions**: create/list/get/update/delete, and a permissions endpoint mirroring
   the tables one. Reuse `PermissionStrings.StorableActions` and the existing permission parsing —
   don't define a new action vocabulary. Deny-by-default: a new bucket is unreachable until granted.
   Bucket delete is destructive and should follow the existing `force=true` convention.
5. **Upload** — `POST` a file's bytes, streaming the request body into chunk rows inside **one
   transaction** so a failure can never leave a half-written file. Validate against the bucket's
   `max_file_size_bytes` and `allowed_mime_types` (null = any). Compute and store a checksum as you
   stream, not by re-reading.
6. **Download** — stream chunks back in order, with correct `Content-Type` and `Content-Length`. Never
   materialize the whole file.
7. **Delete** — file and bucket, cascading to chunks.
8. **Quotas**: add `MaxBucketsPerProject`, `MaxFileSizeBytes`, `MaxStorageBytesPerProject` to
   `QuotaOptions` and the matching nullable overrides to `OrganizationLimits`. Enforce through the
   existing `QuotaService.EnsureXQuotaAsync` shape. `MaxStorageBytesPerProject` is the one that keeps
   backup size bounded — it is not optional.
9. **Outbox events** for file create/update/delete, and the `buckets.{bucketId}.files.{fileId}.{action}`
   channel prefix in `ChannelGrammar` (new prefix, existing mechanism — don't add a second grammar).
10. **Console**: a Storage section — bucket list + create, a file browser per bucket (name, size, type,
    uploaded-at), upload (with progress if it's cheap), download, delete, and a bucket permissions
    editor reusing the existing `AddRoleButton`/`RoleLabel` components rather than new ones.
11. **SDKs**: upload + download in the Flutter SDK and `@praxy/core`. Keep the surface small and match
    each SDK's existing method-naming conventions.
12. **Docs**: `docs/self-host.md` gains a Storage section stating plainly that **backups grow with
    stored files**, since `backup.sh` `pg_dump`s the schema the bytes live in, and pointing at
    `MaxStorageBytesPerProject` as the control. An operator finding this out from a full disk is a
    documentation failure.

## Landmines — read before writing code

- **Kestrel's default 30 MB request-body limit will reject uploads before any Praxy check runs.** Raise
  it explicitly, and derive both it and the quota check from the *same* configured value — if they
  disagree, users hit a confusing failure at a size nobody configured. This is the most likely thing in
  this phase to be missed until someone uploads a big file.
- **Streaming means streaming.** It is very easy to write code that looks streaming but calls
  `ReadToEndAsync`, or that buffers the response. Verify with a file large enough that buffering would
  be obvious in memory, not just that the bytes round-trip.
- **The whole upload must be one transaction.** Chunks and metadata commit together or not at all.
- **Don't let `MaxStorageBytesPerProject` be checked only at the start of an upload** — a streaming
  upload can exceed it midway. Decide and document how you handle that (reject mid-stream and roll
  back is fine and is the simplest correct answer).
- **`EXTERNAL` storage has to be verified, not assumed** — a migration that silently didn't apply it
  looks identical from the outside.

## Tests

- Unit: bucket permission resolution against the shared role resolver; quota enforcement boundaries;
  mime-type allow-list matching; chunk-count/size arithmetic including a file that is an exact multiple
  of the chunk size and one that is one byte over (off-by-one on the final chunk is the classic bug).
- Integration (real Postgres via Testcontainers): upload a file **spanning many chunks** and download it
  back, comparing bytes exactly; a zero-byte file; a file one byte over `max_file_size_bytes` is
  rejected cleanly; deleting a file removes its chunk rows; deleting a bucket cascades; a caller without
  the bucket grant gets denied on both read and write; the outbox row is written on upload.

## Done means

- `dotnet test` green (unit + integration).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run**: in the console, create a bucket, grant permissions, upload a real file
  of at least a few MB, download it back and confirm it opens correctly, delete it, then delete the
  bucket. Confirm an over-quota upload fails with a clear message rather than a truncated file.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/storage-phase-1-report.md`. **Do not write a Phase 2 prompt** — Phase 2 is scoped
  in the design doc but deliberately undesigned in detail, same as every prior phase sequence here.
