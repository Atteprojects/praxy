# Storage, Phase 3 (image transforms) — report

**Status: complete.** Every item in `docs/handoff/storage-phase-3-prompt.md`'s scope shipped and every
non-goal stayed out. This completes the Storage sequence — no Phase 4 prompt.

## The shape of it

One feature, three properties that had to hold simultaneously or the whole thing is a liability:

- **The URL space is bounded.** Requested dimensions snap up to a fixed ladder
  (64/128/256/512/1024/2048); a size above the top rung is a clean `400`, never a silent clamp. This
  is what stops `?width=1..2000` against one public image from generating two thousand cached rows.
- **A derivative is a representation, not a resource.** It carries no permissions of its own and
  resolves through exactly the source file's own `FileAccessRules` decision — no second check, no
  bypass for "it's just a thumbnail."
- **Decode limits run before the full decode, not after.** `SKCodec.Create` parses a header without
  allocating pixels; only a source whose *claimed* dimensions pass a configured pixel ceiling ever
  reaches the allocating `SKBitmap.Decode` call.

## What shipped

### Package pin — `docs/research/dotnet-stack.md`

**`SkiaSharp` / `SkiaSharp.NativeAssets.Linux.NoDependencies` 4.151.2**, verified against the raw NuGet
flatcontainer index (not an AI-summarized fetch) and each package's own nuget.org listing: both
current stable (4.152/4.153 are previews only), both target `net10.0`, both MIT. The NoDependencies
variant's own dependency list confirms **no fontconfig** — five ordinary glibc `.so` deps only,
already present in `dotnet/aspnet:10.0` — so `deploy/Dockerfile` needed no `apt-get` line. Confirmed
empirically too: `dotnet test` passes on this machine's Apple Silicon dev box as well as (later) the
Linux container, meaning the plain `SkiaSharp` package's own net10.0 dependency graph already carries
whatever native asset macOS needs — the Linux package is additive, not a replacement.

### Database — migration `20260905043155_StorageDerivatives`

- **`praxy.file_derivatives`** — `(id, file_id, width, height, format, quality, mime_type, size_bytes,
  chunk_size_bytes, chunk_count, checksum, created_at)`. FK to `files` `ON DELETE CASCADE`. Unique
  index on `(file_id, width, height, format, quality)` — the cache key, and the concurrency control
  (see below).
- **`praxy.file_derivative_chunks`** — `(derivative_id, index, data)`, PK both, FK to
  `file_derivatives` `ON DELETE CASCADE`, `data` forced to `STORAGE EXTERNAL` (same reasoning as
  `file_chunks`: an encoded image is already compressed, so the default `EXTENDED` strategy is pure
  CPU burn). **Deliberately its own table, not more rows in `file_chunks`** — that table's FK targets
  `files.id`, and a derivative is deliberately not a row in `files` (it would then be listable,
  permissionable, and countable as an independent resource, exactly what "a representation, not a
  resource of its own" rules out).
- **`praxy.files.width` / `height`** (nullable int) — the source's own decoded pixel dimensions,
  probed and cached here the *first* time any transform request needs them (never at upload time —
  most files are never transformed), so a request naming only one axis doesn't re-read and re-parse
  the whole source forever. Cleared back to `null` by `ReplaceBytesAsync` (see below) — the probe is
  only valid for the bytes it measured.

### `src/Praxy.Storage` — five new files

| File | What it is |
|---|---|
| `DimensionLadder.cs` | The six rungs and `SnapUp`, returning `null` above the top one. |
| `ImageTransforms.cs` | Pure key resolution: validates the source type, normalizes format/quality, resolves width/height (including the derived-axis and crop cases) — no database, no SkiaSharp. |
| `ImageTransformer.cs` | The actual SkiaSharp work: decode-limit check, resize/crop, encode. |
| `PostgresDerivativeChunkFileStore.cs` | A second, independent `IFileStore` implementation over `file_derivative_chunks`, reusing `PostgresChunkFileStore.CommandAsync` for ambient-transaction enlistment. |
| `DerivativesService.cs` | Orchestration: resolve-or-generate, the source-dimension probe, purge. |

`ImageTransforms` is deliberately free of both the database and SkiaSharp — the ladder snapping is the
security property, and it has to be testable in all four directions (below the first rung, exactly on
one, between two, above the top) without standing up either.

### The transform key and the crop rule

```
neither width nor height given -> the source's own dimensions, unchanged        (no crop)
only one given                 -> that axis snaps to the ladder; the other      (no crop)
                                   is derived from the source's own aspect ratio
both given                     -> each axis snaps to the ladder independently,  (crop)
                                   then the image is scaled to cover and
                                   center-cropped to that exact box
```

`quality` is a real, non-nullable `0` sentinel for lossless `png` rather than `null` — Postgres
treats every `NULL` in a unique index as distinct, which would have silently defeated the very
uniqueness the key exists to provide for the one format that needs it most (every `png` request would
otherwise be its own row, `quality` value and all). It is not snapped to a ladder of its own: it is
already bounded to 1-100, and for a fixed `(width, height, format)` that is a small, finite space
regardless — the same "bounded, not attacker-walkable" property the width/height ladder gives, without
needing the same mechanism.

**Bounded key space, precisely stated:** for one file, the "both axes given" shape has at most 6×6=36
possible rows; "one axis given" has at most 6 (the other axis is a deterministic function of the
source's fixed, non-attacker-controlled aspect ratio, never independently snapped); "neither" has
exactly 1. Multiplied by up to 3 formats and, for the two lossy ones, up to 100 quality values, the
total is a large but genuinely finite ceiling per file — never the unbounded one raw pixel dimensions
would have given before the ladder existed.

### Decode limits, precisely

```csharp
using var data = SKData.CreateCopy(sourceBytes);
using var codec = SKCodec.Create(data) ?? throw Undecodable();
if ((long)codec.Info.Width * codec.Info.Height > options.MaxSourceImagePixels)
    throw FileTransformSourceTooLarge(...);
using var source = SKBitmap.Decode(codec) ?? throw Undecodable();   // the actual allocation
```

`SKCodec.Create` parses only the header; `SKBitmap.Decode(codec)` is what allocates
`width × height × 4` bytes. The ceiling (`Praxy:Storage:MaxSourceImagePixels`, default 40 megapixels)
is checked strictly between the two calls. Only `image/png`, `image/jpeg`, `image/webp` are accepted
as source types at all — checked before either call, so an unsupported type never reaches the decoder.

### Concurrency: unique constraint, tolerate conflict, re-read

Two concurrent requests for the same missing derivative will both generate it — a lock was considered
and rejected as more machinery than the problem needs. Instead: insert the metadata row first (inside
the same transaction as its chunk bytes), and if that insert 23505s against the unique index, detach
the loser's tracked entity and re-read the winner's row rather than erroring. `StorageEngineTests`'
own `UploadAsync` established this exact pattern for a different table already, so it's a repeat of an
existing decision rather than a new one.

### Replacing a file's bytes in place — new capability, not just new plumbing

Phase 1 explicitly didn't have this ("the bytes of a stored file are immutable in Phase 1; replacing
them means a new upload"). Phase 3 needs it: a derivative is keyed by file id, and "upload a new file
instead" would leave the old file's stale derivatives sitting under an id nothing points at for
cleanup. `FilesService.ReplaceBytesAsync`, reached at `PUT /v1/storage/buckets/{id}/files/{id}` (and
the console's twin), gated on `update` — the same permission `RenameAsync` uses. One transaction:
delete the old chunk rows, purge the file's derivatives (`DerivativesService.PurgeAsync` — a bulk
`ExecuteDeleteAsync` that cascades to `file_derivative_chunks` via the FK), stream the new bytes,
update size/checksum/mime type, clear the cached `width`/`height` probe. A budget check accounts for
the file's *own* current bytes no longer counting toward "used" the moment the new ones land, so
replacing a file with one the same size or smaller can never spuriously trip the project quota.

### API

New:

- `PUT /v1/storage/buckets/{bucketId}/files/{fileId}` and its console twin — replace bytes in place.
- `GET|DELETE /v1/console/projects/{projectId}/storage/buckets/{bucketId}/files/{fileId}/derivatives`
  — list cached sizes (+ total bytes) and purge them. Console-only: the data plane only ever fetches
  *one* derivative, through the download endpoint's transform parameters.

Changed:

- `GET .../files/{fileId}/download` accepts `?width=`/`?height=`/`?format=`/`?quality=` on both
  surfaces. With none present, behavior (including `Range`) is unchanged from Phase 2. A transform
  request ignores `Range` entirely and always returns the whole generated image — advertised
  correctly: `Accept-Ranges` is no longer sent on a derivative response.
- `StorageTransfer.DownloadAsync` now takes one `FileDownload` (a small record with two factories,
  `ForFile`/`ForDerivative`) instead of separate file/range/content parameters, so the header-writing
  rules (attachment default, `nosniff`, the inline allowlist) are written once and apply identically
  to both paths — a derivative's *output* type goes through the exact same inline check a source
  file's *claimed* type does, deliberately: a transform's type is server-chosen and therefore safer,
  but "images are safe" is exactly how Phase 1's hole would reopen through a side door.
- Two new error types: `file_transform_invalid` (bad params, unsupported source type, above-ladder
  dimensions) and `file_transform_source_too_large` (the decode-limit rejection) — kept as two rather
  than one per condition, matching how `SiteInvalid`/`FunctionInvalid` already cover many validation
  submessages under one broad type.
- `QuotaService.UsedStorageBytesAsync` now sums `file_derivatives.size_bytes` alongside
  `files.size_bytes` — derivative bytes count against `MaxStorageBytesPerProject` like any other
  bytes, per the prompt.

`docs/openapi/v1.json` regenerated.

### Console

- **File sheet → Derivatives** (the same sheet the per-file Permissions matrix already uses — no new
  component): which sizes are cached, their format/quality, total bytes, and a **Purge all** button.
  Empty state points at the `?width=` query param rather than leaving a blank space.
- No new bucket-level settings — image transforms need no per-bucket opt-in; the safety controls
  (ladder, source-type allowlist, decode limits) are unconditional.

### SDKs

`getFileDownload` in both `@praxy/core` and `praxy_core` grew an optional `transform` parameter
(`FileTransformOptions` / `FileTransform`: `width`/`height`/`format`/`quality`), building the same
query parameters the backend endpoint accepts — no new method, since a derivative is fetched through
exactly the download call that already existed. Both READMEs' stale "image transforms don't exist
server-side yet" lines are corrected, with a transform example next to the existing download one.

### Docs

`docs/self-host.md` gained an **"Image transforms"** section (the ladder, the source-type allowlist,
the one config knob, the re-upload-purges-derivatives behavior) and a paragraph in **"Storage and
backup size"** explaining derivative bytes count against the same quota and are therefore bounded by
the same ladder. `docs/architecture.md`'s "Not built, still open" list no longer claims image
transforms are unbuilt (and, spotted in passing while editing that exact line, no longer claims table
relationships are either — those shipped 2026-09-01 and the line was stale independent of this phase).

## Verification

- **`dotnet test` green** — the whole solution, not just Storage: 560 + 24 new unit tests
  (`DimensionLadderTests`, `ImageTransformsTests`, `ImageTransformerTests` — including a
  header-claims-more-pixels-than-the-configured-ceiling case, built from a genuine tiny PNG rather
  than a crafted decompression-bomb fixture, against an artificially lowered limit) and 314
  integration tests including 7 new (`StorageDerivativesTests`) against a real Postgres:
  dimensions + stable checksum on repeat, cache-hit asserted via derivative row count (not timing),
  permission inheritance (a caller denied on the source generates nothing), cascade-on-delete,
  purge-on-replace, the inline-serving default holding for a derivative's own type, and the
  above-the-ladder `400`.
- **Console build clean** (`tsc -b && vite build`); `sdk/js` typecheck + 84 tests green (3 new);
  `dart analyze .` clean (same 4 pre-existing, untouched infos Phase 2's report already noted) and
  `dart test praxy_core` — 80 tests, 6 new — green.
- **Owner test, actually run** against the local instance (console at 5173, API at 5090, real dev
  Postgres):

  1. Uploaded a 600×400 PNG (generated in-browser via `<canvas>`, no external fixture needed) to the
     existing **User uploads** bucket through the console.
  2. Requested `?width=150`: got a real `256×171` PNG back (`171 = round(400 × 256/600)`), `200`,
     `attachment`, `nosniff` — confirmed via `SKCodec` against the actual response bytes, not just
     the reported content-length.
  3. Opened the file's sheet: **Derivatives** showed `1 cached, 8.0 KB total` and `256×171 · png`
     with its size, matching the request above exactly.
  4. Clicked **Purge all**: toast confirmed, the section reverted to its empty state, and a fresh
     `?width=150` request regenerated a new row rather than serving anything stale.
  5. Deleted the test file and confirmed cleanup left no test artifacts in the shared dev instance.

## Decisions this phase took where the prompt left room

1. **Insert-tolerate-conflict-reread over a lock**, per the prompt's own suggestion — recorded here
   because the prompt explicitly asked the report to say which was chosen.
2. **A derivative's chunk bytes live in a new table (`file_derivative_chunks`), not more rows in
   `file_chunks`.** The alternative — giving a derivative its own row in `files` so it could reuse the
   existing chunk store verbatim — was rejected because it would make a derivative listable,
   permissionable, and countable as an independent file, exactly the "resource of its own" the design
   doc rules out. The cost is a second, small `IFileStore` implementation; the alternative's cost was
   a real second authorization surface.
3. **Only `png`/`jpeg`/`webp` are decodable source types**, narrower than `InlineTypes.Safe`.
   Animated GIF (frame semantics nobody asked for) and AVIF (less exercised) are left out rather than
   assumed to "just work" against attacker-supplied bytes — consistent with the prompt's non-goals,
   even though the prompt didn't enumerate the exact source-type list itself.
4. **The source's probed pixel dimensions are cached on the file row (`files.width`/`height`),
   populated lazily on first need,** rather than probed on every request that omits one axis. Without
   this, a `?width=` alone request would re-read and re-parse the whole source file on every single
   request forever, even for a fully cached derivative — the header parse itself is cheap, but paying
   it on every hit defeats the point of caching. A `?width=`+`?height=` request never touches the
   source at all, cached or not, since that shape needs no source dimensions to resolve its key.
5. **Replacing a file's bytes is a new capability (`PUT`), not something repurposed from an existing
   route**, gated on `update` like renaming. The alternative — deleting and re-uploading — was
   rejected because it changes the file's id, and derivative invalidation (the whole reason this
   capability exists) is specifically about the *same id* carrying stale derivatives forward.
6. **`quality`'s lossless-png value is a real `0`, not `null`,** for the unique-index reason explained
   above — a `null` sentinel would have silently broken deduplication for exactly the format most
   likely to be requested without a quality param at all.

## Known gaps

- **No animated GIF, no SVG rasterization, no smart/face-aware cropping, no CDN** — all explicitly
  out of scope per the prompt, and none attempted.
- **The crop is always center-crop.** No focal-point or smart-crop option; "resize/crop" means
  cover-then-center, not an arbitrary crop rectangle.
- **A derivative's source-dimension probe and its own generation both buffer the whole source file
  into memory.** Bounded by the bucket's own upload-size ceiling (already enforced at upload time),
  and unavoidable either way — decoding an image needs its full encoded bytes regardless of whether
  the codec is asked to seek.
- **No resumable/chunked upload protocol** — unrelated to this phase specifically, but the one piece
  of the original Storage design doc's upload section that stays unbuilt across all three phases.

## Commands

One new knob, `Praxy:Storage:MaxSourceImagePixels` (default 40,000,000 — 40 megapixels): the decoded
pixel-count ceiling checked against a source image's header before the full decode. Everything else
needed no new configuration — the ladder, the source-type allowlist, and the inline-serving rule reuse
are unconditional, not per-bucket settings.

Everything else is as Phases 1-2 left it (`docs/handoff/storage-phase-1-report.md` and
`storage-phase-2-report.md`'s own Commands sections).

## This completes the Storage sequence

No Phase 4 prompt. Three phases: the primitive (chunked bytes, buckets, permissions mirroring
tables), access control and serving (per-file permissions, Range, opt-in inline), and image transforms
(cached derivatives, bounded by a fixed ladder, resolving through the source's own permissions with no
second check). What's left — resumable client uploads, a CDN, signed URLs, antivirus scanning — is
recorded as deliberately out of scope rather than deferred by omission.
