# Security review — Phase 2 report: the HTTP edge

Design: `docs/research/security-review.md`. Prompt: `docs/handoff/security-review-phase-2-prompt.md`.
Scope: where attacker-controlled bytes and hostnames meet the response — the Storage transform/derivative
path, `ByteRanges` arithmetic, `SiteProxyMiddleware`'s header and host handling, `SiteHostPattern`/`_ask-tls`,
preview-URL enumeration, and `site_requests` logging. Explicitly out of scope: the container-execution
seam (Phase 1, shipped) and authorization/project isolation (Phase 3).

Every finding below was demonstrated against the real local dev instance (`dotnet run --project src/Praxy.Api`
against `praxy-dev-pg`, real Docker for Sites containers) — before the fix, and again after — except where
noted as reading-only. Full `dotnet test`: 626 unit + 328 integration, 0 failed.

## 1. Findings table

| id | title | anonymous caller | project developer | status |
|----|-------|-------------------|--------------------|--------|
| A | An extreme source aspect ratio derives an unbounded output dimension on a single-axis transform request, crashing the request (and, for less extreme ratios, silently producing a huge uncapped allocation/derivative) | **High** (any bucket with `read("any")`) | **High** | **Fixed** |
| B | One oversized `Method`/`Path` on a proxied site request silently drops every other request's log row batched in the same flush | **High** (any public site) | High | **Fixed** |
| C | Preview/deployment ids are UUIDv7, whose leading 48 bits are a timestamp, not full-width randomness | Informational | Informational | **Accepted — verified still sound** |
| D | `ByteRanges` arithmetic (suffix/closed/open/unsatisfiable, chunk-boundary offsets) | N/A | N/A | **Verified sound, no defect found** |
| E | `SiteProxyMiddleware`'s `X-Forwarded-*` handling — a caller-supplied spoofed value | N/A | N/A | **Verified sound, no defect found** |
| F | `SiteHostPattern`/`_ask-tls` allow-list — almost-valid hostnames, case sensitivity, disabled sites | N/A | N/A | **Verified sound (pre-existing tests), no defect found** |
| G | Transform parameter validation (`format`/`quality`/`gravity`/`background`) reaching SkiaSharp | N/A | N/A | **Verified sound, no defect found** |

## 2. Findings, one section each

### A — An extreme source aspect ratio derives an unbounded output dimension (crash + uncapped allocation)

**What an attacker does.** Uploads an image with an extreme aspect ratio — no crafted header, no
mismatch between claimed and real dimensions needed, just an ordinary, honestly-encoded image that
happens to be very long and thin (a full-page website screenshot, a panorama, a receipt scan). Requests
a transform naming **only one axis**, e.g. `?width=64`.

**What they get.** `ImageTransforms.ResolveDimensions`'s single-axis branch computed the other axis via
`DerivedDimension`, scaled to preserve the source's real aspect ratio — and, unlike the caller-named
axis, **never snapped or bounded it against `DimensionLadder.TopRung`**. The doc comment justified this
as "it isn't what the caller asked for," which is true for the *key-space* concern (varying `?width=`
alone still produces at most 6 rows per file) but says nothing about the *size* of what gets computed:
a single small requested axis against a pathological source ratio derives an enormous other axis.

**How it was demonstrated.** A real, honestly-encoded 4×100,000 PNG (400,000 total source pixels —
comfortably inside `MaxSourceImagePixels`'s 40M-pixel ceiling, so the *source*-side guard correctly let
it through) uploaded to a real bucket on the local dev instance, then:

```
GET .../files/{fileId}/download?width=64
```

Derived height: `1,600,000`. `ImageTransformer.Transform`'s `Resize` call allocated a 64×1,600,000
target bitmap (~410 MB for the temporary RGBA buffer) **without complaint** — the resize step has no
size ceiling of its own. Only the **PNG encoder** caught it, via libpng's own internal row-count sanity
limit:

```
libpng warning: Image height exceeds user limit in IHDR
libpng encode error: Invalid IHDR data
[ERR] Unhandled exception for GET .../download
System.NullReferenceException: Object reference not set to an instance of an object.
   at Praxy.Storage.ImageTransformer.Transform(...) in ImageTransformer.cs:line 71
```

`SKBitmap.Encode` returns `null` on failure rather than throwing; the unchecked `encoded.ToArray()` on
that `null` crashed with an unhandled `NullReferenceException` — a 500, not the clean 400 every other
rejected transform gets. Response time was fast (0.25s) and process memory did not visibly spike, so
this reproduces as an immediate crash for `format=png` (the default, since the source was png) — but the
*root cause* is broader than the one format that happens to have a builtin sanity check: a request for
`format=webp` or `format=jpeg` against the same file, or a less extreme ratio that stays under libpng's
own limit, does not crash — it silently succeeds, producing and **storing** a multi-hundred-megabyte
derivative (counted against the project's storage quota only *after* the expensive resize+encode already
ran), which is the more damaging half of this finding: a single small, cheap request can force a large,
slow allocation on every deployment that has any bucket serving arbitrary uploaded images.

**Severity.** High under both actor models: no malice is required from the uploader (an ordinary
screenshot triggers it), and any anonymous caller who can reach a `read("any")` file can trigger the
allocation/crash repeatedly and for free. Rated equally for a project developer since it's their own
upload quota and CPU being spent, and the failure mode (an unhandled 500) is the same either way.

**The fix.** `ImageTransforms.cs`: `DerivedDimension` renamed `DerivedDimensionOrThrow` and now rejects
(400, `file_transform_invalid`) any derived axis exceeding `DimensionLadder.TopRung` (2048) — the exact
same bound the explicitly-requested axis already enforced via `SnapUpOrThrow`. Every derivative's *both*
dimensions are now within the same six-rung bound, whether requested or derived; a pathological-ratio
request now gets a clean, immediate 400 explaining the image's aspect ratio and telling the caller to
name both dimensions explicitly to crop instead. `ImageTransformer.cs`: defense in depth alongside that —
`SKBitmap.Encode`'s `null` return is now checked and turned into a 400 (`Unencodable`) rather than an
unhandled `NullReferenceException`, so any *other* future encoder-side rejection (a different format's
own internal limit, an SkiaSharp version change) fails the same clean way instead of crashing.

**Correction, found in review before merge — the first fix had the right target and the wrong shape.**
It bounded the *derived axis* at `DimensionLadder.TopRung`, symmetric with the requested axis. That
reads as obviously correct and passes a 1:1 boundary test, but **only a perfectly square source can
have both axes land at or under 2048**, so it rejected every ordinary photo at the top rung.
Measured against `ImageTransforms.Resolve` directly:

| source | request | first fix | |
|---|---|---|---|
| 3000x4000 (3:4 phone) | `?width=2048` | derives 2731 | **400** |
| 1080x1920 (9:16 phone) | `?width=2048` | derives 3641 | **400** |
| 2000x3000 (2:3 camera) | `?width=2048` | derives 3072 | **400** |
| 2480x3508 (A4 scan) | `?width=2048` | derives 2897 | **400** |
| 4000x2250 (16:9) | `?height=2048` | derives 3641 | **400** |
| 2048x2048 (square) | `?width=2048` | derives 2048 | 200 |

A 2048x2731 derivative is ~22 MB — not a threat, and exactly what a caller asking for a large
thumbnail of a phone photo expects. The verification quoted above (`?width=300` against a 300x200
source) could not have caught this: it derives 200, nowhere near the bound.

The rung exists to bound the **key space** of the axis a caller can vary — which the derived axis, by
definition, is not. What the derived axis needs bounded is **cost**, and cost is *area*. So the bound
is now `DimensionLadder.MaxOutputPixels` (`TopRung * TopRung * 2` = 8,388,608) applied to the
product: every realistic photo and document ratio up to 1:2 passes at full size, a 1:3-or-wider
panorama has to ask for a smaller width (which still works at every lower rung, since the bound is on
the product rather than either axis), and the original 4x100,000 repro is still rejected — 64 x
1,600,000 is 102M pixels, twelve times over.

**Test.** `ImageTransformsTests.An_extreme_aspect_ratio_that_would_blow_up_the_derived_axis_is_rejected`
(new) — both the width-only and height-only directions, asserting the pure resolve function rejects
before any SkiaSharp call is ever reached (matching the file's own "pure function, testable in all four
ladder directions without standing up a database or SkiaSharp" design).
`An_ordinary_photo_ratio_still_transforms_at_the_top_rung` (new, replacing the original
`A_derived_axis_landing_exactly_on_the_top_rung_is_allowed`) — a `[Theory]` over the five ratios in
the table above, including the square case the per-axis bound would have allowed, so the *shape* of
the bound is what's under test rather than one convenient point on it.
`The_output_pixel_bound_is_the_boundary_not_the_axis` (new) — 1:2 at the top rung lands exactly on
`MaxOutputPixels`, 1:3 is rejected there and succeeds one rung down. Verified live end to end: the exact pre-fix repro (`?width=64` against the
4×100,000 PNG) now returns `400 file_transform_invalid` with a message naming the derived height, and a
normal-aspect-ratio transform (`?width=300&format=webp` against a 300×200 image) still returns `200` with
correct bytes.

### B — One oversized `Method`/`Path` silently drops every other log row in the same flush

**What an attacker does.** Sends one HTTP request to any deployed site's public hostname (production or
preview — both go through the same `ForwardToContainerAsync`) whose method token or path exceeds the
`site_requests` table's column widths: `character varying(16)` for `Method`, `character varying(2048)`
for `Path`. Neither is Kestrel-limited to ordinary values — a custom method token (`PROPFIND` and its
kin are real, and nothing stops a client from sending an arbitrary token-shaped string) or a long query-
free path (a scanner walking a huge wordlist, or just a very long real URL) both reach
`SiteRequestLogWriter.TryEnqueue` unmodified.

**What they get.** `SiteRequestLogWorker.ExecuteAsync` drains the channel and constructs entities
directly from `entry.Method`/`entry.Path` with no length check, then calls `AddRange` + one
`SaveChangesAsync` for the whole drained batch. Confirmed directly against the real Postgres instance:

```sql
BEGIN;
INSERT INTO praxy.site_requests (..., method, ...) VALUES (..., 'GET', ...);            -- valid
INSERT INTO praxy.site_requests (..., method, ...) VALUES (..., 'THIS-METHOD-IS-TOO-LONG', ...); -- invalid
INSERT INTO praxy.site_requests (..., method, ...) VALUES (..., 'GET', ...);            -- valid
COMMIT;
-- ERROR: value too long for type character varying(16)
-- ERROR: current transaction is aborted, commands ignored until end of transaction block
-- ROLLBACK
-- 0 rows persisted, including the two otherwise-valid ones
```

A single malformed request aborts the whole implicit transaction — Postgres semantics, not an EF quirk —
so **every other request's log entry drained into the same batch is silently lost**, caught by the
worker's broad `catch (Exception ex)` and logged as one generic flush failure with no indication *which*
entries were real. Under any real concurrent traffic (the exact condition the batching exists to
handle — "drains whatever else is immediately available before flushing"), an attacker who keeps sending
one malformed request every so often can continuously blind a busy site's Logs tab, erasing evidence of
whatever else was happening at the same time. This is not theoretical scaffolding: it was reproduced live
by seeding the channel directly via `SiteRequestLogWriter` with a normal → oversized → normal sequence and
confirming zero rows persisted for any of the three.

**Severity.** High under both actor models — no authentication or deployment rights needed, any anonymous
caller who can reach any deployed site's public hostname can trigger it, and it undermines the "no gaps I
might forget about later" observability guarantee the whole feature exists to provide.

**The fix.** `SiteRequestLog.MethodMaxLength`/`PathMaxLength` constants (new, `Praxy.Persistence.Entities.Sites`),
shared by `PraxyDb`'s `HasMaxLength` calls so the column width and the clamp can never drift apart.
`SiteRequestLogWorker` now truncates `Method`/`Path` to those lengths before constructing each row —
clamping, not rejecting, since there is nothing to reject a proxied request over; this is best-effort
observability of traffic that already happened. The truncation itself backs off one character when it
would land inside a UTF-16 surrogate pair (Postgres `character varying(n)` counts Unicode codepoints,
.NET `string.Length` counts UTF-16 units — an emoji or other astral character sitting exactly at the cut
point would otherwise leave a dangling unpaired surrogate, which Npgsql rejects as an encoding error,
recreating the exact same poisoning bug one layer down).

**Test.** `SiteRequestLogTests.An_oversized_method_or_path_does_not_poison_other_entries_in_the_same_flush`
(new) — enqueues a normal/oversized/normal sequence straight through the real `SiteRequestLogWriter` (not
a direct DB write), verified to fail before the fix (only 0 of 3 rows persisted) and pass after (all 3
persist, the oversized one truncated).
`A_truncation_that_would_split_a_surrogate_pair_backs_off_instead` (new) — an emoji placed exactly at the
column-width boundary, asserting the stored value never ends on an unpaired surrogate.

### C — Preview/deployment ids are UUIDv7 (informational, accepted)

**The question the prompt asked.** Preview URLs key on `deploymentId` alone
(`<deploymentId>.<key>.<projectId>.{Domain}`) — is a GUID's worth of entropy actually sufficient, or does
anything narrow the search space below GUID-strength?

**What's true.** `Ids.NewUuid()` is `Guid.CreateVersion7()` — UUIDv7, whose first 48 bits are a
millisecond Unix timestamp, not random. An attacker who can narrow *when* a deployment was created (a
build-log timestamp, a webhook delivery time, or just "recently") effectively knows those 48 bits, and
the true random contribution is only the remaining 74 bits (12-bit `rand_a` + 62-bit `rand_b` per the
UUIDv7 layout) rather than a full 122 bits of randomness (UUIDv4's).

**Why this is accepted, not fixed.** 74 bits of true randomness is `2^74 ≈ 1.9 × 10^22` — even with the
entire timestamp known exactly, this is far beyond any practical brute-force or enumeration budget
(stronger than a standalone 64-bit unguessable token, a bar most systems treat as sufficient on its own).
Nothing else narrows it further: `Ids.TryParseWire` requires an exact 32-hex (or dashed) match, there is
no list endpoint that enumerates deployment ids across projects, and the GitHub webhook's response body
never echoes one (still HMAC-gated, unchanged from Phase 1). Recorded here, not silently passed over,
because the prompt explicitly asked and because this reasoning needs re-checking if `Ids.NewUuid()` ever
changes to a scheme with less randomness.

**A related, unfixed observation — for later phases, not a Phase 2 finding.** A preview or production
site is a fully attacker(developer)-authored app; if it embeds any third-party resource (a font, an
analytics script, an image), the visiting browser's `Referer` header discloses the full preview
hostname — including the deploymentId — to that third party. This is the same class of leak every
preview-deployment platform (Vercel, Netlify) accepts, isn't something Praxy's proxy can close without
forcing a `Referrer-Policy` on every hosted app regardless of what that app wants (a behavior change to
developer-controlled apps, not a phase-2-sized fix), and doesn't let an outsider *reach* a preview they
didn't already have a link to — it only tells a third party the visitor already went there.

### D — `ByteRanges` arithmetic (verified, no defect found)

Read the existing suite (`ByteRangesTests.cs`) end to end and independently re-derived the RFC 9110
cases it covers (suffix, closed, open-ended, unsatisfiable, past-the-end clamping, multi-range fallback,
zero-length file). Then verified live against a real stored file on the dev instance rather than trusting
the unit tests alone:

- `bytes=0-9` → `206`, `Content-Range: bytes 0-9/490`, `Content-Length: 10`.
- `bytes=-10` (suffix) → `206`, `Content-Range: bytes 480-489/490`.
- `bytes=99999-` (past the end) → `416`, `Content-Range: bytes */490`.
- A transform request (`?width=64`) with a `Range` header still returns a plain `200` with the full
  derivative — Range is correctly never applied to a generated derivative.
- Byte-for-byte: downloaded the full file and a `bytes=100-149` partial response separately and
  confirmed the partial response's 50 bytes exactly match `full[100..150]` — not just the headers, the
  actual bytes.

No defect found. `ChunkRange.For`'s chunk-boundary math (`firstChunk`/`skipInFirstChunk`/`lastChunk`,
using each file's own recorded `chunk_size_bytes`) was read alongside this and is correct by inspection;
the byte-exact live check above is the strongest available confirmation without deliberately corrupting
stored chunk data.

### E — `SiteProxyMiddleware`'s `X-Forwarded-*` handling (verified, no defect found)

**The question.** Does anything assume a trust relationship for `X-Forwarded-*` headers that doesn't
actually hold for a site's own container the way it holds for Caddy?

**What was checked.** `ForwardToContainerAsync` forwards via `IHttpForwarder.SendAsync` with no custom
`HttpTransformer` — meaning it uses YARP's `HttpTransformer.Default`, not a bare pass-through. Verified
live rather than assumed from the package's XML docs: a request was sent directly to the api's HTTP port
carrying deliberately spoofed `X-Forwarded-For: 6.6.6.6`, `X-Forwarded-Proto: https`, and
`X-Forwarded-Host: evil.example.com`, and the echo site's own received headers were inspected:

```json
{"x-forwarded-for":"::1","x-forwarded-host":"sec-echo.<project>.sites.localhost","x-forwarded-proto":"http", ...}
```

All three were **overwritten** with Praxy's own trustworthy values (the real connection's remote address,
the real scheme, the real requested host) — the caller-supplied spoofed values never reached the
container. `Host` itself is set to the destination container's own `host:port` (standard proxy behavior;
the real public hostname is still available to the app via the correctly-set `X-Forwarded-Host`).

No defect found — YARP's default transformer already does the right thing here, closing what could
otherwise have been an IP/proto/host-spoofing vector for any Next.js app on this platform that
(reasonably, following the common pattern from other hosts) trusts `X-Forwarded-For` for its own
rate-limiting or logging.

### F — `SiteHostPattern`/`_ask-tls` allow-list (verified, no defect found)

Read `SiteHostPattern.TryParse` and the existing `SiteHostPatternTests.cs`/`SitesAskTlsTests.cs` suites,
which already cover: empty/malformed 2- and 3-label hosts, empty inner labels, wrong domain suffix,
suffix-only matches (`...sites.localhost.evil.com`), case-insensitivity, a made-up deployment id under a
real site, a disabled site (both production and preview), an unregistered custom domain shaped exactly
like a real one, a custom domain leaking through a *different* enabled sibling site, and case-insensitive
custom-domain matching. Independently confirmed `Ids.IsValidCustomId`'s regex
(`^[a-z0-9][a-z0-9-]{0,35}$`) makes a dot-bearing site key or project id impossible to create in the
first place, which is what keeps the 2-vs-3-label split unambiguous — a key or project id can never
itself contain the delimiter the parser splits on. No defect found; this area was already thoroughly
exercised before this phase and did not need fresh discovery.

### G — Transform parameter validation reaching SkiaSharp (verified, no defect found)

Exercised live against a real uploaded file: `width=2048` (top rung, `200`), `width=2049` (`400`,
"exceeds the maximum"), `width=0`/`width=-5` (`400`, "must be at least 1 pixel"), `format=bmp` (`400`,
"Unsupported format"), `quality=101`/`quality=0` for `jpeg` (`400`, "quality must be between 1 and 100"),
`gravity=nonsense` (`400`, "Unsupported gravity"), `background=zzzzzz`/`background=ff00` (`400`, "must be
six hex digits"), and a valid custom `background=ff0000` (`200`, uncached as designed). Every malformed
value is rejected before ever reaching SkiaSharp — `NormalizeFormat`/`NormalizeQuality`/`NormalizeGravity`/
`NormalizeBackground` are all closed-set or range checks with no pass-through string ever reaching
`EncodedFormat`'s switch. No defect found here independent of Finding A (which is about the *derived*
dimension, not these parameters).

## 3. Accepted risks

- **Finding C (preview/deployment id predictability)** — accepted as described above: 74 bits of true
  randomness remain even with the full creation timestamp known, which is beyond any practical
  brute-force budget. Revisit only if `Ids.NewUuid()`'s scheme ever changes.
- **Referrer leakage from a hosted app's own third-party embeds** (noted under Finding C) — accepted;
  not fixable without imposing a `Referrer-Policy` on every developer-authored app regardless of what
  that app wants, which is a behavior change outside a review phase's budget. Noted for a future phase
  if it's ever worth revisiting as an opt-in site setting.

## 4. Multitenancy prerequisites

Nothing new rises to this bar in Phase 2. Both fixed findings (A and B) are equally severe for an
anonymous caller as for a second untrusted project developer — neither depends on the single-trusted-
operator assumption `CLAUDE.md` documents, so neither was a *latent* multitenancy blocker the way
Phase 1's Findings A/B/C were: they were simply bugs, reachable today, now closed. Phase 1's own
multitenancy-prerequisite list (the Docker socket mount) is unchanged and not re-litigated here.

## 5. For later phases

- **Phase 3 (authorization/project isolation)**: unchanged from what Phase 1 already flagged
  (`PRAXY_FUNCTION_JWT`'s lifetime now that it's always freshly minted). Nothing new spotted in this
  phase's own scope that belongs in Phase 3 instead — the HTTP-edge surface stayed inside Storage and
  Sites' proxy/hostname handling as scoped.
- **The Referrer leakage noted under Finding C** — worth a look if a future phase wants to offer a
  per-site opt-in `Referrer-Policy` default, but not on its own a security-review finding that needs a
  dedicated phase.
- **`DerivativesService.ReadSourceBytesAsync` runs twice** for a single-axis transform request whose
  file has no cached `Width`/`Height` yet (once for the header-dimension probe, once for the actual
  transform) — a real inefficiency, not a security defect (bounded by the same `MaxFileSizeBytes` either
  read already respects), noticed while tracing Finding A's call path. Not fixed here — out of scope for
  a security review, and not cheap enough to be a "why not" fix in passing.

## Commands

No new configuration this phase — both fixes are pure logic changes (`ImageTransforms`'s derived-dimension
bound, `SiteRequestLogWorker`'s truncation), no new `Praxy:*` knobs, no changed defaults, no new runtime
dependency.
