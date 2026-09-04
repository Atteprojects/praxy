# Session task — Storage, Phase 2 (access control and serving)

> **Status: shipped.** 2026-09-04 — see `docs/handoff/storage-phase-2-report.md`.

## Why this exists

Phase 1 shipped buckets, files, chunked bytes in Postgres, and bucket-level permissions. It is
deployed and in use. Phase 2 is the three things that make Storage genuinely usable rather than
merely working: **per-file permissions**, **HTTP Range**, and **opt-in inline serving**.

Read `docs/research/storage.md` in full first — especially its **"Phase 2 — designed 2026-09-04"**
section, which is the complete design and settles the decisions that matter, and **"Downloads are
never renderable"**, which is a security control this phase must not weaken. This prompt assumes
you've read both. Work on a new branch off `main`. Read `CLAUDE.md` first.

The single most important thing to internalise before writing code: **per-file permissions are
additive, not restrictive**, exactly like row security. A bucket-level `read("any")` grant means
everyone reads every file and no per-file grant can claw that back. "Users can only read their own
uploads" is configured by granting *no* bucket read at all. If you find yourself designing a way for
a bucket grant to coexist with per-file restriction, stop — that is a second authorization model, and
this codebase deliberately has one.

## Non-goals

- **No image transforms.** Phase 3, and its own design problem.
- **No signed/time-limited URLs, no CDN, no antivirus.** Out of scope for the whole sequence.
- **No `multipart/byteranges`.** A multi-range request is answered with the full `200` body — the spec
  permits ignoring a Range header, and no browser needs multipart for media playback.
- **No second authorization concept.** Per-file resolves through the same `IRoleResolver` and the same
  intersect-the-roles check.
- **Do not weaken the attachment default.** `X-Content-Type-Options: nosniff` stays on every response,
  and inline is opt-in per bucket against a hard-coded allowlist. `text/html` and `image/svg+xml` are
  permanently excluded, not configurable.

## Scope

1. **Per-file permissions.**
   - `FilePermission` entity + migration: `file_id` (FK → `files`, cascade), `action`, `role`, PK
     `(file_id, action, role)` — `TablePermission`'s shape exactly. Reuse
     `PermissionStrings.StorableActions`; no new vocabulary.
   - `bucket.file_security` already exists in the Phase 1 data model — wire it up rather than adding a
     new flag.
   - Accept `permissions` on upload and expose `$permissions` on the file DTO, mirroring rows.
   - A per-file permissions endpoint mirroring the bucket one.
   - The escalation order is in the design doc; it is copied from
     `QueryCompiler.PermissionPredicate` and should not be improvised.
2. **The listing path — read this twice.** `FilesService.ListAsync` does
   `db.Files.Where(...)` then `CountAsync` + `Skip`/`Take`. The permission filter **must go into that
   EF query**. Filtering after pagination gives a `total` that counts invisible files and pages that
   come back short. The design doc has the shape.
3. **`FilesService.RequireAsync` must stop throwing for reads.** Today it throws when the bucket
   doesn't grant the action — correct while bucket-level is the only level, wrong once per-file grants
   exist, because a caller with a per-file grant and no bucket grant must get their file. Convert it to
   something that reports whether the bucket already allows the action, and let the per-file check run
   when it doesn't. **Test both directions explicitly** — this is the one change here that can produce
   either a security hole (defaulting to allow) or a silently broken feature (keeping the throw).
4. **HTTP Range.**
   - **Extend `IFileStore.OpenRead` to take an offset and length.** Do *not* implement Range above the
     seam by reading and discarding the leading bytes: that works for Postgres and would force a future
     S3 backend to fetch a whole object to serve 1 KB. The seam exists to keep that option open.
   - Chunk arithmetic uses the file's **own** `chunk_size_bytes` (stored per row, not read from
     config), so it stays exact after the configured default is retuned. Formulae are in the design doc.
   - `Accept-Ranges: bytes` on full responses; `206` + `Content-Range: bytes s-e/total`; `416` +
     `Content-Range: bytes */total` for unsatisfiable; suffix (`bytes=-500`) and open-ended
     (`bytes=500-`) both handled.
5. **Opt-in inline serving.** Per-bucket `inline_types` (empty by default). A response is inline only
   when the file's type is in that bucket's list *and* in the hard-coded safe set. `nosniff` always.
6. **Console**: per-file permissions editor on the file row/sheet (reuse `AddRoleButton`/`RoleLabel`,
   don't build new components), the `file_security` toggle on bucket settings, and the `inline_types`
   control. Follow how the tables screens present row security.
7. **SDKs**: per-file permissions on upload in `@praxy/core` and the Flutter SDK. Range is an HTTP
   concern the SDKs don't need to model explicitly.
8. **Docs**: `docs/self-host.md` gains the inline-serving note — what the allowlist is and why
   `text/html`/`image/svg+xml` can never be on it.

## Landmines

- **Additive, not restrictive** — see above. The most likely conceptual error in this phase.
- **The `RequireAsync` conversion** — see item 3. The most likely security error.
- **In-memory filtering after pagination** — see item 2. The most likely correctness error.
- **A `206` is still an `attachment`** unless inline was opted in. Range and `Content-Disposition` are
  orthogonal; don't let adding one quietly drop the other.
- **`Content-Length` on a partial response is the length of the part, not the file.** Getting this
  wrong makes players hang rather than fail loudly.
- **The file's stored MIME type is attacker-controlled** (whatever the uploader sent). The inline
  allowlist is what makes it safe, not the fact that it was stored.

## Tests

- Unit: the escalation order in all four branches (bypass / bucket grant / `file_security` off /
  per-file match); Range header parsing including suffix, open-ended, unsatisfiable, and multi-range;
  chunk arithmetic for a range that starts mid-chunk, ends mid-chunk, spans exactly one chunk, and
  covers the whole file.
- Integration (real Postgres): a caller with **no** bucket grant but a per-file `read` grant can get
  and download that file and **cannot** see any other file in the bucket, including in `list` — assert
  `total` as well as the rows, since that is where in-memory filtering shows up; a bucket-level
  `read("any")` grant still returns every file regardless of per-file grants (the additive property); a
  `206` returns exactly the requested bytes and they match the same slice of a full download; `416`
  for a range past the end; deleting a file cascades its permission rows; an inline-allowlisted type
  serves inline while `text/html` in the same bucket still serves as an attachment.

## Done means

- `dotnet test` green (unit + integration).
- Console build clean (`tsc -b && vite build`); `sdk/js` typecheck/test green if touched.
- **Owner test, actually run**: create a bucket with `file_security` on and no bucket read grant,
  upload two files as different users, and confirm each sees only their own in the console *and* that
  the count reflects it. Then grant bucket-level `read("any")` and confirm both files become visible to
  both — that is the additive property, and it is the one worth seeing with your own eyes.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/storage-phase-2-report.md`. **Do not write a Phase 3 prompt** — image transforms
  are scoped but deliberately undesigned, and need their own design pass.
