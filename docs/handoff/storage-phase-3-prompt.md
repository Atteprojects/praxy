# Session task — Storage, Phase 3 (image transforms)

## Why this exists

Phases 1 and 2 shipped buckets, chunked bytes, per-file permissions, Range and opt-in inline serving.
Both are deployed. Phase 3 adds the one Storage feature developers ask for by name: **on-the-fly
resize/crop/format/quality with a cached derivative**.

Read `docs/research/storage.md` in full first — its **"Phase 3 — designed 2026-09-04"** section is the
complete design, and **"Downloads are never renderable"** is a security control this phase must not
weaken through a side door. This prompt assumes you've read both. Work on a new branch off `main`.
Read `CLAUDE.md` first.

Two things to internalise before writing code, because they are the ones an implementation gets wrong:

1. **The URL space must stay bounded.** Arbitrary `?width=` with a cache is a storage-amplification
   vector — walk `width=1..2000` against one public image and you have created two thousand
   derivatives from one source. Dimensions snap up to a fixed ladder; the design doc says why.
2. **A derivative is a representation of its source file, not a resource of its own.** It inherits the
   source's permissions and resolves through the same `FileAccessRules` escalation. No second check,
   no shortcut for "it's just a thumbnail".

## Non-goals

- **No animated GIF or video thumbnailing, no SVG rasterisation, no smart/face-aware cropping, no CDN.**
  The first three widen the decoder attack surface for something nobody has asked for.
- **No new authorization concept**, and no bypass of Phase 2's per-file check.
- **Do not weaken the attachment default.** A transform's output type is server-chosen, which makes it
  safer than an uploaded file — but inline still requires the bucket to have opted that type in.
  "Images are safe" is how Phase 1's hole gets reopened through a side door.
- **Do not accept arbitrary dimensions**, even without caching. The ladder is the design.

## Scope

1. **The library: SkiaSharp**, specifically `SkiaSharp.NativeAssets.Linux.NoDependencies` so no
   `apt-get` line is needed in `deploy/Dockerfile` — its `libSkiaSharp.so` is built without
   third-party dependencies, fontconfig included in what it doesn't need (this feature renders no
   text). The runtime image is `dotnet/aspnet:10.0`, Debian/glibc, so ordinary Linux native assets
   apply. **Pin the exact version through `docs/research/dotnet-stack.md`'s verify-and-pin
   discipline** — that file is the authority, not this prompt and not memory.

   The design doc explains why **not** ImageSharp: its v4 build-time licence enforcement would land on
   every self-hoster's `docker compose up --build`. Don't re-litigate it; if you find a fact that
   changes it, say so in the report rather than switching libraries unilaterally.
2. **The dimension ladder** — 64, 128, 256, 512, 1024, 2048. A request snaps **up** to the next rung.
   Above the top rung is a clean `400`, not a silent clamp: returning a smaller image than asked for
   is a debugging afternoon nobody needs.
3. **`file_derivatives` table + migration**, keyed by `(file_id, width, height, format, quality)`,
   pointing at its own chunk rows through the existing `IFileStore` seam. FK to `files` is
   `ON DELETE CASCADE`.
4. **Re-upload over a file id must purge that file's derivatives explicitly.** This is the one
   invalidation the database will not do for you, and the most likely stale-thumbnail bug.
5. **Transform parameters on the download endpoint** (`?width=`/`?height=`/`?format=`/`?quality=`).
   With none present the endpoint behaves exactly as it does today — including Range, which is a
   full-file concern and does not apply to a generated derivative.
6. **Decode limits, before the full decode.** Parse the header, check source dimensions against a
   pixel ceiling, and reject a decompression bomb (a 100×100 file that decodes to 30,000×30,000)
   before allocating anything. Only decode types the pipeline claims to support.
7. **Quota**: derivative bytes count against `MaxStorageBytesPerProject` like any other bytes. The
   bounded ladder is what keeps that total predictable.
8. **Console**: show derivatives on the file sheet (which sizes exist, total bytes), and a way to purge
   them for a file. Follow the existing file-sheet patterns; don't build new components.
9. **SDKs**: transform parameters on the download URL builder in `@praxy/core` and the Flutter SDK.
10. **Docs**: `docs/self-host.md` gains a note that derivatives consume storage quota and therefore
    backup size, pointing at the ladder as the bound.

## Landmines

- **The ladder is a security control, not an ergonomic one.** Accepting arbitrary dimensions "just for
  now" is the whole vulnerability.
- **Permissions inheritance** — resolving a derivative through anything other than the source file's
  `FileAccessRules` decision is a second authorization path, which is exactly what Phases 1-2 avoided.
- **`nosniff` and the inline opt-in still apply.** Both must be identical to the untransformed path.
- **Decode before allocate is backwards.** Check dimensions from the header first.
- **Concurrent requests for the same missing derivative** will both generate it. Decide what happens —
  a unique constraint on the key plus "insert, tolerate conflict, re-read" is fine and is simpler than
  a lock. Say which you chose in the report.
- **SkiaSharp is native code decoding attacker-supplied bytes.** The limits above are what contains
  that; they are not optional hardening to add later.

## Tests

- Unit: ladder snapping (below the first rung, exactly on a rung, between rungs, above the top);
  derivative key equality; the decode-limit check against a header claiming enormous dimensions.
- Integration (real Postgres): a transform produces the expected pixel dimensions and a stable
  checksum on repeat; the second request for the same transform serves the cached row rather than
  re-encoding (assert via the derivative row count, not timing); a caller who cannot read the source
  cannot read a derivative of it; deleting the source cascades its derivatives; **re-uploading over
  the file id purges them**; a bucket that has not opted the output type into inline still gets
  `attachment`; a request above the top rung is a clean `400`.

## Done means

- `dotnet test` green (unit + integration).
- Console build clean (`tsc -b && vite build`); `sdk/js` typecheck/test green if touched.
- **Owner test, actually run**: upload a large photo, request it at a couple of sizes, confirm the
  rendered dimensions look right and the second request is served from cache; confirm a size above the
  ladder errors clearly; re-upload over the same file id and confirm the old thumbnails are gone rather
  than stale.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/storage-phase-3-report.md`. This completes the Storage sequence — no Phase 4
  prompt.
