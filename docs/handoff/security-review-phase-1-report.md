# Security review — Phase 1 report: the container boundary

Design: `docs/research/security-review.md`. Prompt: `docs/handoff/security-review-phase-1-prompt.md`.
Scope: the Docker execution seam shared by Functions and Sites — network topology, `HostConfig`
isolation, container environment, resource limits, and the image-build path as a supply-chain
surface. Explicitly out of scope: the Docker socket itself (host-root, conceded elsewhere), the HTTP
edge (Phase 2), and authorization/project isolation (Phase 3).

Every finding below was demonstrated against a real, from-scratch instance built from
`deploy/docker-compose.yml` (the exact self-host artifact, not just `dotnet run`'s dev shortcut) —
before the fix, and again after — except where noted as reading-only.

## 1. Findings table

| id | title | anonymous caller | project developer | status |
|----|-------|-------------------|--------------------|--------|
| A | Function containers shared a Docker network with Postgres | N/A (not directly reachable) | High → **Critical under multitenancy** | **Fixed** |
| B | Neither executor set any isolation flag beyond memory/CPU | N/A | High → **Critical under multitenancy** | **Fixed** — `PidsLimit`, `CapDrop`, `SecurityOpt`, non-root `User`, and (in follow-up) `ReadonlyRootfs` |
| C | A warm function container could leak one app user's `PRAXY_FUNCTION_JWT`/`PRAXY_FUNCTION_USER_ID` into a different invocation | **Critical** (open signup reaches this) | **Critical** | **Fixed** |
| D | `GitCliRepositoryCloner`'s commit SHA had no shape validation before reaching `git fetch` (option-injection defense-in-depth) | Low (currently unreachable) | Low (currently unreachable) | **Fixed** |
| E | Function/Site base images are tag-pinned, not digest-pinned (`node:22-alpine` floats within the tag) | Low | Low | **Fixed** — resolved digest recorded per build; digest pinning supported and documented |
| F | No per-container disk quota — a function/site can fill the host's shared disk | Low (needs deploy access) | High → ~~multitenancy prerequisite~~ | **Fixed** — read-only rootfs + size-capped tmpfs |
| G | Both Docker networks allow outbound internet egress (`internal: false`) | N/A | Informational | **Fixed** — default unchanged (deliberate), now a documented one-line switch |
| H | Preview-container-per-project quota check has a known, pre-existing TOCTOU race | Low | Medium → ~~multitenancy prerequisite~~ | **Fixed** — atomic per-project cold-start gate |
| I | Finding C's fix removed the only bound on how many function containers can exist at once | **High** (open signup reaches it) | **High** | **Fixed** |
| J | Function containers orphaned by a hard crash were never reclaimed | Low | Low | **Fixed** |

## 2. Findings, one section each

### A — Function containers shared a Docker network with Postgres

**What an attacker does.** Any code running inside a function container opens a raw TCP socket to
the hostname `postgres` (or its container IP) on port 5432. Before this fix, `deploy/docker-compose.yml`
named Compose's `default` network `praxy-functions` — the exact network
`Praxy__Functions__DockerNetwork` joins every function container to — and `postgres` declared no
`networks:` key of its own, so it landed on `default` too. Confirmed live on praxycore.dev before this
session (per the prompt) and reconfirmed here from scratch.

**What they get.** A network path to the database. Not a credential — the connection still needs the
Postgres password — but the boundary that's supposed to hold when a credential leaks or a Postgres CVE
lands wasn't there at all. Sites already had this right (`praxy-sites` never had a database on it);
Functions inherited `default` purely as a side effect of the network rename.

**How it was demonstrated.** A function was deployed with this body:

```js
const net = require('net');
module.exports = async () => {
  const s = net.createConnection({ host: 'postgres', port: 5432, timeout: 2000 });
  ...
};
```

Before the fix: invoking it returned `{"postgres":"CONNECTED"}`. After the fix: `{"postgres":"ERROR:ENOTFOUND"}`
(the hostname doesn't even resolve). To rule out "just DNS," a raw connection to postgres's actual
container IP was attempted by `docker exec`-ing directly into the running function container — it
timed out with no route, not merely a refused connection.

**The fix.** `deploy/docker-compose.yml`: `praxy-functions` is now its own explicitly-named network
(mirroring `praxy-sites`), and the api/postgres/caddy network reverts to Compose's own unnamed
default. `api` joins all three networks (`default`, `praxy-functions`, `praxy-sites`); `postgres` and
`caddy` join only `default`. No C#/application code changed — `Praxy__Functions__DockerNetwork`'s
*value* (`praxy-functions`) is unchanged, only what Docker network that name now refers to.

**Upgrade impact — verified, not assumed.** This is a Compose network-identity change, and it does
**not** upgrade in place. Running `docker compose up -d --build` directly against an already-running
old-topology instance fails with:

```
network praxy-functions was found but has incorrect label com.docker.compose.network set to "default" (expected: "praxy-functions")
```

The instance is left running, unchanged, on the old topology — this fails safe, not partially. The
correct upgrade is `docker compose down && docker compose up -d --build`, verified end to end
including a warm function container attached to the old network at the moment of shutdown: `WarmPool`
stops every warm container when `api` shuts down, so nothing is left stranded blocking the network's
removal, and the new topology comes up cleanly with existing projects/functions/images intact. A
running **site** container is not stopped the same way (`RestartPolicy: unless-stopped`, independent
of `api`'s lifecycle) and can make `praxy-sites` report "resource still in use" during `down` — harmless
here since this fix doesn't touch `praxy-sites`, but a note for any future release that does. Full
writeup: `docs/self-host.md`'s Upgrading section.

**Test.** No `dotnet test` regression test is possible for a Compose-file topology change — there's no
in-process seam to assert against. The manual check above (live network membership before/after, plus
a real TCP connection attempt from inside a function container) is the verification, documented here
and in `docs/self-host.md` so it can be re-run against any future topology change.

### B — Neither executor set any isolation flag beyond memory and CPU

**What an attacker does.** Deploys a function (or site) whose code forks child processes as fast as
it can, or relies on inherited Linux capabilities / setuid escalation / running as root.

**What they get, before the fix.** An unbounded process table (a fork bomb has no `PidsLimit` to hit —
verified: a plain `docker run` with no limit forks until the *host* itself is out of PIDs, not just the
container), the container's full default capability set, `no-new-privileges` unset (a setuid binary in
the image can still escalate), and — since neither generated Dockerfile had a `USER` directive — root,
by default, for every runtime.

**Grading, per the prompt's own blast-radius ranking, each verified rather than assumed:**

- `PidsLimit` — applied to both executors. **Isolation property proven, not just the config value**:
  a real deployed function ran a script spawning 300 background processes; with `PidsLimit=20` in a
  controlled test, ~93% of spawn attempts failed with `EAGAIN` and `/proc`'s own live-process count
  never exceeded the limit. The same test against the pre-fix code (`HostConfig` with no `PidsLimit`
  at all) spawned all 300 with zero errors. This is the regression test:
  `FunctionContainerIsolationTests.A_function_that_tries_to_fork_bomb_is_capped_by_PidsLimit`.
- `SecurityOpt: ["no-new-privileges"]` — applied to both executors. Cheap, no observed breakage.
- `CapDrop: ["ALL"]` — applied to both executors, **tested per runtime before applying**: a real Node
  function, a real Dart function (including one with an actual `pubspec.yaml` dependency, not just a
  bare script), and a real Next.js standalone site were each built and run with `CapDrop: ["ALL"]` +
  `SecurityOpt: ["no-new-privileges"]` + `PidsLimit` + a non-root user together, and all three served
  requests correctly. No runtime needed a capability this drops.
- **Non-root `User`** — applied via a `USER` directive baked into the generated Dockerfile (not
  `HostConfig.User` — that's the image's job), for every runtime: Functions' Node, Functions' Dart, and
  Sites' Next.js. `65534:65534` (nobody/nogroup) was chosen over a named user because it's guaranteed
  to exist in every base image's `/etc/passwd`, unlike a named user that isn't (`dart:3.13.0`, Debian-
  based, has no pre-made low-privilege application user the way `node:22-alpine`'s `node` does).
  Verified, not assumed: the first attempt at the Dart runtime broke, because `dart pub get` caches
  packages under root's own home directory (`/root/.pub-cache`) by default, unreadable by a non-root
  user at runtime — found by actually building and running a function with a real dependency, fixed by
  redirecting `PUB_CACHE=/function/.pub-cache` (inside the directory that gets `chown`ed to the
  non-root user) before running `dart pub get`.
- `ReadonlyRootfs` — **not applied.** A real Next.js standalone server was tested with `--read-only
  --tmpfs /tmp` and served a static page correctly, but that minimal app exercises neither ISR
  (revalidate) nor the image optimizer, both of which write to `.next/cache` at runtime — a path a
  single `/tmp` tmpfs mount doesn't cover, and testing every combination of Next.js feature × tmpfs
  layout is a real redesign, not a phase-1 hardening pass. Deferred; see Accepted risks.

**The fix.** `src/Praxy.Functions/DockerExecutor.cs` and `src/Praxy.Sites/SiteDockerExecutor.cs`:
`HostConfig.PidsLimit`, `CapDrop`, `SecurityOpt` added, applied identically to both (the prompt's own
"whatever you decide applies to both, consistently" rule). `PidsLimit` is configurable
(`Praxy:Functions:PidsLimit` / `Praxy:Sites:PidsLimit`, defaulting 256/512). `src/Praxy.Functions/RuntimeTemplates.cs`
and `src/Praxy.Sites/SiteRuntimeTemplates.cs`: a `USER 65534:65534` directive (with a preceding
`chown -R`) added to every generated Dockerfile.

**Test.** `FunctionContainerIsolationTests.cs` (new) — real Docker, real
`DockerExecutor.StartContainerAsync`, asserts the isolation property (bounded live process count and
majority of forks refused), not the config value.

### C — A warm function container could carry one invocation's credential into another's (found during this review, not in the starting list)

The prompt explicitly asked: *"Does a warm-pool container ever outlive the credential it was started
with, or serve a second execution carrying the first one's env?"* — yes.

**What an attacker does.** Nothing special — this doesn't require malicious code, just two ordinary
invocations of the same function close together. `FunctionExecutionService.BuildEnvAsync` mints a
fresh `PRAXY_FUNCTION_JWT`/`PRAXY_FUNCTION_USER_ID` per invocation when the caller is a specific app
user (`docs/functions-runtimes.md`'s documented contract: "set only when the invocation was triggered
by a specific app user... Absent for console/event/schedule triggers"). But `WarmPool.AcquireAsync`
only used that env dict to start a *new* container — if a container for the same deployment was
already warm, it returned the existing container untouched, env and all. A container's OS-level
environment can't be changed after `docker start` without a restart, so the *first* invocation's JWT
stayed baked into the container for as long as it stayed warm (up to `MaxIdleSeconds`, default 300s),
served to *every* subsequent invocation of that function regardless of who triggered it.

**What they get.** User A invokes a function; the cold-started container gets `PRAXY_FUNCTION_JWT`
scoped to A. User B invokes the same function moments later, while the container is still warm — B's
invocation runs with A's JWT and user id still sitting in its process environment, exactly contradicting
the documented "set only for the calling user" contract. Any function that does what the contract
describes — "use the JWT to call back into Praxy's own data plane as that user" — would act as A while
actually serving B's request. The same mechanism means a schedule- or event-triggered execution
(documented as never getting a JWT at all) could observe a *stale* JWT left by whichever user happened
to invoke the function just before it, or a stale `PRAXY_FUNCTION_API_KEY` with platform scopes left by
a schedule run, served into a request from an arbitrary anonymous caller who just signed up.

**Severity.** Rated Critical under *both* actor models, not just the developer one — this doesn't
require a malicious "project developer" at all. Under the default Auth configuration (email+password,
open self-registration), **any anonymous network caller can become an "app user" and trigger this
purely by using the product normally**, crossing trust boundaries between arbitrary end users of the
same app, which is exactly the isolation Praxy's whole permission system exists to hold.

**How it was demonstrated.** Two app users were signed up in a real project; a function granting
`execute("users")` was deployed that echoes `process.env.PRAXY_FUNCTION_JWT`/`PRAXY_FUNCTION_USER_ID`
in its response. Invoking as user A, then immediately as user B, then again as user A:

- Before the fix: user B's invocation returned **user A's** user id (`Assert.Equal(userBId, ...)`
  failed with A's id instead).
- After the fix: each invocation always reports its own caller's identity, and the two JWTs differ.

**The fix.** `WarmPool.AcquireAsync` gained a `poolable` parameter. Any invocation whose env carries
`PRAXY_FUNCTION_JWT` or `PRAXY_FUNCTION_API_KEY` (`FunctionExecutionService.RunAsync`) is now **never
pooled** — always cold-started fresh, never added to the warm dictionary, and explicitly stopped by
`FunctionExecutionService` itself right after the invocation completes. Console-triggered,
anonymous/key-triggered, and scope-free schedule/event invocations are unaffected and keep full
warm-pool reuse, since they never carry an invocation-scoped credential to begin with.

**Tradeoff, recorded rather than hidden.** Every user-triggered invocation, and every scoped
schedule/event invocation, now always pays the cold-start cost — there is no way to safely reuse a
container that was ever handed one identity's credential without either delivering credentials per
request instead of at container start (a wire-contract change, out of this phase's scope) or
serializing all invocations of a container (a throughput change). This phase chose the cheap, provably
correct fix over the more invasive one; see "For later phases."

**Test.** `FunctionWarmPoolCredentialIsolationTests.cs` (new) — real Docker, asserts the isolation
property end to end (verified to fail on the pre-fix code, not just pass on the fixed code).

### D — `GitCliRepositoryCloner`'s commit SHA had no shape validation (option-injection, defense-in-depth)

**What an attacker does, in theory.** `GitCliRepositoryCloner.CloneAsync` runs
`git fetch --depth 1 origin <commitSha>` via `ProcessStartInfo.ArgumentList` (no shell — confirmed
sound, matching `docs/research/security-review.md`'s existing note). If `commitSha` began with `-`, git
would parse it as an option to `fetch` rather than a ref (classic CLI option injection), and nothing
validated its shape before this fix.

**Why it's rated Low, not demonstrated live.** `commitSha` has exactly one producer:
`GitHubPushEventParser.Parse`'s `after` field, taken verbatim from a GitHub push webhook payload —
and that endpoint only accepts requests carrying a valid HMAC signature for the instance's own
configured webhook secret (`GitHubWebhookSignature.Verify`, already confirmed sound). A real GitHub
push payload's `after` is always the actual resulting commit hash, which cannot start with `-`. There
is no API path today where an operator (or anyone else) supplies an arbitrary string that reaches this
code as `commitSha` — this is a reading-only finding, not an exploited one, and is closed as
defense-in-depth so a future caller (a "deploy this exact commit" console action, say) doesn't inherit
an unvalidated field silently.

**The fix.** `GitHubPushEventParser.Parse` now rejects any payload whose `after` doesn't match
`^[0-9a-fA-F]{7,40}$` — a `GitHubPushPayloadException`, the same failure mode as every other malformed-
payload case it already handles.

**Test.** `GitHubPushEventParserTests.Parse_rejects_an_after_field_that_is_not_a_commit_sha` (new) —
covers an `--upload-pack=...`-shaped value, a bare flag, empty, too short, and non-hex. This tightened
validation broke two pre-existing integration tests that used human-readable placeholder strings
(`"commit-1"`, `"commit-prod"`, etc.) as fake commit SHAs — `FunctionGitDeploymentTests.cs` and
`SiteGitDeploymentTests.cs` were updated to use valid-hex-shaped placeholders (`"c0000001"`, etc.)
instead; nothing about what either test actually exercises changed.

### I — The fix for C removed the only bound on concurrent function containers (found reviewing this phase's own work)

**How this was found.** Not by the implementing session — by the independent review of this phase
before merge. Worth recording as such: Finding C's fix is correct and closes a real credential leak,
and it introduced this. A security fix is not automatically a net improvement, and the review that
follows one is not a formality.

**What an attacker does.** Signs up as an app user (default Auth config is email+password with open
self-registration) and issues concurrent invocations of any function granting `execute("users")`.
No malicious code, no deployment rights — the same actor Finding C was rated Critical for.

**What they get.** One dedicated container per concurrent invocation, bounded by nothing.
`WarmPool.AcquireAsync(poolable: false)` returns `docker.StartContainerAsync(...)` *before* taking
the pool lock, so a non-poolable container never enters `_byDeployment` — which means
`EvictOverflowAsync` never sees it and `WarmPoolSize` (default 10) does not apply to it. And since
`identityScoped` is true for **every invocation triggered by an app user**, that is the primary
data-plane path for a BaaS, not an edge case: the warm pool was effectively bypassed for normal
product usage.

Nothing else bounded it. The functions rate limiter partitions per caller
(`{project}|{token-hash}`, `Program.cs`) and is a **fixed window** with `QueueLimit = 0` — it caps a
caller's arrival *rate*, not their concurrency — while a sync invocation runs inline on the request
thread and holds its container for up to `MaxSyncTimeoutSeconds` (30). One caller inside their own
60/min budget could therefore hold dozens of containers at `MemoryLimitMb` (256) each. On the
2 vCPU / 4 GB production droplet, which also runs Postgres, the API, Caddy and the site containers,
that is an out-of-memory condition reachable by a single signed-up user.

Note the shape: Finding F (no disk quota) and Finding H (preview-quota race) were both correctly
identified as resource-exhaustion risks in this same phase. This one is the same class, introduced
by the phase itself, and the original report described its tradeoff as only the cold-start cost.

**How it was demonstrated.** `FunctionIsolatedContainerCapacityTests` deploys a function that sleeps
3s, signs up one app user, and fires six concurrent invocations while polling Docker for running
containers labelled `praxy.function=true`. With the cap raised to 64 (reproducing the pre-fix
behaviour) the test fails with the real measurement:

```
Peak concurrent function containers was 6, expected at most 2.
```

Six callers, six containers. With the cap at 2 it passes: the peak never exceeds 2, the invocations
that lose the race get `503 function_capacity_exceeded` with `Retry-After`, and the ones that win
still succeed — the cap throttles rather than breaks.

**The fix.** `WarmPool` gains a semaphore bounding non-poolable containers
(`Praxy:Functions:MaxConcurrentIsolatedContainers`, default 16 — 16 × 256 MB of container limits,
sized for a small VPS). `FunctionExecutionService.RunAsync` reserves a slot before the try block and
releases it in the `finally`, *after* the container is stopped — releasing earlier would let the next
invocation start one while this one still holds its memory. A caller waits up to
`Praxy:Functions:IsolatedContainerWaitSeconds` (default 5) for a slot, which smooths a burst instead
of failing at the boundary, then gets a typed, retryable 503.

The reservation is deliberately **outside** `RunAsync`'s try block: the broad `catch (Exception)`
turns every *execution* failure into a recorded row and a 200 with `status: "failed"`, which is the
wrong shape for "the server never started this" — that has to reach the caller as a 503. The
execution row is still finalized first, so the method keeps its "must always reach a final status"
invariant.

**Known edge, accepted.** An *async* execution (the single-threaded `FunctionExecutionWorker`, or a
schedule/event trigger carrying an API key) that finds the cap exhausted is finalized `failed` like
any other execution error, rather than requeued — consistent with how every existing failure on that
path behaves, and visible in the console's executions list. With a single-threaded worker and a
default cap of 16 it takes 16 concurrent sync invocations at that exact moment to cause it. Requeuing
on capacity specifically would need a retry concept the execution model doesn't have today.

Two supporting changes: a new error type `function_capacity_exceeded` (503), and `PraxyException`
gains an optional `RetryAfterSeconds` that `ErrorHandlingMiddleware` emits as the `Retry-After`
header — CLAUDE.md's cross-phase rule is that every limit is configurable and *loud* when tripped,
and a 503 that doesn't say when to come back is only half of that.

**A note on why the test mattered.** The first run with the cap set to 2 still measured a peak of 6.
The options existed on the record and were read by `WarmPool`, but nothing bound them from
configuration in `Program.cs` — the knob was dead config. A test asserting
`MaxConcurrentIsolatedContainers == 2` would have passed; the one asserting the live container count
caught it.

**Test.** `FunctionIsolatedContainerCapacityTests.Concurrent_user_triggered_invocations_cannot_exceed_the_isolated_container_cap`
— real Docker, asserts the live container count, verified to fail on the unbounded configuration.

### J — Containers orphaned by a hard crash were never reclaimed (found reviewing this phase's own work)

**What happens.** No function container is meant to outlive the api process: `WarmPool.DisposeAsync`
stops the pooled ones on graceful shutdown, and — since Finding C — `FunctionExecutionService` stops
each non-poolable one in its own `finally`. A **hard** stop (SIGKILL, the OOM killer, a host reboot)
skips both: the pool's tracking is in memory and dies with the process, leaving every running
container behind, holding its memory and CPU reservation with nothing that will ever reclaim it.

The `praxy.function=true` label was already being written on every container for exactly this kind of
bookkeeping. Nothing read it.

**Severity, and why it is Low but not nothing.** Not directly attacker-triggered — it needs a crash.
It compounds rather than stands alone: Finding I's exhaustion is precisely the kind of thing that
*causes* an OOM kill, and the orphans left behind then make the next start worse. Pre-existing
(the pool has always been in-memory), so not introduced by this phase — but the phase's own changes
made the leak wider, since non-poolable containers are untracked by design.

**The fix.** `DockerExecutor.RemoveOrphanedContainersAsync` lists containers by that label and
removes them; `FunctionPoolSweeper` calls it once before entering its loop, the same shape
`FunctionExecutionWorker.ResetStuckAsync` already uses to requeue rows a previous process left
mid-flight. A non-zero count logs a warning rather than passing silently — it means the last process
died badly, which is worth seeing.

**Two things deliberately not done, recorded so they are not "fixed" later by mistake:**

- **Sites is not swept the same way.** A site container is *designed* to outlive the api process
  (`RestartPolicy: unless-stopped`, re-adopted by `SiteContainerRegistry`/`SiteReconciler`), so the
  symmetric change would take every hosted site down on restart. The asymmetry is intentional and is
  commented as such at the call site.
- **This assumes one api process per Docker daemon** — what `deploy/docker-compose.yml` runs, and
  what `WarmPool`'s in-memory tracking already requires. A second replica sharing the daemon would
  sweep the first's live containers. If Praxy ever runs multiple api instances, this needs an
  instance id in the label rather than removal by label alone.

**Test.** `FunctionContainerIsolationTests.An_orphaned_function_container_left_by_a_crash_is_reclaimed`
— creates a container carrying the real label to stand in for a crash orphan, runs the reclaim, and
asserts it is gone. The startup wiring itself is a single call inside an already-registered hosted
service and is not separately covered.

## 3. Accepted risks — reopened, and all closed

The five items below were each accepted with reasoning during the phase itself. On the owner's
instruction ("I don't want any gaps I might forget about later") they were reopened and fixed. The
original reasoning was not wrong — it was a scope judgement — so what changed for each is recorded
here rather than the acceptance being quietly deleted.

- **E — floating base image tags → resolved digest is now recorded.** The original objection stands
  and the default did **not** change: digest-pinning `node:22-alpine` would freeze every self-hosted
  instance on one Node build until someone bumped it by hand, and with no auto-update mechanism
  silently missing security patches is the worse failure. What was actually wrong is that the drift
  was *invisible* — nothing recorded which image a deployment built against, so "did this build pick
  up a new base?" was unanswerable afterwards. Every build log now ends with
  `Base image node:22-alpine resolved to node@sha256:...`
  (`DockerExecutor`/`SiteDockerExecutor.TryResolveImageDigestAsync`, best-effort, never a build
  failure). An operator who *does* want a frozen base sets `NodeBaseImage`/`DartBaseImage` to a
  `name@sha256:...` reference — that already worked and is now documented in `docs/self-host.md`.

- **F — no per-container disk quota → the writable layer was removed instead of measured.** The
  original objection was right that Docker has no portable quota (`--storage-opt size=` needs
  devicemapper, or overlay2 on XFS with pquota). The move is to stop trying to *measure* the
  writable layer and get rid of it: `ReadonlyRootfs = true` plus size-capped, RAM-backed tmpfs
  mounts, which work on every storage driver. A container that writes in a loop hits `ENOSPC` on its
  own tmpfs; the host disk that Postgres and every other container share is untouchable. Knobs:
  `Praxy:Functions:TmpfsSizeMb` (64) and `Praxy:Sites:TmpfsSizeMb` (256).

- **`ReadonlyRootfs` (part of B) — shipped, and the deferral's own doubt is what the test now
  covers.** The phase deferred it because its Next.js check "exercises neither ISR (revalidate) nor
  the image optimizer, both of which write to `.next/cache` at runtime." Correct, and precisely the
  risk — so Sites gets a **second** tmpfs at `/app/.next/cache`, and
  `SiteReadonlyRootfsTests` deploys an app that writes through Next.js's own ISR machinery *and*
  probes each path from an API route. Two details that would have broken this silently: the tmpfs
  mounts need explicit ownership (`mode=1777`, `uid/gid=65534`), because a tmpfs defaults to
  root-owned and the containers now run as 65534 from Finding B's own `USER` directive.

- **G — egress → default unchanged, now an actual switch.** Allowing egress remains right: a
  function calling a third-party API is the normal case, and deny-by-default would need an allowlist
  mechanism neither subsystem has. The gap was that it was an *unexamined* default with no way to
  change it. `deploy/docker-compose.yml` now carries a commented `internal: true` on both networks
  with an explanation of exactly what breaks, and `docs/self-host.md` documents it.

- **H — preview-quota TOCTOU → closed with an atomic gate.** The original call ("a real design
  change, not a phase-1 tweak") over-estimated it: the same reserve-before-start shape used for
  Finding I applies directly. `SiteContainerRegistry.EnterProjectColdStartAsync` is held by
  `SiteProxyMiddleware` across the quota check *and* the registration that satisfies it, so the
  count can no longer go stale underneath the check. Only cold starts in the *same project*
  serialize — the warm path never reaches it, and different projects never wait on each other.
  **Lock ordering matters:** this gate is always taken before the existing per-deployment start
  gate, never after; the doc comments on both say so, because reversing it anywhere would deadlock.
  `QuotaService.EnsurePreviewQuotaAsync`'s own comment (which recorded the race as accepted) is
  updated, and now says the caller owns the gate.

## 4. Multitenancy prerequisites

Everything below is fine — or merely an operational annoyance — under today's single-trusted-operator
model, and becomes a hard blocker the moment a second, untrusted project developer shares an instance.

- **Findings A, B and C were, before this phase, exactly this class of defect** — quietly fine because
  the one operator running the box was also the only person who could deploy functions, and now closed.
  Recorded here as the concrete proof of the prompt's own point: this is precisely the kind of thing
  that gets designed around, silently, years before multitenancy is ever built.
- **F and H were on this list and are now closed** — a per-project disk cap and an enforceable
  preview-container cap were both named here as things a real multitenancy release would need first.
  Both were fixed in the follow-up pass above, so neither is outstanding. Recorded rather than
  deleted, because "why is this not on the prerequisites list?" is a question worth having an answer
  to.
- **The Docker socket itself** (`/var/run/docker.sock` mounted into `api`) remains the standing,
  explicitly-conceded prerequisite this whole initiative works around rather than re-litigates
  (`deploy/docker-compose.yml`'s own inline comment, and this phase's own non-goals). No sandboxed
  per-tenant alternative exists in this release; any multitenancy design has to either accept that
  every tenant's function/site build effectively has root-equivalent host access today, or replace this
  mechanism entirely (gVisor/Kata/Firecracker-style isolation, a remote builder, etc.) — a decision far
  bigger than this review.

## 5. For later phases

- **Phase 2 (the HTTP edge)**: nothing new spotted here beyond what the design doc already scoped —
  this phase's reading stayed inside the container-execution seam and didn't touch
  `SiteProxyMiddleware`, `SiteHostPattern`, or the Storage transform/derivative path.
- **Phase 3 (authorization/credential scope)**: now that `PRAXY_FUNCTION_JWT` is guaranteed fresh per
  invocation (Finding C's fix), it's worth Phase 3 asking whether its *lifetime*
  (`AccountJwtService.DefaultLifetime`) and scope are still right — a short-lived token minted fresh
  per cold-started invocation is a different risk shape than one that might have sat in a warm
  container's env for minutes, and the fix in this phase didn't revisit that question.
- **A real fix for Finding C's tradeoff** — now more pressing, since Finding I bounds capacity and
  therefore converts what was an unbounded resource spike into a throughput ceiling that legitimate
  traffic can hit. If the cold-start cost on every user-triggered invocation
  turns out to matter in practice: deliver invocation-scoped credentials through the per-request
  envelope (`DockerExecutor.InvokeAsync`'s wire contract already carries `headers`) instead of container
  startup env, with the generated wrapper setting/clearing them around each request. This was
  considered and deliberately not done this phase — it's a wire-contract change across both wrapper
  languages (and Dart's `Platform.environment` is read-only at runtime, meaning Dart functions would
  need a different delivery mechanism than "still looks like an env var" entirely) — exactly the
  "don't redesign a shipped subsystem mid-review" line the prompt draws.
- **`docker network inspect`/`docker inspect` readability of a container's env** (including decrypted
  function secrets) is real but requires Docker socket access — the host-root actor this review
  explicitly excludes. Worth a one-line mention in a future security document as "the socket mount
  means secrets-in-env is not a meaningfully separate boundary from host-root," but not a finding on
  its own.

## Commands

New/changed since the last report's Commands section:

- `Praxy:Functions:PidsLimit` (default `256`) / `Praxy:Sites:PidsLimit` (default `512`) — the process
  count cap applied to every function/site container's `HostConfig`.
- **Upgrading past this phase requires `docker compose down` before `docker compose up -d --build`** —
  see `docs/self-host.md`'s Upgrading section and Finding A above for why the normal in-place upgrade
  fails outright (safely) for this one release.
- `Praxy:Functions:MaxConcurrentIsolatedContainers` (default `16`) / `Praxy:Functions:IsolatedContainerWaitSeconds`
  (default `5`) — how many containers may run for invocations that carry an invocation-scoped
  credential (never pooled, see Findings C and I), and how long a caller waits for a slot before
  getting `503 function_capacity_exceeded` with `Retry-After`. Raise the first on a larger host: the
  default is sized as 16 x `MemoryLimitMb`.
- No new runtime dependency, no new required configuration — every other change (`CapDrop`,
  `SecurityOpt`, the non-root `USER` in generated Dockerfiles, the commit-SHA validation) is automatic.
