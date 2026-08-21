# Session task — Sites Phase 1 (Next.js hosting)

## Why this exists

The owner asked for Praxy's own version of Appwrite Sites, starting with Next.js. Read
`docs/research/praxy-sites.md` in full before writing any code — it is the actual design spec this phase
follows: what Appwrite Sites does, what Praxy already has to build on (`src/Praxy.Functions/`'s Docker
build/run pipeline, the just-shipped `sdk/js/` Next.js SDK), why Sites needs genuinely new infrastructure
rather than a `Praxy.Sites` copy-paste of `Praxy.Functions`, and the concrete architecture decisions for
routing/TLS/serving/networking/build/env-vars. Also read `docs/roadmap.md`'s "Sites (post-v0.1.0
initiative)" section (short, points back to the research doc) and `docs/architecture.md`'s Functions
section for context on the patterns being reused.

Owner decision already made, do not re-litigate: **subdomain-per-site public routing**
(`<key>.<projectId>.sites.<domain>`), accepting the wildcard-DNS + Caddy-on-demand-TLS ops burden that
implies, over a path-based or console-preview-only alternative. This was a deliberate infra tradeoff the
owner weighed and chose — do not second-guess it back to the simpler options in `praxy-sites.md`'s
"considered and rejected" framing.

Work on a new branch off `main`. Read `CLAUDE.md` first. This is a new initiative, not a numbered phase in
the original Phase 0–9 roadmap (that roadmap is closed, tagged `v0.1.0`) — follow the same session-task
discipline the post-v0.1.0 work (`org-scoped-console`, `nextjs-sdk`, etc.) already used: one self-contained
session, a report + next prompt at the end if there's a Phase 2 to hand off.

## Non-goals — do not build these

- **No custom domains.** Only the `*.sites.<PRAXY_DOMAIN>` wildcard. Bring-your-own-domain needs its own
  ownership-verification story and is explicitly Sites Phase 2 in `praxy-sites.md`.
- **No git integration.** No push-to-deploy, no webhooks-triggered builds, no PR preview deployments.
  Deployment is console tar upload only, exactly like Functions' v1.
- **No preview URLs for non-active deployments.** Only a site's currently-active deployment is publicly
  reachable. Rolling back via `/activate` (mirroring Functions' rollback endpoint) is in scope; a separate
  URL per not-yet-activated deployment is not.
- **No frameworks other than Next.js.** Do not add a `framework` column or any framework-selection UI —
  `praxy-sites.md` deliberately omits it from v1's `sites` table ("only Next.js exists, so there's nothing
  to select between yet").
- **No graceful/blue-green container swap on redeploy.** A brief stop-old-then-start-new gap on activation
  is acceptable for Phase 1, same simplicity tradeoff Functions' own "auto-activate" made.
- **No multi-replica or auto-scaling per site.** Single container per active deployment, single Docker
  daemon, same single-node constraint Functions already accepted.
- **No static-file-serving fast path.** Every site, static or SSR, builds and runs through the same
  standalone-Next.js-server container path. Do not special-case pure static exports in Phase 1.
- **No changes to Functions.** `src/Praxy.Functions/` is reference material to read, not a project to
  modify. Sites gets its own project, its own Docker network, its own tables.

## Scope

1. **Package research first.** `docs/research/dotnet-stack.md` must be updated with a pinned,
   machine-verified entry for `Yarp.ReverseProxy` (or whatever reverse-proxy approach you land on, if YARP
   turns out to be the wrong fit once you're actually building this — but check `praxy-sites.md`'s reasoning
   for why a streaming-capable proxy library is needed before reaching for a hand-rolled `HttpClient` copy
   loop) before adding the package, per the standing rule in `CLAUDE.md`. Do this before writing the
   middleware that depends on it.
2. **`src/Praxy.Sites/`** — new project, sibling to `Praxy.Functions`, referencing `Praxy.Core`,
   `Praxy.Persistence`, `Praxy.Auth` (for `InstanceKey` env var encryption). Shape (see `praxy-sites.md`'s
   Data model section for the exact table columns):
   - `SitesOptions` — `Praxy:Sites:*` config: `DockerEndpoint`, `DockerNetwork` (dual-mode, same pattern as
     `Praxy:Functions:DockerNetwork`), `NodeBaseImage`, `BuildTimeoutSeconds`, `StartupTimeoutSeconds`,
     `MaxSourceBytes`, `MemoryLimitMb`, `CpuLimit`. Decide sensible defaults (Next.js SSR servers are
     heavier than the function-invoke workloads Functions defaults for — likely more than 256MB).
   - `SitesService` — console-facing CRUD for sites/env vars/deployments, metadata only, same split
     `FunctionsService` has between metadata operations and the worker/executor that does real work.
   - `SiteRuntimeTemplates` — generates the multi-stage Next.js Dockerfile (exact template in
     `praxy-sites.md`) from the uploaded tar, requiring `output: "standalone"` and failing the build with a
     clear, actionable log message if `.next/standalone` is missing post-build.
   - `SiteBuildWorker` — `BackgroundService`, `FOR UPDATE SKIP LOCKED` claim on `queued` `site_deployments`
     rows (same pattern as `FunctionBuildWorker`), builds the image, flushes the build log periodically,
     transitions `ready`/`failed`.
   - A container-start/activation path reusing `DockerExecutor`'s dual networking logic but targeting the
     new `praxy-sites` named Docker network (not `praxy-functions`) — decide whether to extract a shared
     helper or duplicate the ~small amount of logic; `praxy-sites.md` leans toward duplicating given
     Praxy's existing precedent of independent sibling worker projects, but this is your call once you're
     looking at the actual code.
   - A **reconciliation service** run at `api` startup: for each enabled site with an active `ready`
     deployment, ensure a container is actually running; start one if not (handles `api` crashing
     mid-activation).
   - The reverse-proxy middleware: activates only on requests whose `Host` matches the sites wildcard
     pattern, resolves `<key>.<projectId>` → active deployment's container address, proxies through with
     streaming intact. This should not touch the existing `/v1` routing at all — it's an early branch in
     the middleware pipeline.
   - `GET /v1/sites/_ask-tls?domain=<host>` — the Caddy on-demand-TLS ask endpoint. Unauthenticated (Caddy
     calls it), must return `200` only for a hostname matching a real, enabled site with an active
     deployment, `403`/`404` otherwise. Get this right — a permissive version turns the box into an open
     cert-issuance oracle. Add a test that asserts a made-up hostname is rejected.
3. **EF Core migration** — `sites`, `site_deployments`, `site_deployment_sources`, `site_env_vars` under
   the `praxy` schema (columns in `praxy-sites.md`'s Data model section). Follow `Functions.cs`'s entity
   file as the template.
4. **Console admin surface** — `/v1/console/projects/{projectId}/sites/...` (CRUD, deployments, env vars,
   activate), mirroring `FunctionEndpoints.cs`'s console half. Decide whether Sites needs a data-plane
   surface at all (Functions has one for invoke — Sites likely doesn't, since a site isn't invoked through
   the API, it's browsed directly).
5. **`deploy/docker-compose.yml`** — add the `praxy-sites` named network (mirroring `praxy-functions`'s
   explicit `networks.default.name` treatment) and set `Praxy__Sites__DockerNetwork` for the `api` service.
6. **`deploy/Caddyfile`** — add the `*.sites.{$PRAXY_DOMAIN}` site block with `tls { on_demand }` pointed
   at the ask endpoint, reverse-proxying to `api:8080` (same upstream the existing block already uses).
   Check current Caddy syntax/version pin in `docs/research/dotnet-stack.md` or the compose file before
   assuming `on_demand` config shape — verify against real Caddy docs, don't guess from memory.
7. **`deploy/up.sh`** — derive the sites wildcard domain from the existing `PRAXY_DOMAIN` answer (e.g.
   `sites.$PRAXY_DOMAIN`) rather than asking a second question, keeping the documented "asks one question
   on first run" promise in `CLAUDE.md`'s Commands section intact.
8. **`docs/self-host.md`** — document the new wildcard DNS record requirement (`*.sites.<domain>` → the
   box) clearly, since Sites will silently fail to get certs without it.
9. **Console UI** — `SitesPage.tsx` (list), `SiteDeploymentsPage.tsx` (tar upload + build-log `Sheet`,
   cloned from `FunctionDeploymentsPage.tsx`'s upload-and-poll pattern), `SiteSettingsPage.tsx` (env vars,
   root directory, danger zone). New `sites` `Feature` flag in `ProjectLayout.tsx` / `useCapabilities()` /
   `CapabilitiesEndpoints.cs`. Show the live public URL prominently once a deployment is active.
10. **Quotas** — extend `QuotaService` (`src/Praxy.Tables/Quotas/`) with a `sites` dimension, same pattern
    as the existing project/database/table/column/index dimensions.

## Landmines — read before writing code

Verified against current `main` and `deploy/`, not recalled — full detail and reasoning for each of these
is in `docs/research/praxy-sites.md`; this is the condensed version.

- **Functions' JSON-envelope invoke model is the wrong shape for Sites — do not reuse
  `DockerExecutor.InvokeAsync`.** It caps and buffers the entire response (`MaxResponseCaptureBytes`), no
  streaming, no binary. Next.js can stream React Server Components; a capped-buffer proxy will silently
  break real pages. Build a genuine streaming reverse proxy.
- **`EndpointsConfig` alone silently leaves a container on the default bridge network** —
  `DockerExecutor.StartContainerAsync`'s existing code comments call this out explicitly for Functions; the
  same trap applies to Sites' container start. Set both `HostConfig.NetworkMode` and
  `NetworkingConfig.EndpointsConfig`.
- **The macOS `bsdtar` PAX-attribute bug is real and already has a fix to copy.** `RuntimeTemplates.cs`
  re-emits every tar entry fresh (name/mode/data only) specifically to strip `com.apple.provenance`, which
  otherwise breaks the Docker build on Linux when the tar was created on a Mac. `SiteRuntimeTemplates` needs
  the same re-emit step, not a raw pass-through of the uploaded tar.
- **The `_ask-tls` endpoint is a real security boundary, not a formality.** An overly permissive
  implementation lets anyone point DNS at the box and get Let's Encrypt to mint certs for arbitrary
  hostnames through it, which can also trip Let's Encrypt's own rate limits and lock out legitimate
  issuance. It must check the DB, not just parse the hostname shape.
- **`next.config.js` without `output: "standalone"` will build "successfully" but produce no
  `.next/standalone` directory** — the multi-stage Dockerfile's `COPY --from=builder /app/.next/standalone`
  step will fail with a Docker-level error that won't obviously point back at the missing config. Check for
  `.next/standalone`'s existence explicitly after the build stage and fail with a message naming the actual
  fix, before it turns into an opaque Docker COPY error.
- **Env vars need to be present at `npm run build` time, not just container runtime**, or `NEXT_PUBLIC_*`
  values silently end up undefined in the client bundle (no error — they just don't get inlined). Inject
  the full env var set as build args/ENV in the builder stage, and again at container start for the runner
  stage.
- **Don't let the wildcard proxy middleware shadow real `/v1` routes.** Match strictly on the sites
  subdomain pattern (`*.sites.<domain>` / `*.sites.localhost`) before doing anything else in that branch —
  a loose match could accidentally intercept console/API traffic on the primary domain.
- **Caddy's on-demand TLS config shape may have changed since training data** — verify the actual directive
  syntax against current Caddy docs rather than guessing, same discipline `dotnet-stack.md` already applies
  to NuGet packages.

## Tests

`tests/Praxy.Tests.Integration/` — a `SiteTests.cs` modeled on `FunctionTests.cs`'s "no in-memory transport
on the outbound leg" discipline: deploy a small real Next.js app (standalone output) against a real Docker
daemon → build succeeds → container starts → reverse-proxy request through the middleware returns the
actual page → redeploy → old container stops, new one serves → a build missing `output: "standalone"` fails
with the actionable error, not an opaque Docker error. A `SitesAskTlsTests.cs` (or folded into `SiteTests`)
specifically covering the ask-endpoint allow-list: real active site → `200`; disabled site → rejected;
made-up hostname → rejected; site with no `ready` deployment yet → rejected.

`tests/Praxy.Tests.Unit/` — `SiteRuntimeTemplatesTests.cs` (Dockerfile generation, tar re-emit, missing
`output: standalone` detection) mirroring `FunctionRuntimesTests.cs`.

Console: extend whatever owner-click-through convention the console currently has for Functions to cover
Sites (deploy from console → see build log stream → see the live public URL work in a real browser tab).

## Done means

- `dotnet test` green (unit + integration — integration needs a real Docker daemon, same requirement
  Functions' own tests have).
- Console build clean (`tsc -b && vite build`).
- **The owner test, actually run**: deploy a real minimal Next.js app (`output: "standalone"` set) from the
  console → watch the build log stream → see it go `ready` and auto-activate → visit the site's live
  `*.sites.<domain>` URL in a real browser and see the page render (SSR, not just a static shell) → redeploy
  a change → see the new version live → roll back to the previous deployment via activate → old version
  live again.
- `git status` clean, conventional commits, on a new branch off `main`.
- `docs/research/dotnet-stack.md` updated with the reverse-proxy package's pin and any Docker-networking
  corrections found along the way (same discipline Phase 7's own dotnet-stack.md section followed).
- `docs/self-host.md` updated with the wildcard DNS requirement.
- Write `docs/handoff/sites-phase-1-report.md` (what shipped, deviations from `praxy-sites.md` and why,
  known gaps) and, only if there's a clear next slice worth handing off immediately,
  `docs/handoff/sites-phase-2-prompt.md` — otherwise leave Phase 2 as the sketch already in
  `docs/roadmap.md`'s Sites section for the owner to scope fresh when they're ready for custom domains/git
  integration.

## Deploying (only if the owner asks)

This task touches `deploy/docker-compose.yml`, `deploy/Caddyfile`, and `deploy/up.sh`. Do not run these
against the live `praxycore.dev` box or apply the new wildcard DNS record without being asked — confirm
with the owner first, since this changes what's publicly reachable on their real domain.
