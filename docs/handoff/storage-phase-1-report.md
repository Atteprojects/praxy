# Storage, Phase 1 (the primitive) — report

**Status: complete.** Every item in `docs/handoff/storage-phase-1-prompt.md`'s scope shipped, and
every non-goal stayed out. Praxy now has the file-storage pillar it was missing entirely.

## The shape of it

`docs/research/storage.md`'s two load-bearing decisions were implemented as written, so the short
version is:

- **Bytes live in Postgres, split across fixed-size chunk rows**, behind a narrow `IFileStore` seam.
  No second datastore, nothing on disk — `CLAUDE.md`'s fixed decision holds. The seam is the thing
  that keeps a disk or S3 backend addable later without touching the API surface, the metadata model
  or the permission path.
- **The resource model is the Tables model.** Bucket ≈ table, file ≈ row, `BucketPermission` with
  `TablePermission`'s exact field shape over the same four `PermissionStrings.StorableActions`. So
  bucket access is `bucket.Roles(action) ∩ callerRoles` against the *same* `IRoleResolver` the query
  compiler and realtime fan-out use, deny-by-default falls out of it for free, and **no second
  authorization concept was introduced anywhere.**

## What shipped

### Database — migration `20260903044713_Storage`

Four tables in the `praxy` schema ([`Entities/Storage.cs`](../../src/Praxy.Persistence/Entities/Storage.cs)):

| Table | Notes |
|---|---|
| `buckets` | `project_id, key, name, enabled, file_security, max_file_size_bytes, allowed_mime_types, created_at, updated_at`. Unique on `(project_id, key)`. |
| `bucket_permissions` | `(bucket_id, action, role)` PK — `table_permissions` verbatim. |
| `files` | `bucket_id, name, mime_type, size_bytes, chunk_size_bytes, chunk_count, checksum, …`. Indexed `(bucket_id, created_at)` for the browser's one real query. |
| `file_chunks` | `(file_id, index)` PK, `data bytea`, `ON DELETE CASCADE` from `files`. |

Three details the prompt called load-bearing, and how each was handled:

- **`chunk_size_bytes` is on the file**, not only in config. `Praxy:Storage:ChunkSizeBytes` (512 KiB)
  is what *new* uploads are written with; every stored file remembers its own. Retuning the default
  can never invalidate a byte already stored.
- **`ALTER COLUMN data SET STORAGE EXTERNAL`** is in the migration, and it is **verified rather than
  assumed** — `StorageEngineTests.File_chunk_data_column_uses_external_storage` reads
  `pg_attribute.attstorage` from a real container and asserts `'e'`. It was also checked on the live
  dev database after the owner test (`attstorage = e`). A migration that silently didn't apply this
  looks identical from the outside, which is exactly why it is asserted.
- **`FileChunk` PK is `(file_id, index)`** with the cascade, so deleting a file removes its bytes in
  the same statement rather than leaving them for a sweeper, and a Phase 2 `Range` request can seek
  straight to `offset / chunk_size`.

### `src/Praxy.Storage` — new project, sibling to `Praxy.Tables`/`Praxy.Sites`

- **[`IFileStore.cs`](../../src/Praxy.Storage/IFileStore.cs)** — the seam, exactly as narrow as the
  prompt asked: `OpenWrite`, `OpenRead`, `DeleteAsync`. `FileWriteStream` is a `Stream` that also
  reports `BytesWritten`/`ChunkCount`/`Checksum`, so the checksum is computed *while* streaming
  rather than by re-reading what was just written. **Nothing above this seam knows a chunk exists.**
- **[`ChunkedWriteStream.cs`](../../src/Praxy.Storage/ChunkedWriteStream.cs)** — the buffering and
  boundary arithmetic, deliberately split out from the Postgres implementation so the off-by-one
  that matters here is testable without a database and a future non-Postgres store inherits it
  rather than reimplementing it. Buffers exactly one chunk; peak memory is one chunk regardless of
  file size.
- **[`PostgresChunkFileStore.cs`](../../src/Praxy.Storage/PostgresChunkFileStore.cs)** — the only
  implementation. Raw Npgsql over `PraxyDb`'s own connection (the pattern `SchemaDdl`/`RowsService`
  already established), so chunk `INSERT`s **join the caller's ambient EF transaction**. The read
  side is a single ordered cursor in `CommandBehavior.SequentialAccess` mode, pulling each `bytea`
  off the wire as the caller consumes it — one round trip, one chunk in memory.
- **[`BucketsService.cs`](../../src/Praxy.Storage/BucketsService.cs)** — CRUD plus the permission
  matrix, deliberately `TablesService`'s shape: same key/name validation (`Keys.IsValid`), same
  full-replace permission semantics through the same `PermissionStrings.ParseAndExpand`, same
  `force=true` gate on the destructive delete.
- **[`FilesService.cs`](../../src/Praxy.Storage/FilesService.cs)** — upload/list/get/rename/delete
  and the outbox writes.
- **[`BucketAccess.cs`](../../src/Praxy.Storage/BucketAccess.cs)** — the one authorization rule, in
  one named place so it can be got wrong in only one place.
- **[`MimeTypes.cs`](../../src/Praxy.Storage/MimeTypes.cs)** — allow-list matching (exact,
  `type/*`, `*/*`), plus `Content-Type` normalization. An empty allow-list is normalized to `null` on
  write, so there is one representation of "no restriction" rather than two that behave alike.

### Upload — one transaction, streamed, checked mid-stream

`FilesService.UploadAsync` inserts the metadata row (placeholder size/checksum), streams the request
body into chunk rows, then corrects the metadata — **all inside one `SchemaDdl.InTransactionAsync`**,
so a failure at any point leaves nothing behind rather than a half-file. The placeholder state is
never observable.

Both byte limits are checked **before every write, on every read** from the body, not only at the
start:

```csharp
if (writer.BytesWritten + read > maxFileSize)   throw TooLarge(maxFileSize);
if (writer.BytesWritten + read > budget.Remaining) throw OverStorageQuota(budget);
```

That is the prompt's "reject mid-stream and roll back" answer, taken literally: a chunked upload
declaring no `Content-Length` cannot be caught up front, and the transaction gives the rollback for
free. A declared `Content-Length` is still rejected up front as a cheap fast path.

### Kestrel's body limit — the landmine

Raised explicitly **from the same configured value the per-file quota uses**, in two places that
cannot disagree:

1. `Program.cs` sets `KestrelServerOptions.Limits.MaxRequestBodySize` from
   `Praxy:Quotas:MaxFileSizeBytes` (50 MB default), replacing the framework's 30 MB.
2. `StorageTransfer.UploadAsync` raises the *per-request* limit to the value actually resolved for
   that project — an organization's `limits` jsonb can set `maxFileSizeBytes` above the instance
   default, and without this that raised limit would be silently unreachable.

Kestrel remains the backstop for a client that lies about `Content-Length`; its
`BadHttpRequestException(413)` is caught and translated into the *same* `file_size_exceeded` error
the service's own streaming check raises. One error for one condition, whichever guard sees it
first.

### Quotas

Three dimensions on the existing `QuotaOptions`, with matching nullable `OrganizationLimits`
overrides — the org-jsonb mechanism needed no changes, exactly as the design doc predicted:

| Key | Default |
|---|---|
| `Praxy:Quotas:MaxBucketsPerProject` | 20 |
| `Praxy:Quotas:MaxFileSizeBytes` | 50 MB |
| `Praxy:Quotas:MaxStorageBytesPerProject` | 5 GB |

`EnsureBucketQuotaAsync` follows the existing `EnsureXQuotaAsync` shape. The two byte-valued ones go
through `GetStorageBudgetAsync`, which **returns** rather than throws, because the upload path has to
enforce them mid-stream against one org lookup. Used storage is `SUM(size_bytes)` over the project's
files — no denormalized counter to drift out of sync.

A bucket's own `max_file_size_bytes` is **clamped to the resolved quota on write and re-derived at
upload time** (an org limit can be lowered after the bucket was created): a bucket may narrow the
ceiling, never widen it.

### Events

`buckets.{bucketId}.files.{fileId}.create|update|delete`, written to the outbox inside the same
transaction as the file change and published in-process after commit. `ChannelGrammar` got a **new
prefix on the existing mechanism** — the same four fan-out variants a row event produces, one level
shallower — not a second grammar. Read roles travel in the payload, computed pre-commit, because a
delete has no file left to re-query afterward.

### API

**Data plane** `/v1/storage` (`data-plane` rate limit, `ProjectGuardFilter` + `AppPrincipalFilter`):

- Bucket management (`POST/GET/PATCH/DELETE /buckets…`, `GET/PATCH /buckets/{id}/permissions`) is
  API-key-only under two new scopes, `storage.read` / `storage.write` — the storage analogue of
  `DatabaseEndpoints`' schema surface, because configuring a bucket is a server/CI concern.
- File operations (`POST/GET/PATCH/DELETE /buckets/{id}/files…`, `GET …/download`) are reachable by
  sessions, guests and keys alike; **bucket permissions do the access control**, and a key
  additionally needs the scope — the exact posture `RowEndpoints` has.

**Console** `/v1/console/projects/{projectId}/storage` (operator session + project ownership):
bucket CRUD, permissions, the file browser, upload/download/delete, and a `/usage` endpoint. Reads
and writes here bypass bucket permission filtering entirely — the same implicit posture
`ConsoleRowEndpoints` has for operators — and every mutation is audited (`storage.buckets.*`,
`storage.files.*`).

Download sets `Content-Type` and `Content-Length` from the metadata row and copies the chunk stream
straight to `Response.Body`. The whole file is never materialized.

> Two API details worth recording, both caught by tests rather than by reading the code:
>
> - The upload routes are declared `.Accepts<Stream>("*/*")`, not
>   `.Accepts<Stream>("application/octet-stream")`. Naming a concrete type turns it into an
>   endpoint-*matching* constraint, and uploading a PNG then 404s before reaching the handler.
> - The download routes need `.Produces<Stream>(200, "application/octet-stream")`, not a bare
>   `.Produces(200, contentType: …)`. Without a response *type* the generator emits no `content`
>   block at all and the endpoint reads as undocumented — `OpenApiDocumentTests`' ratchet, which
>   exists for precisely this, caught it. `Stream` resolves to `{"type": "string", "format":
>   "binary"}`, which is the correct shape for a binary download.

### Console

A Storage section, `g b` in the nav (`g s` was already auth settings):

- **`StoragePage`** — bucket list plus a project usage bar against `MaxStorageBytesPerProject`,
  captioned "Stored files are included in every backup." That number is what an operator's backups
  grow by; it belongs on screen, not only in a doc.
- **`BucketFilesPage`** — drag-and-drop upload with a real progress bar, download, delete, and each
  file's name/type/size/uploaded-at plus the chunk count and checksum prefix it was actually written
  with.
- **`BucketSettingsPage`** — the permission matrix **reusing `AddRoleButton`/`RoleLabel`** exactly as
  `TableSettingsPage` does (same grammar, same parse, same presets shape), an enable toggle, and a
  typed-name danger zone.
- Overview's quota card gained **Buckets** and **Stored files** rows (the latter byte-formatted).

Upload goes through `XMLHttpRequest` rather than `fetch` for one reason, documented at the call
site: `fetch` reports no upload progress at all, and a multi-megabyte upload with no feedback reads
as a hung console. Download fetches with credentials and hands the browser a blob URL, which keeps
the file's stored name without the server needing `Content-Disposition` — deliberately Phase 2's
problem.

### SDKs

A 5-method `StorageService` in **both** SDKs, matching each other and each SDK's existing naming:
`createFile`, `listFiles`, `getFile`, `getFileDownload`, `deleteFile`. Bucket configuration and its
permission matrix stay out, the same line `TablesService` draws against schema management.

Rather than bolting a second HTTP path beside each SDK's `Transport` seam, both seams gained a raw
byte path — `TransportRequest.bodyBytes`/`contentType` so an upload's bytes go on the wire unencoded,
and a bytes-shaped response for download (Dart's `TransportResponse` already carried `bodyBytes`; the
JS one gained an optional field plus an `expect: "bytes"` request flag). Error mapping is shared with
the JSON path, so a 401 on a download is the same typed error it is anywhere else. `@praxy/core`'s
edge-runtime safety test still passes — no `node:*` import, no `Buffer`.

Both packages' READMEs document the surface, including that a bucket denies everyone until an
operator grants a role (so a `401` on a fresh bucket is expected, not a bug).

### Docs

`docs/self-host.md` gained a **"Storage and backup size"** section that says the thing plainly, in a
blockquote, before the backup instructions:

> **Every stored byte lands in every backup.** `backup.sh` runs `pg_dump` over the `praxy` schema, so
> a project holding 5 GB of files produces 5 GB of dump, every run.

…plus the control (`MaxStorageBytesPerProject`), why there is deliberately no "skip the chunk table"
flag in v1 (a backup that silently omits data is worse than a large one), the dead-tuple churn note,
and the six new config keys in the configuration table. The backup section itself now names
`praxy.file_chunks` explicitly as part of what `praxy.dump` contains. `docs/openapi/v1.json` was
regenerated.

## Verification

**`dotnet test` green — 494/494 unit, 287/287 integration** (real Postgres via Testcontainers, real
Docker daemon throughout). New coverage:

*Unit* — `ChunkedWriteStreamTests` (the boundary arithmetic: 0, 1, 63, **64 (exact multiple)**,
**65 (one byte over)**, 128, 129, 1000 bytes against a 64-byte chunk; no empty trailing chunk ever;
concatenated chunks reproduce the input; SHA-256 matches; chunking is independent of how the source
is sliced), `MimeTypesTests`, `BucketAccessTests` (against the real `RoleResolver`), plus new
`ChannelGrammar`, `OrganizationLimits` and `StorageBudget` cases.

*Integration* (`StorageEngineTests`, 21 tests) — a ~250 KB file over a 4 KB chunk size round-tripping
byte-for-byte with its chunk rows inspected directly; the exact-multiple case; a zero-byte file
storing no chunks at all; one byte over the bucket limit rejected with **nothing left behind**; the
mid-stream case with `Transfer-Encoding: chunked` and no declared length; both byte quotas; the
bucket quota; mime rejection; deny-by-default on read *and* write for users and guests; a team grant
resolving through the shared resolver; a key needing the scope on top of the grant; file delete
removing chunk rows; bucket delete cascading; the outbox rows; cross-project isolation; and
`attstorage = 'e'`.

*Streaming* (`StorageStreamingTests`) — the landmine, tested rather than asserted. A **32 MB** and a
**128 MB** file, each generated on the fly (so the test never holds one either), uploaded and
downloaded back with the body hashed as it arrives, with a background sampler watching the managed
heap throughout.

The assertion is about **growth with respect to file size**, not an absolute number of megabytes,
and that distinction was earned rather than assumed: the first version asserted an absolute bound,
measured 19 MB in isolation — twice, for both a 64 MB and a 128 MB file — and then failed at 65 MB
when run after 285 other integration tests, because `GC.GetTotalMemory(false)` counts whatever
garbage the rest of the suite left uncollected. An absolute bound was measuring the suite, not the
code. What is stable, and is the actual property being claimed, is that quadrupling the file does
not quadruple the memory: the test now asserts that the extra 96 MB of file costs less than 48 MB of
extra peak heap. A buffering implementation spends the full 96 MB and more; a streaming one spends
about nothing, because its working set is one chunk plus transport buffers either way.

**Console build clean** (`tsc -b && vite build`). **SDKs green**: `dart analyze .` clean (the four
pre-existing `prefer_initializing_formals` infos in `realtime_socket.dart` are untouched by this
work), `dart test praxy_core praxy_codegen` + `flutter test praxy_flutter example` all passing, and
the JS workspace's typecheck and 107 tests across four packages.

**Owner test — actually run, twice: once over HTTP, once by clicking the console.**

Over HTTP against a real instance: created a bucket, granted permissions, uploaded a **5 MB real
PDF** (11 chunks), downloaded it byte-identical (`cmp` clean, `file` still reports a valid 1-page
PDF), the server's streamed checksum matching `shasum -a 256` taken locally; a 9 MB upload against an
8 MB bucket limit rejected with `HTTP 400 file_size_exceeded` — *"This file exceeds the 8388608 byte
limit for this bucket."* — and the file list still showing only the one good file; usage reporting
exactly the stored bytes, then 0 after delete; bucket delete refused without `force` and accepted
with it.

In the console UI: created **Product images** (key auto-slugified to `product_images`, allow-list
`image/*, application/pdf`), landed on its permissions as designed, applied the "Signed-in users"
preset and watched the matrix fill in, uploaded a 3.2 MB PNG through the **file picker** (7 chunks)
and a 964 KB PNG through **drag-and-drop** (2 chunks), had a `.txt` refused with *"This bucket does
not accept 'text/plain'. Allowed: image/*, application/pdf."*, downloaded both back through the
console's own path and confirmed the SHA-256 matched the stored checksum **and that the PNG decodes
at its original 1400×1000** — i.e. it opens correctly, not merely round-trips — then deleted a file
and deleted the bucket through the typed-name danger zone. Afterwards, Postgres showed
`buckets=0 files=0 chunks=0 bucket_perms=0`, the audit log held all eleven storage actions, and the
outbox held the file create/delete events on the new channel.

## Deviations from the prompt, and why

1. **`file_security` is persisted but unused.** The design doc's field list includes it marked
   "(Phase 2)", so the column exists and defaults to `false` — it saves a migration later. It is
   never read and never appears on the wire; Phase 1 is bucket-level only, and Phase 2 owns its
   semantics.
2. **Bucket CRUD is on the data plane as well as the console.** The prompt didn't say where; the
   design doc says the resource model deliberately mirrors Tables, and Tables has both a key-scoped
   `/v1/databases` schema surface and a console one. Two new scopes (`storage.read`/`storage.write`)
   came with it.
3. **The SDKs ship five methods, not two.** The prompt asked for upload + download and "keep the
   surface small". Upload alone is not usable — an app that uploads an avatar needs to list, read and
   delete one too. `updateFile` (rename) is the one route deliberately left unwrapped, to hold the
   line somewhere; it exists in the API and both READMEs say so.

## Known gaps

- **Deleting a bucket emits no per-file `delete` events.** The files go via the DB-level FK cascade,
  not through `FilesService`, so a realtime subscriber to `buckets.X.files` sees nothing. This
  matches Tables exactly (dropping a table emits no per-row deletes) and is left consistent rather
  than special-cased.
- **The project storage quota is a soft guard under concurrency.** Two simultaneous uploads can each
  read the same `Remaining` before either commits, so the total can overshoot by up to one file.
  Same trade `EnsurePreviewQuotaAsync` already accepts; serializing every upload behind a lock would
  cost more than it protects.
- **The upload holds a database transaction for the duration of the request.** Inherent to the
  one-transaction requirement. A slow client on a large file holds a pooled connection for that long.
  Worth watching if Storage sees heavy concurrent use.
- **No SDK-side streaming.** Both SDKs buffer a file whole in memory; the *server* streams both ways.
  Documented in both READMEs at the method.
- **Renaming a file is API-only** — no console UI and no SDK method for it.

## Commands

Nothing new is required at runtime — Storage adds no Docker/network/CLI dependency. New knobs:

| Key | Default | Purpose |
|---|---|---|
| `Praxy:Storage:ChunkSizeBytes` | `524288` | Bytes per `file_chunks` row for **new** uploads; recorded per file. |
| `Praxy:Storage:DefaultBucketMaxFileSizeBytes` | `52428800` | Per-file ceiling for a bucket created without one. Clamped to the quota. |
| `Praxy:Quotas:MaxBucketsPerProject` | `20` | Buckets per project (org-overridable). |
| `Praxy:Quotas:MaxFileSizeBytes` | `52428800` | Per-file ceiling (org-overridable). **Kestrel's body limit is derived from this.** |
| `Praxy:Quotas:MaxStorageBytesPerProject` | `5368709120` | Total stored bytes per project (org-overridable). **Bounds backup size.** |

Everything else is unchanged: `dotnet test`, `npm run build --prefix console`,
`dotnet run --project src/Praxy.Api`, `cd deploy && ./up.sh`.

## Where Phase 2 picks up

Scoped in `docs/research/storage.md` and deliberately undesigned in detail, same as every prior phase
sequence here: **per-file permissions** (the row-security analogue — `buckets.file_security` is
already the flag, and the side table follows `__perms`' shape), **HTTP `Range`** (the `(file_id,
index)` PK is chosen so `offset / chunk_size` is a seek, and `ChunkReadStream` is the one class that
needs to learn it), and **`Content-Disposition` inline-vs-attachment handling**. No Phase 2 prompt is
written, per the prompt's own instruction.
