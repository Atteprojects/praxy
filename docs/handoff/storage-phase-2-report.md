# Storage, Phase 2 (access control and serving) — report

**Status: complete.** Every item in `docs/handoff/storage-phase-2-prompt.md`'s scope shipped and
every non-goal stayed out. Storage went from *working* to *usable*: a file can now be reached by the
person it belongs to and nobody else, seeked into by a media player, and — when a bucket explicitly
asks — rendered instead of downloaded.

## The shape of it

Three features, one theme: the file is reachable, now who and how.

- **Per-file permissions are the row-security analogue, and additively so.** The escalation order is
  copied from `QueryCompiler.PermissionPredicate` rather than improvised, and the property that
  matters is the one it is easiest to build backwards: **a bucket-level grant reaches every file and
  no per-file grant can claw it back.** "Users see only their own uploads" is configured by granting
  *nothing* at bucket level, not by restricting a grant that exists. No second authorization concept
  appears anywhere — same `IRoleResolver`, same intersect-the-roles check.
- **Range went into the store seam, not above it.** `IFileStore.OpenRead` grew an offset and length,
  so the Postgres backend narrows its cursor to the chunk rows the range actually covers and a future
  S3-compatible one can issue a native ranged `GET`. Skipping bytes off the front of a full stream
  would have worked today and quietly ruined that.
- **Inline serving is two gates, never one.** The bucket opts a type in *and* the type has to be in a
  hard-coded safe set. `X-Content-Type-Options: nosniff` stays on every response either way, and a
  `206` is still an `attachment` unless inline was opted into.

## What shipped

### Database — migration `20260904174349_StorageFilePermissions`

- **`praxy.file_permissions`** — `(file_id, action, role)`, PK all three, FK to `files`
  `ON DELETE CASCADE`. `TablePermission`'s field shape verbatim, one level down; the storage analogue
  of a row's `__perms` side table. An index on `(action, role)` serves the listing filter's `EXISTS`.
- **`praxy.buckets.inline_types`** (`text[]`, nullable) — the per-bucket opt-in list.
- `buckets.file_security` needed no migration: Phase 1 persisted it against exactly this, and this
  phase wired it up rather than adding a second flag.

### `src/Praxy.Storage` — five new files, all pure and all unit-tested

| File | What it is |
|---|---|
| `FileAccessRules.cs` | The escalation order as a pure function returning `Allow` / `Deny` / `PerFile`. |
| `FilePermissions.cs` | Parses `action("role")` for a file — read/update/delete only, mirroring `RowPermissions`. |
| `ByteRanges.cs` | `Range` header parsing against a real size: `Full` / `Partial` / `Unsatisfiable`. |
| `ChunkRange.cs` | Which chunk rows a byte range covers, and how much of the first to skip. |
| `InlineTypes.cs` | The hard-coded safe set, plus the both-gates check. |

They are separate from the services for one reason beyond tidiness: each is a place where a single
wrong branch produces something that still *looks* like it works — a permission that quietly allows,
a range that streams plausible but wrong bytes — so each gets its own test file rather than an `if`
buried in a service.

### The escalation order

```
bypassPermissions        -> Allow
bucket grants the action -> Allow      (per-file grants cannot narrow this)
!bucket.file_security    -> Deny       (401, exactly as before this phase)
otherwise                -> PerFile    (the file's own grants decide)
```

`FilesService.RequireAsync` is gone, replaced by `ResolveAsync` (reports what the bucket decided) plus
two callers that handle all three outcomes explicitly:

- `RequireBucketAsync` — for **create**, which is bucket-level or nothing: there is no file yet for a
  grant to hang off, the same reason a row can't grant its own creation.
- `RequireFileAsync` — loads the file and, on `PerFile`, requires a matching row in
  `file_permissions`. **A per-file miss is a `404`, not a `401`**, matching what a row the caller
  can't see returns, so the status code doesn't leak that someone else's file exists.

Both directions of that conversion are tested — the security hole (defaulting to allow) and the dead
feature (keeping the old throw) are opposite one-line mistakes.

### The listing path

The filter is folded into the EF query, never applied to the page afterwards:

```csharp
if (decision == FileAccessDecision.PerFile)
    query = query.Where(f => db.FilePermissions.Any(p =>
        p.FileId == f.Id && p.Action == PermissionStrings.Read && callerRoles.Contains(p.Role)));
var total = await query.CountAsync(ct);
```

Filtering after `Skip`/`Take` would report a `total` counting files the caller cannot see and hand
back short pages. The integration tests assert `total` as well as the rows, because a two-file test
that only reads the array passes either way.

### Realtime fan-out

A file event's read roles are now the bucket's `read` grants **plus** the file's own, when the bucket
opts in — the additive rule applied to the fan-out as it is to the query, so a subscriber who can
read a file through either level gets its events and one who can read it through neither gets
nothing. `ReadRolesAsync` mirrors `RowsService.ComputeReadRolesAsync`; delete captures them
pre-commit, since the rows cascade away with the file.

### Range

`IFileStore.OpenRead(fileId, chunkSizeBytes, offset, length)`. The Postgres store computes
`ChunkRange.For(...)` and issues one statement:

```sql
SELECT CASE WHEN "index" = @first THEN substr(data, @skip) ELSE data END
FROM praxy.file_chunks
WHERE file_id = @file_id AND "index" >= @first AND (@last < 0 OR "index" <= @last)
ORDER BY "index"
```

The leading partial chunk is trimmed by Postgres rather than read and discarded — with the column at
`STORAGE EXTERNAL` that skips the TOAST slices outright — and the trailing one is trimmed by the
stream's remaining-byte count. The arithmetic uses the file's **own** recorded `chunk_size_bytes`, so
retuning `Praxy:Storage:ChunkSizeBytes` can never misaddress a byte already stored.

HTTP behaviour: `Accept-Ranges: bytes` on every download; `206` + `Content-Range: bytes s-e/total`
with **`Content-Length` set to the part, not the file**; `416` + `Content-Range: bytes */total` for a
range past the end; suffix (`bytes=-500`) and open-ended (`bytes=500-`) both handled; multi-range
answered with the full `200` body, which RFC 9110 §14.2 explicitly permits and which no browser needs
multipart to avoid.

### Inline serving

`InlineTypes.Safe` is images (`png`/`jpeg`/`gif`/`webp`/`avif`), media (`video/mp4`, `video/webm`,
`audio/mpeg`, `audio/mp4`), `application/pdf` and `text/plain`. **`text/html` and `image/svg+xml` are
permanently excluded and not configurable.** A bucket's `inline_types` is validated against that set
when written (loud, so a bucket can't carry protection-shaped configuration that is silently ignored)
*and* intersected with it again when serving (the actual control, and what makes shrinking the set
take effect immediately on buckets already configured).

### API

New:

- `GET|PATCH /v1/storage/buckets/{bucketId}/files/{fileId}/permissions` and its console twin.
- `GET /v1/console/projects/{projectId}/storage/inline-types` — the server-owned vocabulary the
  console's picker renders, in the shape the functions surface's `/runtimes` already established.

Changed:

- Upload accepts repeated `?permissions=read("user:abc")`. Query, not body, because the body *is* the
  bytes — the one place an upload can't mirror a row's create payload exactly.
- `FileResponse` carries `$permissions`, spelled and shaped like a row's.
- `BucketResponse` carries `fileSecurity` and `inlineTypes`; create/update accept both.
- Downloads honour `Range` on both surfaces.
- One new error type: `file_range_not_satisfiable` (416).

`docs/openapi/v1.json` regenerated.

### Console

- **Bucket → Settings**: a *File security* toggle and an *Inline serving* picker, both with copy that
  says the thing that is easy to get backwards — that per-file grants are additive, and that the
  inline list is fixed and cannot include HTML or SVG.
- **Bucket → Files**: a *Permissions* button per row opening a sheet with the same
  `AddRoleButton`/`RoleLabel` matrix the row sheet uses, one column short (no `create`). The button
  carries the grant count, amber at zero when file security is on — a file nobody has been granted
  is unreachable by anyone the bucket matrix doesn't already cover, which is worth flagging.
- The download button stays a `fetch` + blob rather than becoming a plain link: for a bucket serving
  that type inline, a link would *render* the file in the console's own origin instead of saving it.

### SDKs

`permissions` on `createFile` in `@praxy/core` and `praxy_core`, and `$permissions` on both file
models (Dart decodes a missing field as empty rather than throwing, so an older server still parses).
Both READMEs gained the additive-not-restrictive paragraph at the upload method, and the Flutter
one's stale "HTTP `Range` … doesn't exist server-side" line is corrected. Range itself is an HTTP
concern neither SDK models — it works through whatever client the caller already has.

### Docs

`docs/self-host.md` gained **"Serving files inline"**: what the allow-list is, why `text/html` and
`image/svg+xml` can never be on it, why a separate origin is the real answer for rich user content,
and that Range needs no configuration.

## Verification

- **`dotnet test` green.** Five new unit test files (`FileAccessRulesTests`, `FilePermissionsTests`,
  `ByteRangesTests`, `ChunkRangeTests`, `InlineTypesTests`) plus additions to
  `ContentDispositionTests`, and a new integration file, `StorageAccessTests` — 16 tests against a
  real Postgres.
- **Console build clean** (`tsc -b && vite build`); `sdk/js` typecheck + tests green;
  `dart analyze .` clean (the 4 pre-existing `prefer_initializing_formals` infos in
  `realtime_socket.dart` are untouched) and `dart test praxy_core` / `flutter test` green.
- **Owner test, actually run** against the local instance (console at 5173, API at 5090):

  1. Created bucket **User uploads** in the console, toggled **file security** on there, and granted
     `create("users")` and *no read* on the bucket matrix.
  2. Signed up two app users and uploaded one file each through the data plane with
     `?permissions=read("user:<self>")&permissions=update("user:<self>")`.
  3. **Each user saw exactly their own file, and the count agreed**: `total=1`, `["alice.txt"]` for
     one and `total=1`, `["bob.txt"]` for the other. Alice fetching Bob's file: `404`. Her own:
     `200`, `attachment`, `nosniff`.
  4. Added the role **Anyone** to the bucket matrix from the console's own role picker (which grants
     `read("any")`, leaving `create("users")` in place). **Both users then saw both files —
     `total=2` each — with the per-file rows untouched**, and a session-less guest saw both too.
     That is the additive property, seen rather than assumed.
  5. Opened the per-file **Permissions** sheet in the console, ticked `delete`, and confirmed the
     grant persisted through the API.
  6. Ticked `text/plain` in **Inline serving**: that file then served
     `Content-Disposition: inline` while a `text/html` file uploaded to the same bucket still served
     `attachment` — both with `nosniff`.
  7. Uploaded a 1.5 MB file (3 chunks) and ranged into it: `bytes=1000000-1000099` returned `206`,
     `Content-Range: bytes 1000000-1000099/1500000`, `Content-Length: 100`, and bytes identical to
     that slice of a full download; `bytes=-500` matched the tail; `bytes=99999999-` returned `416`
     with `Content-Range: bytes */1500000` and a `file_range_not_satisfiable` envelope.

## Decisions this phase took where the prompt left room

1. **No auto-grant to the uploader.** The design doc raised it as an open product call and
   recommended following rows; that is what shipped. An upload with no `permissions` is unreachable
   by its own uploader in an owner-only bucket — surprising exactly once, and explicit forever. There
   is a test asserting it, so reversing the decision later is a deliberate act.
2. **A per-file miss is `404`, not `401`.** Matches rows; doesn't leak existence.
3. **`create`/`write` are refused on a file**, mirroring `RowPermissions`. A `create` grant on a file
   could never be consulted, so storing one silently would be dead configuration.
4. **The permissions endpoint is gated on `update` for the file**, not on a bucket-management scope —
   so a user granted `update("user:self")` on their own upload can re-share it without operator help.
   Bucket configuration stays key-scoped, as it was.
5. **Every range over a zero-length file is `416`**, per RFC 9110 (a request with no `Range` header
   is of course still a normal `200`).
6. **`OpenRead` takes the file's chunk size as a parameter**, the way `OpenWrite` already does,
   rather than the store re-reading the metadata row — a second round trip and a second source of
   truth for a value the caller is holding. A backend that doesn't chunk ignores it.
7. **The console's uploader doesn't send `permissions`.** The API and both SDKs do; in the console
   they are set from the file's Permissions sheet, since an operator uploading rarely knows them yet.

## Known gaps

- **Neither SDK wraps the per-file permissions endpoint** — upload-time grants only, which is what
  the prompt scoped. An app that lets users re-share their own files calls
  `PATCH …/files/{id}/permissions` directly for now.
- **Turning `file_security` off leaves the rows in place.** They stop being consulted and
  `$permissions` reads back empty (reporting them as live grants would be a lie), and they come back
  if it is turned on again. Same shape as `row_security`.
- **`If-Range` and `ETag`/`Last-Modified` are not implemented.** A client can range into a file that
  changed underneath it — though a stored file's bytes are immutable, so today that means only "was
  deleted and replaced under a new id".
- **No `multipart/byteranges`**, deliberately: multi-range gets the full body.
- **Inline content is still same-origin.** The safe-type allow-list is risk management; a separate
  origin would make it structural. Recorded in `docs/research/storage.md` and in `self-host.md` as an
  owner decision rather than assumed here.
- **A file's `$permissions` costs one extra query per page** (batched across the page, skipped
  entirely when file security is off).

## Commands

Nothing new to configure — Phase 2 added **no config knobs**. Both new controls are per-bucket
settings, set on the console's bucket **Settings** tab or through
`PATCH /v1/storage/buckets/{bucketId}`:

- `fileSecurity` (bool) — opt the bucket into per-file grants.
- `inlineTypes` (string[]) — types to serve inline; validated against the fixed safe set, which
  `GET /v1/console/projects/{projectId}/storage/inline-types` reports.

Everything else is as Phase 1 left it (`docs/handoff/storage-phase-1-report.md`'s Commands section).

## Where Phase 3 picks up

**No Phase 3 prompt is written, deliberately.** Image transforms are scoped in
`docs/research/storage.md` but undesigned — which library, where derivatives live, how they are
invalidated, and what a transform costs against the same chunk store — and that needs its own design
pass before a session tries to implement one.

Two things this phase leaves ready for it: the download path now has exactly one place where a
response's bytes are chosen (`FilesService.OpenDownloadAsync` → `StorageTransfer.DownloadAsync`), and
`IFileStore` can already express "part of a file", which a derivative cache will want.
