# Session task — Security review, Phase 2: the HTTP edge

## Why this exists

Phase 1 (`docs/handoff/security-review-phase-1-report.md`) closed the container-execution boundary:
Functions got its own Docker network (no longer sharing one with Postgres), both executors gained
`PidsLimit`/`CapDrop`/`SecurityOpt`/a non-root user, and — found during that phase, not on the
starting list — a warm function container could serve one app user's `PRAXY_FUNCTION_JWT` into a
different user's invocation. This is Phase 2 of three: the HTTP edge, where attacker-controlled bytes
and hostnames meet the response.

Read `docs/research/security-review.md` first — the threat model, actor definitions, and report format
all come from it, and Phase 1's report shows the format applied — then `CLAUDE.md`. Work on a new
branch off `main`.

## This is a review, not a feature phase — read this before scoping

Same discipline as Phase 1:

- **The deliverable is findings.** Judge this session by what it found and closed, not by "did it
  implement the scope list below."
- **Record every finding whether or not you fix it** — attack, impact, severity under *both* actor
  models, and either the fix or the explicit reason for accepting it.
- **Do not fix everything.** Fix what is cheap and clearly right; document-and-accept what is
  expensive; never redesign a shipped subsystem mid-review.
- **Every fix carries a regression test** — the shape that matters is the isolation/correctness
  property actually holding, not a config value or a happy-path assertion.
- **No new features.**

## The two actors — rate every finding under both (unchanged from Phase 1)

1. **Anonymous network caller** — anyone who can reach the instance, no credentials. For this phase
   specifically: reaches every site's public hostname (and every preview URL, if guessable/enumerable),
   and any file whose bucket grants `read("any")`.
2. **Project developer** — an authenticated operator who can create buckets, upload files, and deploy
   sites. Today this is the instance owner and people they trust (`CLAUDE.md` defers multitenancy).

Same rule as Phase 1: a finding that's harmless under one trusted operator but critical the moment a
second untrusted developer shares the instance is **not "low"** — it's a multitenancy prerequisite, and
the report needs that section again.

**Out of scope, same as Phase 1: a host-root attacker.** The Docker socket mount is still conceded;
don't re-litigate it.

## Where to look — from the design doc, not a closed scope list

`docs/research/security-review.md` calls Storage's download edge already well-defended —
`ContentDisposition.Build`'s CR/LF stripping, `InlineTypes.ServesInline`'s two re-intersected gates
with `text/html`/`image/svg+xml` permanently excluded, unconditional `nosniff` — so the job there is
**verification that it's still true**, not fresh discovery. The undiscovered surface is newer:

- **The transform/derivative path** (`docs/handoff/storage-phase-3-report.md`, extended by
  `docs/handoff/storage-phase-2-report.md`'s follow-up fixing a stored XSS, a production crash, and two
  transform correctness bugs — all in code that predates this exact review). `?width=`/`?height=`/
  `?format=`/`?quality=` on the download endpoint, the dimension ladder meant to keep the derivative
  cache bounded (does it actually reject every input that should snap to `400` rather than silently
  clamping?), `MaxSourceImagePixels`'s decompression-bomb check (does it run *before* any real
  allocation, for every code path that can reach the decoder, not just the common one?), and whether a
  crafted `format=`/`quality=` value can reach SkiaSharp in a way that wasn't exercised by the Phase 3
  test suite.
- **`ByteRanges` arithmetic** (HTTP Range support, Storage Phase 2). Off-by-one and negative/overflow
  cases on `Range: bytes=...` — a malformed or adversarial range header is a classic source of either an
  information leak (reading past what was authorized) or a crash.
- **`SiteProxyMiddleware`'s header and host handling.** What headers does it forward from the upstream
  site container back to the caller, and from the caller to the container? Can a site's own response
  (fully attacker-controlled — it's the developer's deployed app) inject something the proxy trusts
  unconditionally? Does anything about `X-Forwarded-*`/`Host` handling assume a trust relationship that
  doesn't actually hold for a site's own container the way it holds for Caddy (`Praxy:TrustForwardedHeaders`'s
  documented Caddy-only trust boundary)?
- **`SiteHostPattern`'s parse and the `_ask-tls` endpoint sharing it.** The three-label preview-URL
  format (`<deploymentId>.<key>.<projectId>.{Domain}`) and the two-label production format
  (`<key>.<projectId>.{Domain}`) — does the parser correctly reject a hostname that's *almost* valid in
  a way that could resolve to the wrong project or deployment? `_ask-tls` (Caddy's on-demand TLS ask
  endpoint) trusts *some* pattern match to decide whether to issue a certificate — what happens on a
  hostname crafted to pass that check without actually mapping to a real, enabled site?
- **Preview-URL enumeration.** Preview URLs key on `deploymentId` (a GUID) — is that the only thing
  standing between "anonymous caller" and "arbitrary preview of someone else's in-progress deployment,"
  and is that actually sufficient, or does anything (a list endpoint, a predictable id shape, a leak
  via logs/referrers) narrow the search space below GUID-strength?
- **Whether `site_requests` logging can be poisoned by a crafted request.** The table logs
  method/path/status/duration per proxied request (`docs/handoff/sites-request-logs-report.md`) —
  can an attacker-controlled path or header end up stored in a way that breaks the console's Logs tab
  rendering, or that's misleading to an operator reviewing it (log injection, not just log volume)?

## Non-goals for this phase

The permission model itself — Storage's additive bucket/per-file grants, the scope and lifetime of a
function's minted credentials (including the note Phase 1 left about revisiting `PRAXY_FUNCTION_JWT`'s
lifetime now that it's always freshly minted), and whether project isolation holds at every entry
point including the ones that bypass the normal API surface — is **Phase 3**. Note anything spotted
there in this report's "for later phases" section and move on.

## Tests

Same standard as Phase 1: assert the property that actually has to hold (a range request can't read
outside what was authorized; a hostname that shouldn't match a site, doesn't), not a config value or a
single happy-path status code. Where something is only reachable by comparing against a running
instance (transform output correctness was exactly this class of bug last time), demonstrate it
against a real deployed bucket/site, not just inline unit assertions.

## Done means

- `dotnet test` green; console build clean if touched.
- Every finding demonstrated before the fix and shown closed after, against a running instance, for
  anything reachable at run time. Reading-only findings marked as such and rated lower confidence.
- **Owner test, actually run**: upload a file and exercise a transform, and deploy a site and hit both
  its production and a preview URL, on a real instance after the changes.
- `git status` clean, conventional commits, branch off `main`.
- Write `docs/handoff/security-review-phase-2-report.md` in the same shape as Phase 1's report:
  findings table, a section per finding, accepted risks, multitenancy prerequisites, for later phases.
- Write `docs/handoff/security-review-phase-3-prompt.md`, and update `CLAUDE.md`'s Commands section if
  any configuration changed.
