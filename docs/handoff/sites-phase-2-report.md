# Sites Phase 2 — report

**Status: complete.** Every item in `docs/handoff/sites-phase-2-prompt.md`'s scope shipped. Full-repo
`dotnet test` green — **351/351 unit, 187/187 integration** (real Postgres via Testcontainers, real
Docker daemon throughout, no mocked Docker leg), including a regenerated `docs/openapi/v1.json` for the
new `previewUrl` field. Console `tsc -b && vite build` clean. See "Full suite note" below for one retry
this took, unrelated to Phase 2's own code.

## What shipped

**Preview URLs for every `ready` deployment.** `SiteHostPattern.TryParse`
([SiteHostPattern.cs](../../src/Praxy.Sites/SiteHostPattern.cs)) now accepts a third, optional leading
label — `<deploymentId>.<key>.<projectId>.{Domain}` — alongside the existing 2-label production shape,
via one extended parse both `SiteProxyMiddleware` and `_ask-tls` consume (a new overload keeps the old
2-arg signature working). `deploymentId` is the deployment's own wire id (`Ids.Wire` — 32 lowercase hex
chars): already a valid DNS label, already what the console shows for every other resource id, needs no
shortening/collision scheme. `SiteEndpoints.cs`'s `SiteDeploymentResponse` gained a `previewUrl` field
(null until `ready`), and `_ask-tls` now allows a preview hostname whose deployment belongs to the
(enabled) site and is `ready`, active or not.

**`SiteContainerRegistry` re-keyed from site id to deployment id**
([SiteContainerRegistry.cs](../../src/Praxy.Sites/SiteContainerRegistry.cs)). Phase 1 kept one entry per
site because only the active deployment ever ran; Phase 2 previews mean several containers can be live
for one site at once. Every call site that read `registry.TryGet(site.Id, ...)` now resolves
`site.ActiveDeploymentId` first: `SiteProxyMiddleware`, `SitesService.ActivateAsync`,
`SitesService.DeleteAsync` (now sweeps *every* deployment's container, not just the active one — a
deleted site's stray preview containers must not be orphaned), `SiteReconciler`, and — found only by
running the real integration suite, not by the prompt's own audit list —
**`SiteScreenshotWorker.CaptureAsync`**, which also read the registry by site id. Missing that one made
every screenshot capture silently fail forever, only surfaced as the pre-existing
`Deploy_serve_redeploy_and_roll_back_a_real_nextjs_app` test timing out waiting for a screenshot. Fixed;
green afterward. The registry's doc comment is rewritten for the new keying, per the prompt's own
landmine about a stale "one per site" comment misleading the next session.

**On-demand preview containers + idle sweep.** `SiteProxyMiddleware` still never starts a container on
the *production* path (unchanged from Phase 1 — stays exclusively `SiteReconciler`'s and
`SitesService.ActivateAsync`'s job, so it can never race the blue-green swap below). On a cold 3-label
request for a `ready` deployment, it now starts one itself — a genuinely new pattern for this codebase
(a Docker start on the request path, not inside a `BackgroundService`) — bounded by the existing
`StartupTimeoutSeconds`, quota-checked first (`QuotaService.EnsurePreviewQuotaAsync`, new
`Praxy:Quotas:MaxPreviewContainersPerProject`, default 10, org-overridable, project-scoped since the
resource being protected — the host's Docker daemon — is shared across a project's sites), and
serialized per deployment via a new `SiteContainerRegistry.StartOrJoinAsync` (a per-deployment
`SemaphoreSlim` gate — two concurrent first-requests to the same cold preview join the same start instead
of racing two containers; verified by firing 5 concurrent requests and asserting exactly one container
via `docker ps --filter label=...`). A new `SitePreviewSweeper`
([SitePreviewSweeper.cs](../../src/Praxy.Sites/SitePreviewSweeper.cs)) stops a preview container once
`SiteContainerRegistry` hasn't seen a proxied request for it in `PreviewIdleSeconds` (default 600s — a
reference point off Functions' `WarmPool.MaxIdleSeconds=300`, not a mandate, since a real Next.js SSR
container is heavier to cold-start than an invoke-shaped Functions workload), re-checked every
`PreviewSweepIntervalSeconds` (default 60s). Never touches a deployment that's currently any site's
`ActiveDeploymentId`, however idle it looks by request traffic — checked against the DB, and the actual
removal (`TryRemoveIfIdle`) re-verifies the idle timestamp atomically at removal time to close the race
against a request that touched it a moment earlier.

**Graceful (blue-green) activation** (`SitesService.ActivateAsync`). Re-ordered from Phase 1's
start-new/flip-pointer/stop-old to: start-new (or promote an already-warm preview container for the same
deployment — the "already being previewed" optimization the prompt called out, free once previews exist)
→ **register the new deployment's container in `SiteContainerRegistry` before the DB write commits** →
flip `site.ActiveDeploymentId` and save → stop the old one. That ordering matters specifically *because*
the registry moved from site-keyed to deployment-keyed: under the old shape, `Set()` was a same-key
replace, so there was never a window with no entry to serve. Once keyed by deployment id, a naive
"flip DB pointer, then register new container" ordering reopens exactly the gap Phase 1's report called a
known limitation. Registering first closes it: a request landing in the gap still resolves the *old*
deployment id (DB not yet committed) and finds its still-running container untouched. Verified with a
tight 50ms polling loop against the production hostname running concurrently with a real `/activate` call
— asserted zero failed requests across the swap.

**Caddyfile**: a third site block, `*.*.*.{$PRAXY_SITES_DOMAIN}` (three wildcard labels). **Verified
against real Caddy**, not assumed from the 2-label fix's shape, per the prompt's explicit landmine: ran
`caddy:2` in a throwaway Docker container with `debug` logging and a local ask stub. A config with only
the existing 2-label block reproduced Phase 1's exact bug signature one label short —
`"no certificate matching TLS ClientHello"`, `"on_demand":false`, ask endpoint never called — for a
genuine 3-label hostname; adding the 3-label block made the same hostname correctly reach the ask
endpoint. Full transcript in `docs/research/dotnet-stack.md`'s Caddy section. No new DNS record needed —
a DNS wildcard matches any number of labels below its owner name (RFC 4592), unlike TLS wildcard
matching's exactly-one-label strictness, so Phase 1's existing `*.sites.<domain>` record already resolves
3-label preview hostnames with zero config change.

**Console** (`SiteDeploymentsPage.tsx`): a "Preview ↗" link column in the deployments grid for every
`ready` deployment (stops row-click propagation so it doesn't also open the sheet), and in the deployment
sheet, a clear `active — production` / `previewable` badge plus a banner linking to the preview URL
("preview this build" for a not-yet-active ready deployment, "open its preview URL" for the active one) —
makes which deployment is live vs. merely previewable unambiguous without a new screen.

## Deviations & notes

- **The full owner-test's browser file-upload step wasn't literally clicked through.** The Browser pane's
  toolset has no OS file-picker automation, and the shared local dev API instance this session found
  already running (per `docs/local-dev-instance.md`) was a long-lived `dotnet run` process predating
  every change in this phase — `dotnet run` doesn't hot-reload, so hitting it would exercise stale
  pre-Phase-2 code (confirmed: an upload attempt against it 500'd and left a container this session had
  to clean up, unrelated to Phase 2's own correctness). In its place: (1) `SiteTests.cs`'s new
  integration test drives the *exact* owner-test flow end to end — deploy, redeploy (superseding v1), hit
  v1's preview URL and confirm it serves v1 while production serves v2, 5 concurrent cold-start requests
  confirming no container race, wait for the real idle sweep to stop the preview while confirming
  production is never touched, confirm cold-start still works on a now-fully-idle preview URL, then a
  tight 50ms polling loop against production during a real `/activate` call confirming zero failed
  requests — all via real HTTP through the real `SiteProxyMiddleware` to real Docker containers; (2) the
  console's structural changes (new Preview column/badges, the 6-column empty-state grid) were verified
  live in a real browser against this session's own dev console build, including creating and deleting a
  real site through the UI with no console errors. Not independently confirmed: the Preview link/badges
  rendering against real API response data with a `previewUrl` actually populated (only checked via
  `tsc`'s type-check and the empty-state render) — low risk, since `previewUrl` is a plain string field
  consumed exactly like `imageTag` a few lines above it, but worth a real look next time this page is
  touched.
- **Full suite note**: a first whole-repo `dotnet test` attempt landed during an unrelated disk-space
  crisis on this shared machine (see below) that killed the Testcontainers Postgres mid-run, cascading
  into ~137 unrelated failures (`Connection refused` from Npgsql) across every other test class — not a
  Phase 2 regression; the Sites-scoped run (13/13) had already completed cleanly before the disk issue
  hit. Re-run cleanly after the owner freed disk space and rebooted the machine (see below): **351/351
  unit, 187/187 integration**, including `OpenApiDocumentTests` after regenerating
  `docs/openapi/v1.json` (stale only because `previewUrl` is a new response field — see
  `docs/api-reference.md`'s regenerate command).
- **Docker Desktop hung mid-session, then the host disk filled up** — two separate incidents, both
  environmental, not caused by Phase 2's own code:
  1. The daemon became unresponsive (`docker ps`/`_ping` timing out) from resource pressure on this
     shared dev machine. Restarted with the owner's explicit go-ahead (`kill -9` the wedged
     `com.docker.backend`/VM processes, relaunch). One side effect required cleanup: the shared local dev
     Postgres container (`praxy-dev-pg`, not started by this session) had no restart policy and stayed
     down after the daemon came back, breaking the shared dev API instance's queries until noticed and
     `docker start praxy-dev-pg`'d back up — the API self-healed once Postgres was reachable, no restart
     needed.
  2. Later, the host disk filled to ~130MB free (out of 460GB) — root cause: Docker Desktop's VM disk
     (`Docker.raw`) had grown to 71GB from the day's build activity, plus two stale installer disk images
     (`hdiutil`-mounted, ~4.6GB combined) left over from the Docker Desktop restart in (1), never cleanly
     detached. Freed the installer images (`hdiutil detach`, ~5GB) to restore enough headroom for tools to
     run at all, which surfaced a **third** incident: a `docker system prune -f` kicked off to reclaim
     more space wedged the daemon again (same `_ping`-timeout signature as (1)) — this time the
     auto-mode safety classifier correctly declined a second automatic `kill -9`/restart, since that
     exact intervention had already been used once. Reported the full situation and stopped there rather
     than working around the block. The owner resolved it independently (freed space, rebooted); Docker
     came back healthy on its own afterward, `praxy-dev-pg` was restarted the same way as in (1), and the
     full suite re-run above confirmed nothing was left broken. No volumes, running containers, or tagged
     images were touched by anything above.
- **Preview quota is project-scoped, not per-site** (`Praxy:Quotas:MaxPreviewContainersPerProject`) — the
  prompt left this as an implementation call. The resource actually being protected (host Docker/memory
  capacity) is shared across every site in a project, so an aggregate project-level cap matches the
  described failure mode ("a project accumulating many stale ready deployments") more directly than a
  per-site cap would.
- **The preview quota check is best-effort, not airtight**: it reads `SiteContainerRegistry`'s in-memory
  snapshot at call time, so two concurrent cold starts for *different* deployments in the same project
  can both pass before either registers — the same class of small race every other soft resource guard in
  this codebase (e.g. `WarmPool` eviction) accepts rather than serializing the whole request path over a
  distributed lock for a soft cap.
- **`SiteDockerExecutor` gained `CountRunningContainersAsync(label, ct)`**, a thin `Docker.DotNet`
  label-filter query, added so the new `SiteTests` preview-container assertions never need to shell out
  to a raw `docker ps` — keeps the "no raw CLI shell-outs" discipline the class's own cleanup methods
  already follow.

## Known gaps (deliberate, per the prompt's own non-goals)

- No custom domains (`site_domains` doesn't exist), no git integration, no additional framework presets,
  no change to the active-deployment model's public-facing behavior — all explicitly out of scope, per
  the prompt.
- Preview containers get the same env vars as the active deployment (site-level, not deployment-scoped or
  preview-specific) — matches how env vars have worked since Phase 1; not something this phase's prompt
  asked to change.
- No console surfacing of the new `MaxPreviewContainersPerProject` quota dimension in the Usage card
  (unlike `sites`, which Phase 1 added there) — the prompt asked for enforcement and configurability, not
  a new usage-display row; can be added alongside a future quota-display pass if it turns out to matter.

## Tests

- `SiteHostPatternTests.cs` (new, unit): valid 2-label and 3-label parses, the 2-arg overload still
  works, malformed variants of both shapes, case insensitivity.
- `QuotaTests.cs` (extended): `EnsurePreviewQuotaAsync` trips once the configured max is already running,
  and never counts a site's own active deployment against the preview cap — driven directly against the
  service with deployment rows seeded straight into Postgres (no Docker needed for a quota-only check).
- `SitesAskTlsTests.cs` (extended): a superseded (ready, not active) deployment's 3-label preview
  hostname is allowed; a made-up deployment id under a real site/key is still rejected; a disabled site
  rejects its previews too, not just its production hostname.
- `SiteTests.cs` (extended, real Docker): the full Phase 2 owner-test flow — see "What shipped" above.
  Confirms `docker ps --filter label=praxy.deployment=...` shows exactly one container after 5 concurrent
  cold-start requests, and exactly zero after the idle sweep runs (`PreviewIdleSeconds=3`/
  `PreviewSweepIntervalSeconds=1` overridden for the test).
- Sites-scoped `dotnet test --filter "FullyQualifiedName~SiteTests|FullyQualifiedName~SitesAskTlsTests|FullyQualifiedName~QuotaTests"`:
  13/13 green on their own, real Postgres + real Docker throughout, and again as part of the full
  351/187 whole-repo run — see "Full suite note" above.

## Commands

New config, all under `Praxy:Sites:*` (defaults shown) unless noted:

- `PreviewIdleSeconds` (600) / `PreviewSweepIntervalSeconds` (60) — how long an idle preview container
  survives, and how often `SitePreviewSweeper` checks. Never applies to a site's active/production
  container.
- `Praxy:Quotas:MaxPreviewContainersPerProject` (10, org-overridable via `organizations.limits`'
  `maxPreviewContainersPerProject`) — concurrent preview containers per project.

Everything else is unchanged from `docs/handoff/sites-phase-1-report.md`'s Commands section — same Docker
endpoint/network, domain, build/startup timeouts, resource limits, screenshot settings. A preview URL in
local dev needs the same port-append caveat as the production URL:
`http://<deploymentId>.<key>.<projectId>.sites.localhost:5090`, not the console's `:5173`.

## Owner-test checklist

Run via the real integration test (`SiteTests.Preview_urls_serve_independently_idle_sweep_runs_and_activation_has_no_gap`,
real Docker, real `npm install`/`next build`) rather than a manual browser click-through — see Deviations
above for why, and exactly what it covers. Separately, in a real browser against this session's own dev
console build: created a site, confirmed the deployments page's new empty-state grid (6 columns) renders
with no console errors, deleted the site via the Settings page's danger-zone confirmation flow, confirmed
it's gone from the list. The shared local dev instance (Postgres + API) was left in the same state it was
found in, after both mid-session environmental incidents (see Deviations) were fixed.

## Next

`docs/handoff/sites-phase-3-prompt.md` was **not** written this session — nothing learned here materially
changes the Phase 3 sketch already in `docs/research/praxy-sites.md` (custom domains, on-demand TLS
generalizing almost for free, `_ask-tls` gaining a second `site_domains` lookup path). Per the owner's
explicit sequencing decision, that's its own scoping session when the owner is ready.
