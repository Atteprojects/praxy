# Storage — design

## Context

Praxy has no file storage at all today — no `src/Praxy.Storage`, and `docs/roadmap.md` mentions it only
in passing as "its own future initiative" alongside TOTP and multi-org. That makes it the largest
remaining gap in the product rather than a refinement: every comparable BaaS ships a Storage product,
and a Praxy app currently has nowhere to put an avatar, an attachment, or an export.

It is also the first feature since the original v0.1.0 scope that is a genuinely *new pillar* rather
than an extension of Tables. Relationships and geo added column types to an existing engine; Sites
reused Functions' container machinery. Storage introduces its own resource hierarchy, its own
permission surface, and — the part worth designing carefully — its own answer to "where do the bytes
actually go."

## The decision everything hangs off: bytes in Postgres, chunked

**Chosen by the owner, 2026-09-03, over local disk and over plain single-column `bytea`.**

`CLAUDE.md`'s fixed decisions include **"PostgreSQL only — no second datastore"**, and the codebase
already has a precedent: both `FunctionDeploymentSource.Tar` and `SiteDeploymentSource.Tar` are
`bytea` columns (`PraxyDb.cs:337`, `:403`). Storage follows the same rule, with one change forced by
scale — **files are split across fixed-size chunk rows rather than living in a single `bytea` value.**

Why chunking rather than copying the Functions/Sites shape directly:
- A single `bytea` value is capped at **~1 GB** by Postgres, and TOAST has to materialize it to read
  it. That's acceptable for a deployment tarball; it is not acceptable for a user-facing file store.
- Chunking is what makes **streaming** possible in both directions. An upload writes chunk rows as the
  request body arrives; a download reads them back in order. Neither ever holds a whole file in memory,
  which a single-column design cannot avoid.
- It makes HTTP **Range** requests cheap later (seek to `offset / chunkSize`, skip the rest) — the
  difference between a file store you can serve video from and one you can't.

What this costs, stated plainly rather than discovered later:
- **Every stored byte lands in every backup.** `deploy/backup.sh` `pg_dump`s the `praxy` schema, so a
  5 GB file store means 5 GB dumps, every run. This is inherent to keeping files in the database and is
  not solved by any amount of cleverness — it is *managed*, by a per-project storage quota (below) and
  by documenting the growth in `docs/self-host.md`. A `backup.sh` flag to skip the chunk table is a
  reasonable later addition for operators who back files up separately; it is deliberately not in v1,
  because a backup that silently omits data is worse than a large one.
- Deleting large files leaves substantial dead tuples. Normal autovacuum territory, but worth saying
  out loud since no existing Praxy table churns at this volume.

**Set the chunk column's storage to `EXTERNAL`, not the default.** Postgres's default `EXTENDED`
storage tries to LZ-compress every `bytea` before storing it out of line. For the media that dominates
a real file store — JPEG, PNG, MP4, ZIP, anything already compressed — that is CPU spent to achieve
nothing. `ALTER TABLE ... ALTER COLUMN data SET STORAGE EXTERNAL` keeps values out of line and skips
the compression attempt. This is a one-line migration detail that is very easy to miss and impossible
to notice from behavior alone.

**Chunk size: 512 KiB, configurable.** Small enough that a chunk is a comfortable buffer, large enough
that a 100 MB file is ~200 rows rather than thousands. The implementing session should confirm the
figure against real timings rather than treat it as settled — it is a tuning constant, not an
invariant, and it must be recorded per-file (below) so changing the default never breaks existing data.

**Behind an interface, so this is reversible.** The chunk store sits behind a narrow
`IFileStore`-shaped seam (open a write stream, open a read stream, delete). Postgres-chunked is the
only implementation in v1, but the seam is what lets a disk or S3-compatible backend be added later
*without touching the API surface, the metadata model, or the permission path*. This is the specific
thing that keeps today's decision from becoming a trap.

## Resource model — deliberately the Tables model, not a new one

Storage maps onto the shape this engine already has, so that permissions, events and the console
behave the way developers have already learned:

| Storage | Tables analogue | Why |
|---|---|---|
| **Bucket** | Table | The configuration + permission boundary. |
| **File** | Row | The thing addressed, permissioned and evented. |
| `BucketPermission (BucketId, Action, Role)` | `TablePermission` | **Identical shape**, same four storable actions. |
| Per-file permissions (Phase 2) | Row security + `__perms` | Same opt-in flag, same side-table pattern. |

`CLAUDE.md`'s cross-phase rule — *"One role resolver — query compiler and realtime fan-out consume the
same implementation"* — applies unchanged: bucket access is `bucket.Roles(action).Intersect(callerRoles)`,
the same check `CatalogEntry.TableRoles` already performs, against roles from the same
`IRoleResolver`. No second authorization concept is introduced anywhere in this design, and any phase
that finds itself wanting one should stop and re-read this paragraph.

**Deny by default**, per the same cross-phase rule: a new bucket is unreachable until permissions are
granted, exactly like a new table.

```
Bucket:  id, project_id, key, name, enabled, file_security (Phase 2),
         max_file_size_bytes, allowed_mime_types (nullable = any), created_at, updated_at
File:    id, bucket_id, name, mime_type, size_bytes, chunk_size_bytes, chunk_count,
         checksum, created_at, updated_at
Chunk:   file_id, index, data bytea   -- PK (file_id, index), data SET STORAGE EXTERNAL
```

`chunk_size_bytes` lives on the **file**, not in config, so the tuning constant can change for new
uploads without invalidating a single existing file. Getting this wrong is unrecoverable without a data
migration, which is why it's stated here rather than left to the implementation.

## Upload and download

**Upload is a single streaming request in v1**, not a resumable multi-part protocol. The body streams
straight into chunk rows inside one transaction: either the whole file and its metadata commit, or
neither does, so a failed upload can never leave an orphaned half-file. Resumable/chunked *client*
protocols (Appwrite has one, for very large files over poor connections) are real but separable work,
and they only make sense once the simple path exists.

**Kestrel's default 30 MB request body cap will reject uploads long before any Praxy limit does.** It
has to be raised explicitly, and the effective cap has to come from the same configured value the
quota check uses, or the two will disagree and produce a confusing failure at a size nobody configured.
This is the single most likely thing to be missed in Phase 1.

**Download streams chunks in order.** HTTP `Range` is deliberately Phase 2 — the chunk layout is chosen
to make it easy, but shipping it in v1 would mean getting partial-content semantics right on top of
everything else that's new.

## Downloads are never renderable — a security control, not an ergonomics choice

**This section corrects the original design, which got it wrong.** The first version of this doc
listed "correct `Content-Disposition` inline-vs-attachment handling" as a Phase 2 nicety, alongside
`Range`. That framing is what led Phase 1 to ship a download endpoint that echoed the uploader's own
`Content-Type` back with no `Content-Disposition` and no `nosniff` — caught in review before merge.

The chain that makes it exploitable, and why it is not theoretical:
1. A file's stored MIME type is whatever `Content-Type` the *uploader* sent (`MimeTypes.Normalize`
   accepts any well-formed `type/subtype`, `text/html` and `image/svg+xml` included).
2. A bucket's `allowed_mime_types` is null — any type — by default.
3. The console is served from the **same origin as the API** (`UseStaticFiles` +
   `MapFallbackToFile("index.html")`), and the operator session cookie is `SameSite=Lax`.

So an uploaded `text/html` file, opened by an operator, runs script **same-origin with the console**.
`HttpOnly` stops that script reading the cookie; it does not stop a same-origin `fetch('/v1/console/…')`
from sending it. A bucket granting `read("any")` — the obvious configuration for avatars or public
assets — makes the URL reachable by anyone.

**The rule: every download is `Content-Disposition: attachment` plus `X-Content-Type-Options: nosniff`.**
The real `Content-Type` is still reported, because it is useful metadata and harmless once the
response cannot become an active document. The attachment header stops rendering; `nosniff` stops the
browser sniffing its way back to rendering.

**Inline serving is a Phase 2 feature that must be opt-in and allowlisted** — a per-bucket flag plus a
list of types that are safe to render (images and video, never `text/html`, never `image/svg+xml`,
which carries script). It must never arrive by simply dropping the two headers above.

**The file name is a header-injection boundary**, since it goes into `Content-Disposition`. Defended
on both sides: `FilesService.ValidateName` rejects control characters as a class (not just NUL), and
`ContentDisposition.Attachment` drops everything outside printable ASCII from the quoted form while
carrying the real name percent-encoded in `filename*` (RFC 6266).

## Quotas

Three new dimensions on the existing `QuotaOptions` record (and therefore, for free, overridable
per-organization through the existing `OrganizationLimits` jsonb — the mechanism already exists and
needs no changes):

- `MaxBucketsPerProject`
- `MaxFileSizeBytes` — the per-file ceiling, also the value Kestrel's body limit must be derived from
- `MaxStorageBytesPerProject` — **the answer to backup growth.** Without it, an instance's backup size
  is unbounded by anything, which is the one genuinely operational consequence of this design.

Enforcement follows the existing `QuotaService.EnsureXQuotaAsync` shape. Per `CLAUDE.md`'s "every limit
is configurable and loud when tripped", exceeding these is a clear typed error, never a truncated write.

## Events

Storage writes go through the outbox (`praxy.events`) like every other write since Phase 3, which gets
realtime fan-out and webhooks for free. Channel grammar follows the existing pattern exactly —
`buckets.{bucketId}.files.{fileId}.create|update|delete`, wildcarding the same way
`databases.*.tables.*.rows.*.create` already does, so `ChannelGrammar`'s powerset logic needs a new
prefix rather than a new mechanism.

## Phased rollout

- **Phase 1 — the primitive**: `Praxy.Storage` project, bucket CRUD + permissions, streaming upload,
  full-file download, delete, the chunk store behind its interface, the three quotas, outbox events,
  console screens (bucket list, file browser, upload, permissions), and Flutter/JS SDK upload+download.
  **Non-goals**: no per-file permissions, no Range requests, no image transforms, no resumable uploads,
  no encryption at rest, no antivirus.
- **Phase 2 — access control and serving**: per-file permissions (the row-security analogue, same
  opt-in flag + side table), HTTP Range, and *opt-in* inline serving with a safe-type allowlist.
  Designed in full below. **Shipped 2026-09-04** — kickoff:
  `docs/handoff/storage-phase-2-prompt.md`, report: `docs/handoff/storage-phase-2-report.md`.
- **Phase 3 — image transforms**: on-the-fly resize/crop/format/quality with a cached derivative.
  Designed in full below; kickoff: `docs/handoff/storage-phase-3-prompt.md`.

**Explicitly out of scope for the whole sequence**: a CDN integration, signed time-limited URLs, and
antivirus scanning. Each is a legitimate future initiative; none is needed for Storage to be useful.

## Phase 2 — designed 2026-09-04

Three items that share a phase because they are all "the file is reachable, now who and how".

### Per-file permissions — the row-security analogue, additively

Phase 1 already reserved `bucket.file_security` in the data model for this. The escalation order is
copied verbatim from `QueryCompiler.PermissionPredicate`, which is the shape to match rather than
invent:

```
bypassPermissions            -> allow
bucket grants the action     -> allow
!bucket.file_security        -> deny
otherwise                    -> EXISTS(file_permissions where file_id, action, role = ANY(callerRoles))
```

**The property that matters most, and the one people get wrong: this is additive, not restrictive.**
A bucket-level `read("any")` grant means *everyone reads every file*, and no per-file grant can take
that away — exactly how table-level grants override row security today. So the headline use case,
"users can only read their own uploads", is configured by granting **no bucket-level read at all**,
turning `file_security` on, and attaching `read("user:<id>")` to each file. A design that lets a
bucket grant coexist with per-file restriction would be a *different* model from tables, and this
codebase has one authorization concept on purpose.

New table, mirroring `TablePermission`'s shape exactly:

```
FilePermission: file_id (FK -> files, cascade), action, role   -- PK (file_id, action, role)
```

**The listing path is where this gets hard, and it is not optional.** `FilesService.ListAsync` today
does `db.Files.Where(f => f.BucketId == ...)` then `CountAsync` + `Skip`/`Take`. The permission filter
**must go into that EF query**, not into a post-pagination filter in memory — otherwise `total` counts
files the caller cannot see and pages come back short or empty. This is the direct analogue of the
compiled `EXISTS` the query compiler folds into its `WHERE`:

```csharp
if (!bucketGrantsRead && bucket.FileSecurity)
    query = query.Where(f => db.FilePermissions.Any(p =>
        p.FileId == f.Id && p.Action == PermissionStrings.Read && callerRoles.Contains(p.Role)));
```

**`FilesService.RequireAsync` has to stop being fatal for reads.** Today it throws when the bucket
does not grant the action — which is correct while bucket-level is the only level, and wrong the
moment per-file grants exist: a caller with no bucket grant but a per-file grant must get their file,
not a 403. Converting that gate from "throw" to "returns whether the bucket already allows it" is the
single most likely place to introduce either a security hole (defaulting to allow) or a broken feature
(keeping the throw). It deserves its own tests in both directions.

**Open decision for the owner: does upload auto-grant the uploader?** Rows do not — permissions are
explicit on create. Appwrite does. Consistency with tables says no auto-grant; ergonomics says almost
every caller wants `read("user:<self>")` on their own upload and will write that boilerplate every
time. Recommend following rows (explicit, no magic) and revisiting if it proves annoying, but this is
a product call, not a technical one.

### HTTP Range — push it into the seam, do not implement it above

The chunk layout was chosen in Phase 1 to make this cheap, and the per-file `chunk_size_bytes` (stored
on the row rather than read from config) is what makes the arithmetic exact even after the configured
default is retuned:

```
firstChunk  = start / file.ChunkSizeBytes
skipInFirst = start % file.ChunkSizeBytes
lastChunk   = end   / file.ChunkSizeBytes
        SELECT data FROM praxy.file_chunks
        WHERE file_id = @id AND "index" BETWEEN @first AND @last ORDER BY "index"
```

**`IFileStore.OpenRead` must grow the range, rather than the endpoint skipping bytes off the front of
a full stream.** Reading-and-discarding works for the Postgres backend and would quietly destroy the
next one: an S3-compatible store serves a range with a native ranged `GET`, and a seam that cannot
express "bytes 5,000,000-5,000,999" forces it to fetch the whole object to serve 1 KB. The seam exists
precisely to keep that option open, so the signature becomes `OpenRead(Guid fileId, long offset,
long? length)`.

Required HTTP behaviour, all of it standard and all of it easy to half-do:
`Accept-Ranges: bytes` advertised on full responses; `206` with `Content-Range: bytes s-e/total` for a
satisfiable range; `416` with `Content-Range: bytes */total` for one past the end; suffix (`bytes=-500`)
and open-ended (`bytes=500-`) forms both handled. **Multi-range (`bytes=0-99,200-299`) should be
answered with the full `200` body** rather than `multipart/byteranges` — the spec explicitly permits
ignoring a Range header, and a multipart encoder is a lot of surface for a case no browser needs.

Range is orthogonal to `Content-Disposition`: a partial response is still an `attachment` unless
inline has been opted into.

### Inline serving — opt-in, allowlisted, and still not the safest option

Read "Downloads are never renderable" above first; this section only adds the opt-in. A per-bucket
`inline_types` allowlist, empty by default, and a response is served inline only when the file's type
is in it. `X-Content-Type-Options: nosniff` stays on **every** response either way.

The allowlist is a hard-coded set of types that cannot execute — images (`image/png`, `image/jpeg`,
`image/gif`, `image/webp`), `application/pdf` if wanted, `text/plain`. **`text/html` and
`image/svg+xml` are permanently excluded**, not configurable: SVG carries script, and that is the whole
vulnerability again with an extra step.

**The stronger option, which is an owner decision rather than a default:** serve user content from a
*separate origin* the way Sites already does (`<key>.<projectId>.{Praxy:Sites:Domain}`). Same-origin
inline content is inherently a risk-management exercise, whereas a different origin makes it a
non-issue structurally — a compromised inline asset cannot reach the console's cookies at all. The
subdomain machinery already exists and is proven. It is more moving parts (DNS, a second Caddy block,
CORS for the SDKs), which is why it is raised here rather than assumed; if inline serving is expected
to carry anything richer than a thumbnail, it is the right answer.

## Phase 3 — designed 2026-09-04

On-the-fly resize/crop/format/quality, the one Storage feature developers ask for by name. Three
questions had to be settled before any of it: which library, where derivatives live, and how the URL
space is bounded. The third turned out to matter most and is the one an implementation is most likely
to get wrong.

### The library: SkiaSharp, and the reason is operational rather than technical

**ImageSharp is the obvious .NET answer and it is the wrong one here.** Since June 2022 it ships under
the Six Labors Split License: open-source consumers and businesses under $1M revenue stay on Apache-2.0,
which Praxy itself qualifies for. That is not the problem. **v4.0.0 added build-time licence
enforcement requiring a `sixlabors.lic` file to compile a project that depends on it** — and Praxy's
self-host path is `docker compose up --build`, which builds the API from source on the operator's own
machine. That enforcement would land on *every self-hoster's build*, not just ours. The alternative is
pinning v3.x forever and forgoing security updates on an image decoder, which is precisely the
component you least want frozen.

**SkiaSharp is MIT**, with no commercial tier and no build-time enforcement, and
`SkiaSharp.NativeAssets.Linux.NoDependencies` ships a `libSkiaSharp.so` built without third-party
dependencies — **fontconfig included in what it does not need**, because this feature renders no text.
So it needs no `apt-get` line in the Dockerfile. The runtime image is `dotnet/aspnet:10.0`, which is
Debian/glibc, so the ordinary Linux native assets apply; only the console build stage is Alpine, and it
never touches this code.

**The honest trade, stated rather than buried:** ImageSharp is fully managed, so a malformed-image bug
is a .NET exception. Skia is C++, so the same bug is potentially memory corruption — and every byte it
decodes is attacker-supplied. Skia is among the most heavily fuzzed codebases in existence (it is
Chrome's graphics engine), which is real mitigation but not a guarantee. The limits in "Bounding the
damage" below are what actually contains this, and they would be needed with either library.

Per `CLAUDE.md`, the exact version goes through `docs/research/dotnet-stack.md`'s verify-and-pin
discipline; this doc deliberately does not name one.

### Bounding the URL space — the part that is easy to get wrong

A transform URL like `?width=237` is a **storage-amplification vector** the moment derivatives are
cached: an attacker walks `width=1..2000` against one public image and creates two thousand cached
derivatives from a single source. Appwrite accepts arbitrary dimensions; Praxy should not.

**Requested dimensions snap up to a fixed ladder** — 64, 128, 256, 512, 1024, 2048 — so `?width=237`
is served by the 256-wide derivative. The key space per source file is therefore small and fixed, the
cache cannot be walked, and callers still get "about this big" without needing to know the ladder.
Anything above the top rung is rejected rather than silently clamped, because silently returning a
smaller image than asked for is the kind of surprise that costs an afternoon to debug.

This also disposes of the quota interaction cleanly: derivatives count against
`MaxStorageBytesPerProject` like any other bytes, and a bounded ladder means that total is predictable
rather than attacker-controlled.

### Where derivatives live

**As ordinary files in the same chunk store**, in a `file_derivatives` table keyed by
`(file_id, width, height, format, quality)` and pointing at their own chunk rows. Same `IFileStore`
seam, same streaming read, same backup story — no new storage concept, and the "PostgreSQL only" rule
holds without an exception.

Invalidation falls out of the schema rather than needing a sweeper: the FK to `files` is
`ON DELETE CASCADE`, so deleting a source drops its derivatives and their bytes in one statement, and
**re-uploading over a file id must purge its derivatives explicitly** — that is the one case the
database will not do for you, and the one most likely to ship as a stale-thumbnail bug.

### What it inherits from Phases 1 and 2, and must not weaken

- **Permissions are the source file's.** A derivative is a representation of that file, not a
  separate resource with its own grants. It resolves through exactly the same `FileAccessRules`
  escalation — no second check, no bypass for "it's just a thumbnail".
- **`nosniff` on every response, and the attachment default still holds.** A transform's output type is
  server-chosen (the encoder's), not the uploader's, which makes it *safer* than the source — but
  inline still requires the bucket to have opted that type in. A transform endpoint that quietly serves
  inline because "images are safe" reopens Phase 1's hole through a side door.
- **Only decode types the transform pipeline claims to support**, and validate the decoded dimensions
  *before* allocating the output. A 100×100 JPEG that decodes to 30,000×30,000 is a decompression bomb;
  the ladder caps the output but the *source* needs its own pixel ceiling, checked after header parse
  and before the full decode.

### Deliberately out of scope

No animated-GIF or video thumbnailing, no smart/face-aware cropping, no SVG rasterisation (it is
excluded from inline serving for the same reason it should not be decoded), and no CDN. Each is a
separate initiative, and the first three all widen the decoder attack surface for a feature nobody has
asked for yet.

## Verification

This doc's claims about the current codebase were checked directly rather than assumed: the absence of
any storage implementation (`ls src/Praxy.Storage`, plus a roadmap grep), the existing `bytea`
precedent and its exact EF mapping (`PraxyDb.cs:337`/`:403`), `TablePermission`'s field shape as the
model for `BucketPermission`, `PermissionStrings.StorableActions` as the action set, `QuotaOptions`'s
record shape and its `OrganizationLimits` override mechanism, `RoleResolver`'s principal types, and
`ChannelGrammar`'s existing channel construction. The Postgres-side claims — the ~1 GB `bytea` ceiling
and `EXTENDED`-vs-`EXTERNAL` compression behavior — are standard documented Postgres behavior and
should still be confirmed against a real container by the implementing session, in keeping with this
repo's "pull it, don't trust the tag" discipline.

The Phase 1 session (kickoff: `docs/handoff/storage-phase-1-prompt.md`) owns its own verification:
`dotnet test`, the console owner-test, and a real end-to-end upload/download of a file large enough to
span many chunks with its bytes compared byte-for-byte on the way back out.
