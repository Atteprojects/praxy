# Session task — Sites Phase 2 (preview URLs + graceful redeploy)

## Why this exists

Sites Phase 1 shipped 2026-08-21 (`docs/handoff/sites-phase-1-report.md`) with two deliberate gaps: only a
site's *active* deployment is ever reachable (no way to preview a build before promoting it), and
activating a new deployment does a brief stop-old-then-start-new, a real if short downtime window on every
redeploy. The owner wants both closed. Read `docs/research/praxy-sites.md`'s "Phase 2" section in full
before writing any code — it's the actual design spec, grounded in a direct read of the real Phase 1
source (`SiteHostPattern.cs`, `SiteContainerRegistry.cs`, `SiteProxyMiddleware.cs`, `SiteBuildWorker.cs`,
`SitesOptions.cs`, `deploy/Caddyfile`), not assumed from the report's prose. Also read
`docs/handoff/sites-phase-1-report.md` in full — its "Known gaps" section names exactly what this phase
closes, and its "post-report correction" about Caddy's wildcard-depth bug is the single most important
landmine to internalize before touching the Caddyfile again (see Landmines).

This is Phase 2 of 4 the owner has committed to (2026-08-22): preview URLs + graceful redeploy (this
phase), then custom domains (Phase 3), then git integration (Phase 4). Additional framework presets beyond
Next.js are explicitly deferred past all of them — do not add framework selection here. Work on a new
branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **No custom domains.** That's Phase 3 (`research/praxy-sites.md` has its sketch). Don't touch
  `site_domains` (doesn't exist yet) or widen `_ask-tls`'s hostname matching beyond what preview URLs need.
- **No git integration.** That's Phase 4. Deployment stays console tar upload only.
- **No additional frameworks.** Owner's explicit call — Next.js only, still.
- **No changes to the active-deployment model's public-facing behavior.** A site's production URL
  (`<key>.<projectId>.{Domain}`) must keep working exactly as it does today throughout this phase — every
  change here is additive (a new preview-URL shape, a smoother activation) or internal (the registry
  refactor), never a behavior change to the existing 2-label hostname path from a visitor's perspective.
- **No unbounded preview containers.** Every `ready` deployment getting an always-on container forever is
  explicitly the wrong model (see Scope #2) — don't ship that as a shortcut even though it's simpler than
  on-demand start + idle-sweep.

## Scope

1. **Extend `SiteHostPattern.TryParse`** (`src/Praxy.Sites/SiteHostPattern.cs`) to accept a third,
   optional leading label: `<deploymentRef>.<key>.<projectId>.{Domain}` for previews, alongside the
   existing `<key>.<projectId>.{Domain}` for production. This is the one shared parse both
   `SiteProxyMiddleware` and `_ask-tls` already consume by design (its own doc comment explains why a
   second parser would be a security bug) — extend it in place, don't fork it. Decide the exact
   `deploymentRef` shape (full deployment id is simplest and safest; a shortened form is friendlier but
   needs a collision-safe scheme — your call, document the reasoning either way).
2. **Refactor `SiteContainerRegistry`** (`src/Praxy.Sites/SiteContainerRegistry.cs`) from keyed-by-site-id
   (one entry, no eviction — correct today because only the active deployment ever runs) to keyed-by-
   deployment-id. Every call site that currently does `registry.TryGet(site.Id, ...)` needs to instead
   resolve `site.ActiveDeploymentId` first, then look that up — audit `SiteProxyMiddleware`,
   `SitesService.ActivateAsync`, and `SiteReconciler` for this pattern. Re-run the full Sites integration
   suite after — this class is load-bearing for every Sites request path.
3. **On-demand preview container start + idle sweep.** `SiteProxyMiddleware.InvokeAsync`, on seeing a
   3-label host resolving to a `ready`-but-not-active deployment with no registry entry, should start a
   container for it (reusing `SiteDockerExecutor`'s existing container-start + readiness-probe logic) bound
   by `Praxy:Sites:StartupTimeoutSeconds`, rather than 404ing. Guard against two concurrent first-requests
   to the same cold preview racing to start two containers — a per-deployment async lock, or a signal to a
   background starter mirroring `SiteBuildSignal`'s pattern, are both reasonable; pick one. Add a periodic
   sweeper (new, or extend `SiteReconciler`) that stops a preview container idle past a new
   `Praxy:Sites:PreviewIdleSeconds` (pick a sane default — start from Functions' `WarmPool`'s
   `MaxIdleSeconds=300` as a reference point, not a mandate). The active deployment's container is never
   subject to this sweep — only non-active/preview containers.
4. **New quota**: cap concurrent preview containers per site or per project (extend `QuotaService`, same
   pattern as the existing `sites` dimension from Phase 1) so a project accumulating many stale `ready`
   deployments can't exhaust host Docker/memory capacity.
5. **Caddyfile**: add whatever's needed for 3-label hostnames to get on-demand TLS certs. **Verify this
   against real Caddy, not by pattern-matching the existing 2-label fix** — see Landmines. Update
   `docs/research/dotnet-stack.md`'s Caddy section with whatever you find, the same way Phase 1's own
   session did after its wildcard-depth bug.
6. **Graceful (blue-green) activation.** In `SiteBuildWorker.BuildAsync`'s call into
   `SitesService.ActivateAsync` (and the console's explicit `/activate` rollback endpoint, which goes
   through the same method): start the new deployment's container fully (through the same readiness probe
   used today), and only once it's genuinely responding, swap the registry entry for the site from old to
   new, then stop/remove the old container. The proxy middleware should never observe a moment with no
   entry for an active site. If a redeploy's new image was already running as a preview container (from
   #3), consider promoting it directly instead of starting a second one — an optimization, not required for
   correctness.
7. **Console**: surface each `ready` deployment's preview URL in `SiteDeploymentsPage.tsx`'s deployment
   list/sheet (a "Preview" link/button per deployment, alongside the existing Activate button), and make
   clear in the UI which deployment is currently active vs. merely previewable. No new screens needed —
   this extends the existing deployments page.

## Landmines — read before writing code

- **Caddy's on-demand-TLS automation-policy subject matching is exactly as strict about wildcard depth as
  a real TLS wildcard certificate, and a depth mismatch fails almost silently.** This is not a hypothetical
  — it's exactly what happened in Phase 1: `*.{$PRAXY_SITES_DOMAIN}` (one wildcard label) silently refused
  every real 2-label site hostname (`ERR_SSL_PROTOCOL_ERROR` in the browser, the `_ask-tls` endpoint never
  even called, nothing in Caddy's logs at INFO level — only visible with `debug` logging on). `caddy
  validate` catches config *syntax* errors, not this — it passed the whole time the bug was live. Whatever
  you do for 3-label preview hostnames, prove it against a real Caddy instance and a real 3-label hostname
  actually getting a cert and serving a page, not just a clean `caddy validate` run.
- **`SiteContainerRegistry`'s current "one entry, no eviction" shape is deliberate, documented, and
  everyone reading its doc comment will assume it's still one-per-site unless you update that comment
  too.** Update the doc comment when you re-key it to deployment id — a stale comment here is exactly the
  kind of thing that causes the next session to build on a wrong mental model.
- **`SiteHostPattern`'s doc comment exists specifically to stop someone from writing a second, slightly
  different hostname parser somewhere else.** `_ask-tls` and `SiteProxyMiddleware` must both go through
  your extended version, not grow their own 3-label special case independently.
- **Starting a Docker container synchronously inside an HTTP request handler (`SiteProxyMiddleware`) is a
  new pattern for this codebase** — everywhere else, container starts happen in a `BackgroundService`
  (`SiteBuildWorker`, `SiteReconciler`) off the request path. Bound it tightly with
  `StartupTimeoutSeconds`, and make sure a slow/failed cold start returns a clear error to the visitor
  rather than hanging the request indefinitely.
- **`docker ps -a --filter label=praxy.site=true` must stay clean after a full `dotnet test` run** — Phase
  1's own test suite needed `ApiTestBase.DisposeAsync()` made `virtual` specifically because Sites
  containers are deliberately never auto-stopped by production code (unlike Functions' `WarmPool`, which
  Functions' tests could rely on being cleaned up). Any new test that starts a preview container needs the
  same explicit cleanup discipline.

## Tests

`tests/Praxy.Tests.Integration/SiteTests.cs` (extend, don't fork) — a real Docker daemon test: deploy a
site, deploy a second version without activating it, hit its preview URL and confirm it serves the new
version while the production URL still serves the old one, confirm the preview container appears and later
(with a shortened `PreviewIdleSeconds` for the test) gets swept, confirm activating the second deployment
now serves it on the production URL with no observable gap (a tight polling loop across the swap should
never see a failed request), confirm two concurrent first-requests to a cold preview don't create two
containers (`docker ps` shows exactly one). Extend `SitesAskTlsTests.cs` if the ask-endpoint's logic
changes for 3-label hostnames. Unit tests for `SiteHostPattern`'s extended parse (valid 2-label, valid
3-label, malformed variants of both) and the new quota check.

## Done means

- `dotnet test` green (unit + integration, real Docker daemon).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run**: deploy a site, deploy a second version, visit its preview URL and confirm
  it's the new version while production still shows the old one, activate it and confirm production now
  shows the new version with no visible gap (refresh repeatedly during activation), confirm an idle preview
  container gets cleaned up after the configured window, confirm hitting a very-stale preview URL still
  cold-starts it correctly on demand.
- `git status` clean, conventional commits, on a new branch off `main`.
- `docs/research/dotnet-stack.md` updated with whatever real Caddy on-demand-TLS behavior you find for
  3-label hostnames.
- Write `docs/handoff/sites-phase-2-report.md` (what shipped, deviations, known gaps — same format as
  Phase 1's report). Do **not** write `docs/handoff/sites-phase-3-prompt.md` unless something learned this
  session materially changes the Phase 3 sketch already in `research/praxy-sites.md` — otherwise leave
  Phase 3 (custom domains) for its own scoping session, per the owner's explicit sequencing decision to
  keep these as separate sessions.

## Deploying (only if the owner asks)

This touches `deploy/Caddyfile`. Do not apply changes to the live `praxycore.dev` box without being asked
— confirm first, since a Caddyfile mistake here has already caused a real (if contained) outage-shaped bug
once in Phase 1.
