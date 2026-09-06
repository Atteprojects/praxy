# Session task — Security review, Phase 3: authorization and project isolation

> **Status: shipped.** 2026-09-06 — see `docs/handoff/security-review-phase-3-report.md`, which
> closes the security-review initiative. One correction made during review before merge: Finding E
> is fixed for *new* repository connections only; pairs that already exist are untouched.

## Why this exists

Phase 1 (`docs/handoff/security-review-phase-1-report.md`) closed the container-execution boundary
(network topology, `HostConfig` isolation, a credential-leak-across-invocations bug found during that
phase itself, and the capacity bound its own fix needed). Phase 2
(`docs/handoff/security-review-phase-2-report.md`) closed the HTTP edge: a real, live-demonstrated crash
where an extreme (but honestly-encoded, non-malicious) image aspect ratio derived an unbounded output
dimension on a single-axis transform request, and a bug where one oversized `Method`/`Path` on a proxied
site request silently dropped every other request's log row batched in the same flush — both found by
testing against a running instance, not by reading alone. `ByteRanges`, `SiteProxyMiddleware`'s
`X-Forwarded-*` handling, and `SiteHostPattern`/`_ask-tls` were all read and independently verified sound.

This is Phase 3 of three, and the last one: the permission model itself — whether project isolation
actually holds at every entry point in Storage, Sites and Functions, including the ones that don't go
through the normal, already-permission-checked API surface.

Read `docs/research/security-review.md` first — the threat model, actor definitions, and report format
all come from it, and Phase 1/2's reports show the format applied — then `CLAUDE.md`. Work on a new
branch off `main`.

## This is a review, not a feature phase — read this before scoping

Same discipline as Phases 1 and 2:

- **The deliverable is findings.** Judge this session by what it found and closed, not by "did it
  implement the scope list below."
- **Record every finding whether or not you fix it** — attack, impact, severity under *both* actor
  models, and either the fix or the explicit reason for accepting it.
- **Do not fix everything.** Fix what is cheap and clearly right; document-and-accept what is
  expensive; never redesign a shipped subsystem mid-review.
- **Every fix carries a regression test** — the shape that matters is the isolation/correctness
  property actually holding, not a config value or a happy-path assertion.
- **No new features.**

## What Phases 1 and 2 learned the hard way — apply them here too

1. **Review your own fixes as adversarially as the original code.** Phase 1's highest-severity new
   finding was introduced by a fix made in that same phase (the credential-isolation fix removed the
   only bound on how many containers could exist), and a second was widened by one. Before writing the
   report, re-read your own diff asking what each fix now makes possible that wasn't possible before.
2. **Assert the live property, not the configuration.** A test asserting a config value round-trips
   passes even when the thing it's supposed to control was never actually wired up — Phase 1 found this
   exact bug for `MaxConcurrentIsolatedContainers`. For this phase specifically: a permission-boundary
   test must prove the boundary actually blocks the request, not that a role/grant round-trips through
   an API.
3. **"Verified end to end" against a *fresh* instance is not verification.** Exercise the property
   against state that predates whatever you change, not only something you just created — a project
   created before a permission fix, a function whose credential was minted under the old scope, a
   bucket grant set up before any change to `FileAccessRules`.
4. **A real, honestly-shaped test case beats a crafted one.** Phase 2's headline finding needed no
   malicious file — an ordinary extreme-aspect-ratio screenshot was enough. Prefer reproducing an
   isolation failure with an ordinary second project/second user/second function before reaching for
   something adversarial-looking; if the ordinary case already breaks isolation, that's the finding.
5. **Pick the boundary case most likely to break your fix, not the one easiest to reason about.**
   Phase 2's own fix was reviewed and corrected before merge: it bounded a derived image dimension at
   the ladder's top rung, and the boundary test written for it used a **1:1 square source** — the one
   aspect ratio where that bound cannot bite. It passed, and every ordinary photo (3:4, 9:16, A4) was
   silently rejected by the fix. A boundary test you chose because it was convenient proves the fix
   compiles, not that it's right. For this phase: don't test isolation between two projects you set up
   identically — test it where the two differ in whatever way the code path actually branches on.
6. **Observability that lies is worse than none, and it hides the next bug.** Phase 1's orphan-container
   reclaim counted removal *attempts*, not removals, because the helper it called swallows its own
   failures — so the startup log reported successes that never happened. That false count concealed a
   second, worse defect (one contended container aborted the entire sweep) for a whole phase. This phase
   is entirely about whether a check *actually happens*: treat any log line, counter, or return value
   that reports success without proving it as a finding in its own right, not just a cosmetic issue.

## The two actors — rate every finding under both (unchanged from Phases 1 and 2)

1. **Anonymous network caller** — anyone who can reach the instance, no credentials.
2. **Project developer** — an authenticated operator who can create buckets, upload files, and deploy
   functions and sites. Today this is the instance owner and people they trust (`CLAUDE.md` defers
   multitenancy).

Same rule as Phases 1 and 2: a finding that's harmless under one trusted operator but critical the
moment a second untrusted developer shares the instance is **not "low"** — it's a multitenancy
prerequisite, and the report needs that section again. This phase is the one most likely to produce
prerequisites of exactly that shape, since project isolation *is* the multitenancy question.

**Out of scope, same as Phases 1 and 2: a host-root attacker.** The Docker socket mount is still
conceded; don't re-litigate it.

## Where to look — from the design doc and what Phases 1-2 flagged, not a closed scope list

- **Storage's additive bucket/per-file grants.** `docs/handoff/storage-phase-2-report.md`'s own framing:
  "additive like row security — a bucket grant reaches every file and no per-file grant narrows it." Is
  that actually true at every read path (the plain download, a Range request, a transform/derivative —
  Phase 2 confirmed a derivative never gets a second permission check, by design, resolving entirely
  through the source file's own `FileAccessRules`)? Does anything let a caller name a file id from a
  *different* bucket or project and have it resolve anyway (an id-confusion bug, not a permission-grant
  bug)?
- **A function's minted credential scope and lifetime.** Phase 1's own unresolved note: now that
  `PRAXY_FUNCTION_JWT` is guaranteed freshly minted per invocation (Finding C's fix closed the
  cross-invocation leak), is `AccountJwtService.DefaultLifetime` still the right value for a token that
  used to potentially sit in a warm container's env for minutes and now never does? What can that JWT
  actually do — does its scope match "call back into Praxy's own data plane as the triggering user," or
  is it broader? Same question for `PRAXY_FUNCTION_API_KEY` on schedule/event triggers.
- **Project isolation at entry points that bypass the normal API surface.** `SiteProxyMiddleware` (does
  it re-derive `bucket`/`site`/`project` scoping correctly for every branch, including the custom-domain
  path?), the GitHub webhook endpoint (`POST /v1/vcs/github/webhook` — HMAC-verified, but once inside,
  does it resolve the pushing repository to *only* the sites/functions that actually connected it, never
  a different project's connection to the same repo?), `FunctionExecutionWorker`/`SiteBuildWorker`/
  schedulers (do they carry the right project scope through to whatever they act on, or could a
  claimed-row's project id and its actual target project ever disagree?).
- **API key scopes — the whole model, which no phase has reviewed yet.** `ApiKeyScopes`
  (`src/Praxy.Auth/ApiKeyService.cs`) defines twelve scopes including `storage.read`/`storage.write`
  and `execution.read`/`execution.write`, and a key is the primary non-session way into the data
  plane. Neither Phase 1 nor Phase 2 touched it, and it is squarely this phase's subject. Is **every**
  data-plane endpoint actually scope-gated, or do some reach the resource without a `RequireScope`
  call at all (the function invoke path has one — is that the rule or the exception)? Does a key
  issued for project A resolve against project B? Does a read-scoped key reach any write path? Are
  scopes checked before or after the resource is loaded, and does that ordering leak existence?
- **The several principal/project resolution entry points, and whether they agree.** There is not one
  way into "who is calling and for which project" — there are at least
  `DataPlaneEndpoints.CurrentProject`, `ConsoleProjectFilter.Current`, `OperatorAuth.Current` and
  `AppPrincipal.Current`. Drift between two of them is precisely the shape of bug this phase is
  hunting: a resource reachable through one entry point with a check the other applies and it doesn't.
  Map which endpoints use which, and look hardest wherever a resource is reachable through more than
  one.
- **Cross-project id confusion generally.** Anywhere a request supplies an id (a file id, a bucket id, a
  function id, a table id) — is it always scoped by `WHERE project_id = @callersProject AND id = @id`,
  or is there a path that looks up by id alone and checks project membership afterward (a TOCTOU-shaped
  bug, or worse, a path that forgets the check entirely)?
- **The role resolver itself.** `CLAUDE.md`'s cross-phase rule: "one role resolver — query compiler and
  realtime fan-out consume the same implementation." Confirm that's still true and that Storage/Sites/
  Functions all reach the same implementation too, not a parallel one that could drift.

## Non-goals for this phase

Nothing left — this is the last phase in the sequence Phases 1 and 2 didn't already claim. If something
turns up that's clearly a fourth phase's worth of work (a genuine redesign, not a fixable finding),
document it as a recommendation rather than starting it.

## Tests

Same standard as Phases 1 and 2: assert the property that actually has to hold (a caller in project A
cannot reach project B's file/function/site by id, however it's named), not a config value or a single
happy-path status code. Prefer reproducing with two ordinary projects/users over a crafted-looking
attack, per this phase's own lesson above.

## Done means

- `dotnet test` green; console build clean if touched.
- Every finding demonstrated before the fix and shown closed after, against a running instance, for
  anything reachable at run time. Reading-only findings marked as such and rated lower confidence.
- **Owner test, actually run**: create two separate projects on a real instance, each with its own
  bucket/file, function, and site, and confirm neither can reach the other's resources by any id it can
  discover or guess, after the changes.
- `git status` clean, conventional commits, branch off `main`.
- Write `docs/handoff/security-review-phase-3-report.md` in the same shape as Phases 1-2's reports:
  findings table, a section per finding, accepted risks, multitenancy prerequisites, for later phases.
- This is the last phase in the sequence — the report's "multitenancy prerequisites" section becomes the
  durable, standalone output `docs/research/security-review.md` said it would be. Write it so it reads
  as a complete list on its own, referencing Phases 1-2's own prerequisite entries rather than repeating
  them.
- Update `CLAUDE.md`'s Commands section if any configuration changed, and note in the report whether a
  fourth phase is warranted or whether the initiative is complete.
