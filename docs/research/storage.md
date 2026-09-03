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
  opt-in flag + side table), HTTP Range, and *opt-in* inline serving with a safe-type allowlist —
  see "Downloads are never renderable" below for why inline is opt-in rather than the default,
  and why this bullet used to be wrong.
- **Phase 3 — image transforms**: on-the-fly resize/crop/format/quality with a cached derivative. This
  is the one Appwrite parity item in Storage that developers actually ask for by name, and it is its
  own design problem (which library, where derivatives live, how they're invalidated) rather than a
  slice of Phase 1.

**Explicitly out of scope for the whole sequence**: a CDN integration, signed time-limited URLs, and
antivirus scanning. Each is a legitimate future initiative; none is needed for Storage to be useful.

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
