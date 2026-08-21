# Sites Phase 1 — report

> **Post-report correction (found live on praxycore.dev after this phase's PR merged and deployed):**
> the Caddyfile's sites block shipped with a single-label wildcard (`*.{$PRAXY_SITES_DOMAIN}`), but a
> site's actual public hostname has *two* variable labels (`<key>.<projectId>.…`) — Caddy's on-demand
> automation-policy subject matching is as strict about wildcard depth as a real TLS wildcard
> certificate, so every real site silently failed to get a certificate at all (`ERR_SSL_PROTOCOL_ERROR`
> in the browser, ask-endpoint never even called, nothing in Caddy's own logs at INFO level). This
> phase's own "verified against real Caddy" claim below (`caddy validate` + a worked `on_demand_tls`
> example) was real but insufficient — `caddy validate` checks config syntax, not whether the
> automation policy's subject pattern actually matches the hostnames the application generates, and no
> integration test exercises real Caddy (`SitesAskTlsTests`/`SiteTests` both bypass it). Fixed
> (`*.*.{$PRAXY_SITES_DOMAIN}`), verified against a real two-label hostname getting a real Let's
> Encrypt cert and serving the actual page. Full failure signature and fix documented in
> `docs/research/dotnet-stack.md`'s Caddy section — read it before touching this Caddyfile again.

**Status: complete.** Every item in `docs/handoff/sites-phase-1-prompt.md`'s scope shipped. 519 .NET
tests green (336 unit, up from 324; 183 integration, up from 176 — 178 pre-existing plus 5 new Sites
tests, `SiteTests` × 2 and `SitesAskTlsTests` × 3), all against real Postgres (Testcontainers) and a
real Docker daemon, no mocked Docker leg. Console `tsc -b && vite build` clean. The owner-test was
run for real, in a real browser, against a real Next.js build — full transcript below, and it
surfaced (and this session fixed) a genuine rollback bug before it could ship.

## What shipped

**`src/Praxy.Sites/`** (new project, sibling to `Praxy.Functions`): `SitesOptions` (`Praxy:Sites:*`
config — Docker endpoint/network, the sites domain, base image, timeouts, resource limits, all
configurable per CLAUDE.md's cross-phase rule); `SiteRuntimeTemplates` (generates the multi-stage
standalone-output Dockerfile, re-emits the uploaded tar stripped of the macOS `bsdtar`
`com.apple.provenance` PAX attribute exactly like `Praxy.Functions.RuntimeTemplates` does, checks for
`.next/standalone` post-build and fails with an actionable message instead of an opaque Docker COPY
error); `SiteDockerExecutor` (a Sites-specific sibling of `DockerExecutor` — deliberately duplicated,
not shared, per the research doc's own framing: no `InvokeAsync` since Sites is proxied not invoked,
a plain-HTTP readiness probe instead of a shared-secret `/_health` contract, `RestartPolicy:
unless-stopped` since containers are meant to run continuously); `SiteContainerRegistry` (in-memory
site-id → running-container-address map, the Sites analogue of `WarmPool` but simpler — one entry per
site, no idle eviction); `SiteBuildWorker` (`FOR UPDATE SKIP LOCKED` claim on `site_deployments`,
same shape as `FunctionBuildWorker`, auto-activates the freshest successful build — which for Sites
means actually starting a container, not just flipping a pointer); `SiteReconciler` (an immediate
pass on `api` startup plus a periodic one, ensuring every enabled site's active `ready` deployment
has a genuinely running container — handles both `api` crashing mid-activation and the ordinary case
of every restart leaving the in-memory registry empty while the containers themselves, under
`RestartPolicy: unless-stopped`, kept running the whole time); `SiteProxyMiddleware` (YARP direct
forwarding — `IHttpForwarder.SendAsync`, not the route/cluster config model, since the destination is
resolved per request from the DB + registry — an early branch in `Program.cs`'s pipeline, before
`PlatformCorsMiddleware`/static files/routing, matching `<key>.<projectId>.{Praxy:Sites:Domain}` and
falling through untouched for everything else).

**`SiteEndpoints.cs`** (`src/Praxy.Api/Endpoints/`): console admin CRUD for sites, env vars, and
deployments under `/v1/console/projects/{projectId}/sites`, mirroring `FunctionEndpoints.cs`'s
console half exactly. No data-plane surface — decided per the prompt's own prompt: a site is browsed
directly, not invoked, so there's nothing for an API key to call. `GET /v1/sites/_ask-tls` is
unauthenticated (Caddy calls it) and returns `204` only for a hostname that parses as
`<key>.<projectId>.{Domain}` **and** resolves to a real, enabled site **and** that site's active
deployment is `ready` — anything else is `404`, including a hostname shaped correctly but pointing at
nothing real. Covered by `SitesAskTlsTests.cs`.

**EF migration** `Sites` (`sites`, `site_deployments`, `site_deployment_sources`, `site_env_vars`,
all under `praxy`), generated via `dotnet ef migrations add`, matching `praxy-sites.md`'s Data model
section column-for-column.

**Console**: `SitesPage.tsx` (list + create, with a live public-URL preview as you type the key, and
the `output: "standalone"` requirement shown up front rather than discovered from a failed build),
`SiteDeploymentsPage.tsx` (tar upload + build-log `Sheet`, cloned from
`FunctionDeploymentsPage.tsx`'s upload-and-poll pattern), `SiteSettingsPage.tsx` (root directory, env
vars, danger zone). New `sites` `Feature` flag wired into **both** `ProjectLayout.tsx`'s nav **and**
`CommandPalette.tsx`'s `DESTINATIONS` list — the second one is easy to miss (its own doc comment
says so: three earlier features' `g`-chords silently did nothing until someone noticed the palette
has its own separate source of truth for chord wiring, not just the sidebar).

**Quotas**: `QuotaService.EnsureSiteQuotaAsync` + `MaxSitesPerProject` on `QuotaOptions`/
`OrganizationLimits`, same pattern as every other dimension. Console's Usage card on Project Overview
gained a Sites row.

**`deploy/`**: a `praxy-sites` named Docker network (`api` joins both it and `praxy-functions`, kept
separate so the two features' containers can't reach each other by default);
`Praxy__Sites__DockerNetwork`/`Praxy__Sites__Domain` env vars; `deploy/Caddyfile`'s
`on_demand_tls { ask ... }` global option plus a `*.{$PRAXY_SITES_DOMAIN}` site block with
`tls { on_demand }`; `deploy/up.sh` derives `PRAXY_SITES_DOMAIN=sites.$DOMAIN` from the one domain
question it already asks, keeping CLAUDE.md's "asks one question on first run" promise intact.

**Docs**: `docs/research/dotnet-stack.md` gained the `Yarp.ReverseProxy` pin (2.3.0, verified current
against the NuGet index and a web search, confirmed to build clean on `net10.0` despite the
package's own highest explicit TFM being `net8.0`), the `IHttpForwarder` direct-forwarding shape,
`Docker.DotNet.Enhanced`'s `BuildArgs`/`RestartPolicy` APIs (verified by reflection against the
installed package, same discipline Phase 7's own research used), the real
`.next/standalone`-without-`public/` Docker COPY failure this session hit and fixed, and the current
Caddy on-demand-TLS directive syntax (verified against real Caddy docs and `caddy validate` against
the actual `deploy/Caddyfile`, including the degenerate unset-env-var case). `docs/self-host.md`
gained the wildcard DNS requirement and a Sites section mirroring the existing Functions one.

## Deviations & notes

- **Root directory support is simpler than a full monorepo workspace.** `SiteRuntimeTemplates`
  treats `rootDirectory` as "cd into this subdirectory, otherwise a self-contained Next.js app with
  its own `package.json`" — not full npm/yarn/pnpm workspace support, where Next's standalone output
  nests based on workspace-root detection in a way this template doesn't follow. Covers the common
  "app lives in a subfolder of the tar" case; a true monorepo with a shared root lockfile is out of
  scope for Phase 1 and not mentioned in the original prompt either way.
- **`RUN mkdir -p public .next/static` was added to the generated Dockerfile**, not in the original
  `praxy-sites.md` template. Found by actually building the owner-test app: a Next.js app with no
  `public/` directory (legitimate — it's optional) makes the runner stage's
  `COPY --from=builder .../public ./public` fail with an opaque "not found" error, the same class of
  failure the missing-`output: "standalone"` check already exists to preempt. Reproduced directly,
  fixed, documented in `dotnet-stack.md`.
- **A deployment's `activatedAt` is not cleared when superseded** — by design, it's a historical
  "when did this deployment last go live" timestamp, not a "is this currently active" flag (matches
  `FunctionDeployment.ActivatedAt`'s exact semantics). The console UI originally got this wrong (see
  the owner-test section below) — fixed there, not in the data model.
- **Env vars validate a stricter key charset than Functions'** (`SitesService.SetEnvVarAsync`
  additionally rejects a leading digit) because Sites env var keys are also used as Dockerfile `ARG`
  names, which need valid identifier syntax; Functions' env vars never appear in generated Dockerfile
  text, only in a runtime `Env` list, so no such constraint existed there.

## Known gaps (deliberate, per the prompt's own non-goals)

- No custom domains, no git integration, no preview URLs for non-active deployments, no graceful
  blue-green swap on redeploy (brief stop-old/start-new gap on activation), no multi-replica/
  auto-scaling, no static-file fast path, no frameworks besides Next.js — all explicitly out of scope
  for Phase 1, per the prompt.
- **Build-time `NEXT_PUBLIC_*` inlining is exercised structurally (unit-tested Dockerfile ARG/ENV
  generation) and by direct `docker build`/`docker run` during this session, but not by an automated
  integration test that actually executes client-side JS** — `Praxy.Tests.Integration` has no headless
  browser. The owner-test's real-browser pass covered the SSR/runtime-env path thoroughly (see below)
  but didn't specifically load a page with a `NEXT_PUBLIC_*` value inlined into the client bundle.
  Low risk (the mechanism is identical to the runtime-env path that *was* verified, just consumed at
  a different pipeline stage), but worth knowing if a future session wants to close it.
- **A site's public URL doesn't include a port**, correct for the intended self-hosted deployment
  (Caddy on 80/443) but means local `dotnet run` dev testing needs the port appended by hand
  (`http://<key>.<projectId>.sites.localhost:5090`, not `:5173` — the console's own dev port doesn't
  proxy site traffic). Documented in `CLAUDE.md`'s Commands section; not changed in the console since
  it would make the *shown* URL wrong for the case that actually matters (production).

## Tests

- `SiteRuntimeTemplatesTests.cs` (unit): root-directory validation, Dockerfile generation (multi-stage
  structure, the missing-standalone check runs before the runner stage, root-directory-adjusted
  COPY paths, env vars become build args declared before `npm run build`), and the macOS `bsdtar` PAX
  re-emit (built a tar with a `com.apple.provenance` extended attribute, confirmed the regenerated
  context strips it).
- `SiteTests.cs` (integration, real Docker): deploys a genuine Next.js app (`output: "standalone"`,
  `getServerSideProps` forcing per-request SSR, reading a runtime env var) end to end — build →
  ready → auto-activate → a real proxied HTTP request (Host header set to the site's hostname,
  routed through `SiteProxyMiddleware` to a real container over a real socket) returns the actual
  rendered page — then redeploys, confirms the new version serves and the old container is gone, then
  rolls back via the explicit `/activate` endpoint and confirms the original version returns. A second
  test confirms a build missing `output: "standalone"` fails with the actionable message, not an
  opaque Docker error.
- `SitesAskTlsTests.cs` (integration): made-up hostnames (both correctly-shaped and not) rejected
  without touching the database; a site with no deployment yet rejected; a real enabled site with a
  genuinely `ready`+active deployment allowed; disabling that same site rejects it again.
- `ApiTestBase.DisposeAsync()` is now `virtual` — Sites' two test classes override it to stop the
  Docker containers they started. Unlike Functions' `WarmPool` (explicitly stopped on every shutdown —
  an ephemeral cache), a site's container is deliberately left running across an `api` restart, so
  nothing in production code ever stops a test's containers either; without this, every
  `dotnet test` run silently leaked one container (and one built image) per Sites test, forever.
  Confirmed fixed: `docker ps -a --filter label=praxy.site=true` is empty after a full test run.

## Commands

- Dev API: unchanged (`dotnet run --project src/Praxy.Api`), but now also needs the Docker daemon
  Functions already required — see `CLAUDE.md`'s updated Commands section. New config:
  `Praxy:Sites:DockerEndpoint` (default `unix:///var/run/docker.sock`), `Praxy:Sites:DockerNetwork`
  (default `""`, dev mode — set to `praxy-sites` in the compose file), `Praxy:Sites:Domain` (default
  `sites.localhost`), `Praxy:Sites:NodeBaseImage` (default `node:22-alpine`),
  `Praxy:Sites:BuildTimeoutSeconds` (900), `Praxy:Sites:StartupTimeoutSeconds` (60),
  `Praxy:Sites:ReconcileIntervalSeconds` (60), `Praxy:Sites:MemoryLimitMb` (512),
  `Praxy:Sites:CpuLimit` (1.0), `Praxy:Sites:MaxSourceBytes` (25MB). Full reference:
  `docs/self-host.md`'s Configuration table and its new Sites section.
- **A site's live URL in local dev needs the API's own port appended** — `http://<key>.<projectId>.
  sites.localhost:5090`, not the console's `:5173` (which only proxies `/v1`, not the sites wildcard).
- Self-host stack: `cd deploy && ./up.sh` unchanged from the owner's side — still asks one question.
  Giving a domain now also derives `PRAXY_SITES_DOMAIN=sites.$DOMAIN` automatically; a public
  deployment additionally needs its own wildcard DNS record (`*.sites.<domain>` → the box) — see
  `docs/self-host.md`'s "Sites and the wildcard subdomain" section.
- EF migration: unchanged command; the new one is named `Sites`.

## Owner-test checklist (run by this session, all passing)

Ran against a local dev instance (`dotnet run --project src/Praxy.Api` + `npm run dev --prefix
console`), driving the real console UI in the Browser pane, with a real Docker daemon:

1. Created a site from the console ("Owner Test Blog", key `owner-test-blog`) — the create modal
   showed a live public-URL preview and the `output: "standalone"` requirement.
2. Set an env var (`PRAXY_GREETING`) from the Settings tab.
3. Uploaded a real Next.js app (a tar built with macOS's own `tar`, deliberately exercising the real
   `bsdtar` PAX-attribute quirk the codebase works around) — watched the build log stream live in the
   Deployments Sheet, including the generated `ARG PRAXY_GREETING` / `ENV PRAXY_GREETING=
   $PRAXY_GREETING` steps proving the build-arg mechanism fired for real.
4. Watched it go `ready` and auto-activate (site badge flipped to "live").
5. Visited the live `*.sites.localhost:5090` URL in a real browser tab: rendered the actual page,
   with a fresh timestamp on every reload (genuine per-request SSR, not a static shell) and the env
   var's value inlined server-side (`hello-from-praxy-owner-test`).
6. Redeployed a changed version — watched it build, go ready, auto-activate, and confirmed the live
   URL now served the new content with the old container gone (`docker ps` showed exactly one
   container for the site, not two).
7. Rolled back via the console's Activate button on the older deployment. **This step failed on the
   first attempt** — the button was permanently disabled ("Active") on the older deployment even
   though it was no longer the site's current one, because the code (mirrored from
   `FunctionDeploymentsPage.tsx`) checked `!d.activatedAt`, which stays true forever once a deployment
   has ever been activated. Fixed by comparing against the site's actual `activeDeploymentId` instead
   (`SiteDeploymentsPage.tsx`); re-ran the check and confirmed the button now correctly re-enables for
   a superseded deployment, and clicking it made the original version live again.
8. Disabled the site from Settings and confirmed both the public URL (`404`) and `_ask-tls` (`404`)
   correctly refuse it, then re-enabled it.
9. Deleted the site and confirmed its container was stopped and removed (`docker ps -a` showed it
   gone, not just stopped).

All temporary state created for this test (a scratch console-operator account, the site itself, its
Docker containers/images) was cleaned up afterward — the local dev database and Docker daemon were
left in the same state they were found in.

## Next

No `docs/handoff/sites-phase-2-prompt.md` — Phase 2 (custom domains, per-deployment preview URLs,
graceful blue-green swap, git integration, additional frameworks) needs its own scoping session when
the owner is ready; the sketch already in `docs/roadmap.md`'s Sites section and
`docs/research/praxy-sites.md`'s "Phased rollout" is enough to start that from fresh, per the
kickoff prompt's own instruction not to write one unless there's a clear immediate next slice.
