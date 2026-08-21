# Research — Praxy Sites (Next.js hosting), design + phased rollout

Source: study of Appwrite Sites' public docs and blog posts, cross-referenced against Praxy's actual
`src/Praxy.Functions/` implementation (`DockerExecutor.cs`, `RuntimeTemplates.cs`, `FunctionBuildWorker.cs`,
`WarmPool.cs`), `deploy/Caddyfile`/`deploy/up.sh` (read directly, not assumed), and the just-shipped
`sdk/js/` Next.js SDK (`@praxy/core`/`@praxy/react`/`@praxy/nextjs`, PR #6). `docs/research/nextjs-sdk.md`
closed with a note for whoever eventually planned Sites: extend the Functions pipeline rather than invent
hosting from scratch. This is that plan.

Owner decision taken 2026-08-20: **subdomain-per-site public routing** (`<key>.<projectId>.sites.<domain>`,
Appwrite-style), accepting the wildcard-DNS/on-demand-TLS ops burden that implies, over a path-based or
console-preview-only alternative.

---

## What Appwrite Sites does

- Sites is a sibling product to Functions in Appwrite's UI, but internally reuses the same
  container-based, control-plane-managed execution model (Open Runtimes executor) Functions already has —
  Appwrite's own materials frame SSR as "handled through Appwrite's existing Functions infrastructure
  rather than a wholly separate hosting runtime."
- A Site is configured with a **framework preset** (Next.js, Nuxt, SvelteKit, Astro, Remix, Vue, Angular,
  Flutter web, static), an **install command**, **build command**, **output directory**, optional **root
  directory** (monorepo support), and an **SSR toggle**.
- Next.js: runs in a container-based Node.js runtime (not an edge/isolate model), so "all Next.js features
  work without extra configuration" — no Vercel-specific adapter needed. Supports both default and
  **standalone** output mode; standalone is called out as reducing build/cold-start time. Default build
  config: install `npm install`, build `npm run build`, output `./.next`.
- Deployment sources: Git integration (auto-deploy on push, **preview deployments per PR**), the Appwrite
  CLI, or direct file upload via console.
- Environment variables are available at build and/or runtime, configured through the console.
- Custom domains; SSR execution "at the user's nearest edge location" via Appwrite's own CDN/edge network —
  a cloud-specific capability a single-node self-hosted Praxy instance has no equivalent for.

Sources: [Free Next.js hosting with Appwrite Sites](https://appwrite.io/blog/post/free-nextjs-hosting),
[Deploy a Next.js app to Appwrite Sites](https://appwrite.io/docs/products/sites/quick-start/nextjs),
[Server Side Rendering docs](https://appwrite.io/docs/products/sites/rendering/ssr),
[Announcing Appwrite Sites](https://appwrite.io/blog/post/announcing-appwrite-sites).

---

## What Praxy already has to build on

1. **The Functions pipeline** (`src/Praxy.Functions/`, Phase 7) already solves "user uploads a tar, Praxy
   builds a Docker image, Praxy runs it self-hosted, including inside the compose stack where `api` itself
   is containerized." Reusable *patterns* (not code — see "Why Sites needs new infrastructure" below):
   - `RuntimeTemplates`'s build-context generation: re-read every entry from the uploaded tar via
     `System.Formats.Tar`, re-emit a fresh minimal entry (name/mode/data only, dropping macOS `bsdtar`'s
     `com.apple.provenance` PAX attribute that breaks the build on Linux), then append a generated
     Dockerfile.
   - `DockerExecutor`'s dual networking mode: `Praxy:Functions:DockerNetwork` empty (dev, `api` bare on
     host) → publish the container's port to a random host port; set (self-hosted compose, `api` itself
     containerized) → attach to a named Docker network and reach the container by its own IP (both
     `HostConfig.NetworkMode` *and* `NetworkingConfig.EndpointsConfig` must be set — `EndpointsConfig`
     alone silently leaves the container on the default bridge).
   - The `FOR UPDATE SKIP LOCKED` background-worker claim pattern (`FunctionBuildWorker`,
     `SchemaJobRunner`, `WebhookDeliveryWorker` all share it).
   - Encrypted env var storage via `InstanceKey` (AES-256-GCM, already exists from Phase 1 OAuth token
     storage — no new key-management infra needed).
   - Console UI shape: list → detail-with-tabs → deployments page with tar upload + a `Sheet` tailing the
     build log via a `refetchInterval` that turns itself off once the build settles.
2. **A working Next.js client SDK** (`@praxy/core`/`@praxy/react`/`@praxy/nextjs`, PR #6, merged
   2026-08-20). A hosted site is architecturally just "an app shaped like `sdk/js/examples/nextjs`, but
   built and run by Praxy instead of `next dev` locally" — the session-cookie/JWT bridge a hosted app needs
   to talk to Praxy at runtime already exists and needs zero new SDK work for v1.
3. **Organization → Project scoping** and the Functions console pattern to clone directly for a `Site`
   resource under Project.

### Why Sites needs new infrastructure, not just a `Praxy.Sites` copy of `Praxy.Functions`

Functions' run model is the wrong shape for hosting a web app. Functions invokes a warm-pool container
per-request with a capped, buffered **JSON envelope** (`{statusCode, body, headers}`, response capped at
`Praxy:Functions:MaxResponseCaptureBytes`, no streaming, no binary — an explicitly documented known gap of
that feature) and idle-sweeps containers after `MaxIdleSeconds`. A Next.js site needs a container that is
**started once on deploy and stays running** (crash-restarted, not idle-swept), reachable by a
**transparent HTTP reverse proxy** (preserving streaming responses — Next.js can stream React Server
Components — and arbitrary headers/binary bodies), addressed by **hostname**, not invoked by an API call.
This is the one genuinely new piece of infrastructure Phase 1 has to build.

---

## Architecture decisions for Phase 1

### Routing — subdomain-per-site

Each site is reachable at `<site.key>.<project.id>.sites.<PRAXY_DOMAIN>`. The project id is included
because a site's `key` is only unique within its project (mirrors `Functions.Key`'s `(project_id, key)`
uniqueness) — reusing that instead of inventing a new globally-unique-slug constraint keeps the naming
scheme consistent with how the console already URLs everything (`/project/$projectId/...`). In
local/plain-HTTP dev (no `PRAXY_DOMAIN` set), the equivalent is `<key>.<projectId>.sites.localhost`, which
resolves to `127.0.0.1` in every modern browser/OS resolver with no DNS setup — `dotnet run` dev stays
frictionless.

This is new self-host ops burden the owner is accepting: today's `deploy/Caddyfile` and `deploy/up.sh`
(read directly for this research) implement **single-domain, standard HTTP-01 ACME** — one `PRAXY_DOMAIN`
the owner types in on first run, one A record. Sites needs a **wildcard DNS record** (`*.sites.<domain>` →
the box) in addition to that. Must be documented clearly in `docs/self-host.md` as part of Phase 1's own
deliverables.

### TLS — Caddy on-demand TLS, not a DNS-01 wildcard cert

A single wildcard cert via DNS-01 would require Caddy to hold DNS-provider API credentials
(Cloudflare/Route53/etc.) — a hard requirement on which registrar/DNS host the self-hoster uses, and a new
secret to manage. **On-demand TLS** is the standard Caddy pattern for exactly this shape of problem
("arbitrary number of dynamically created subdomains, issue certs lazily, first request pays the ACME
round-trip") and needs no DNS provider integration — any registrar that can point an A/AAAA record works.

It requires one new small, security-sensitive piece: an "ask" endpoint Caddy calls before issuing each
cert (`tls { on_demand }` config), which Praxy must implement to return `200` **only** for a hostname that
resolves to a real, enabled site with an active deployment — otherwise the box becomes an open oracle for
anyone to mint certs against, and a vector to burn through Let's Encrypt's rate limits. Proposed:
`GET /v1/sites/_ask-tls?domain=<host>`, unauthenticated (Caddy calls it), strict allow-list logic only, no
other behavior.

Caddy's own config stays simple: one additional site block matching `*.sites.{$PRAXY_DOMAIN}` with
`tls { on_demand }`, reverse-proxying to the same `api:8080` upstream Caddy already forwards to today —
Caddy does not need per-site awareness. All Host-header → container dispatch happens inside `api`.

### Serving — a reverse-proxy middleware inside `Praxy.Api`

Not a copy of `DockerExecutor.InvokeAsync`'s JSON-envelope model — this needs a real streaming-capable
HTTP reverse proxy (method/headers/body/chunked response passthrough). The natural choice is
`Yarp.ReverseProxy` (Microsoft's ASP.NET Core reverse proxy library) as a new pinned dependency. **The
Phase 1 implementation session must research and pin it in `docs/research/dotnet-stack.md` before adding
it**, per the standing package-pinning rule — this research doc does not do that pinning. The middleware
activates only when the request's `Host` header matches the sites wildcard pattern, resolves
`<key>.<projectId>` to the site's currently-active deployment's container address, and proxies straight
through. Everything else (the console, `/v1` API) is unaffected — this is an early-pipeline branch, not a
change to existing routing.

### Container networking

Reuse `DockerExecutor`'s exact dual-mode logic, but with a **new, separate named network**
(`praxy-sites`, mirroring `praxy-functions`) rather than sharing Functions' network — keeps the two
features' containers unable to reach each other by default, a smaller blast radius if either is
compromised, consistent with Praxy's deny-by-default posture elsewhere.

### Container lifecycle

Unlike Functions' warm pool (ephemeral, idle-swept), a site's active deployment's container is started on
activation and left running, with Docker's own `RestartPolicy: unless-stopped` handling crash recovery —
no application-level health-check loop needed beyond initial startup readiness (poll the container's HTTP
port until it responds, bounded by a `Praxy:Sites:StartupTimeoutSeconds` timeout, analogous to Functions'
cold-start wait but against the app's own root path since Sites doesn't control the app's routes to add a
`/_health` contract the way Functions' generated wrapper does). On `api` startup, a lightweight
reconciliation pass should ensure each enabled site's active deployment has a running container (handles
the case where `api` itself crashed mid-activation) — new; Functions has no equivalent because its
containers are acquired lazily per-invocation rather than expected to be continuously present.

### Build — multi-stage Dockerfile, standalone output required

Generated the same way `RuntimeTemplates.Dockerfile` is today (a `SiteRuntimeTemplates`-equivalent), but
Next.js-specific:

```dockerfile
FROM node:22-alpine AS builder
WORKDIR /app
COPY . .
RUN npm install
RUN npm run build
FROM node:22-alpine AS runner
WORKDIR /app
ENV NODE_ENV=production
COPY --from=builder /app/.next/standalone ./
COPY --from=builder /app/.next/static ./.next/static
COPY --from=builder /app/public ./public
EXPOSE 3000
CMD ["node", "server.js"]
```

This requires the user's `next.config.js`/`.mjs` to set `output: "standalone"` — Phase 1 **requires** this
rather than supporting Next.js's default (non-standalone) output mode, which would need shipping the full
`node_modules` tree into the runtime image (heavier, messier multi-stage copy). Build should fail fast
with a clear, actionable error (surfaced in the build log, same mechanism as Functions' entrypoint-
validation errors) if `.next/standalone` doesn't exist after `npm run build`. Document this constraint
prominently in the Sites onboarding UI/docs.

### Env vars — build time and runtime both

Unlike Functions (runtime-only), Sites env vars are injected at **both build time and runtime** — Next.js
inlines `NEXT_PUBLIC_*` vars at build time (`npm run build`), and reads all other vars from `process.env`
at runtime. Injecting the full set at both stages is safe (Next.js only inlines vars explicitly referenced
with the `NEXT_PUBLIC_` prefix in code) and needs no new storage shape — same encrypted `site_env_vars`
table Functions already has as `function_env_vars`, just consumed at two points in the pipeline instead of
one.

### Static-only sites — no separate fast path

A standalone Next.js server serves prerendered/static pages fine on its own; one code path (always build
and run a container) is simpler than branching on framework output mode, and can be revisited as an
optimization later if container overhead for static sites proves wasteful in practice.

### Quotas

Extend the existing `QuotaService` (Phase 9, `src/Praxy.Tables/Quotas/`) with a `sites` dimension (max
sites per project), following the exact pattern already used for projects/databases/tables.

---

## Data model (for Phase 1's implementation session)

New `praxy`-schema tables, mirroring `functions`/`function_deployments`/`function_env_vars`:

- `sites`: `id, project_id, key, name, root_directory, enabled, active_deployment_id, created_at,
  updated_at`. Unique `(project_id, key)`. (`framework` is deliberately omitted from v1 — only Next.js
  exists, so there's nothing to select between yet; add it when a second framework lands.)
- `site_deployments`: `id, site_id, project_id, source_size_bytes, status(queued|building|ready|failed),
  build_log, error, image_tag, container_id, created_at, updated_at, activated_at`.
- `site_deployment_sources`: `deployment_id (PK, FK), tar bytea` — split out exactly like
  `function_deployment_sources`, deleted once the build finishes.
- `site_env_vars`: `id, site_id, key, protected_value, created_at, updated_at`. Unique `(site_id, key)`.

New project: `src/Praxy.Sites/` (`SitesOptions`, `SitesService`, `SiteBuildWorker`, `SiteRuntimeTemplates`,
a reverse-proxy middleware component, a startup reconciliation service), following the same shape as
`src/Praxy.Functions/`. Whether the Docker build-context/tar-rewrite logic and the dual-network container
start logic get extracted into a shared library or duplicated is an implementation-session call — Praxy's
existing precedent (Webhooks/Functions/Messaging are each independent sibling projects that don't share a
generic worker-loop abstraction) leans toward duplicating the small amount of logic needed rather than
introducing a shared abstraction prematurely; the implementation session should make this call once it's
looking at the actual code, not before.

Console: `SitesPage.tsx` (list) → `SiteDeploymentsPage.tsx` (tar upload + build-log `Sheet`, cloned from
`FunctionDeploymentsPage.tsx`) → `SiteSettingsPage.tsx` (env vars, root directory, danger zone), all
children of `projectRoute` exactly like the Functions routes, gated by a new `sites` `Feature` flag in
`ProjectLayout.tsx` / `useCapabilities()`. The site's live public URL (once a deployment is active) should
be shown prominently on the list/detail screens.

---

## Phased rollout

- **Phase 1** (see `docs/handoff/sites-phase-1-prompt.md`): everything above — Next.js only, subdomain
  routing + on-demand TLS, console upload (no git integration), single always-on container per site, no
  custom domains beyond the `*.sites.<domain>` wildcard, no preview-per-deployment URLs (only the active
  deployment is reachable).
- **Phase 2** (sketch only — not detailed here, do not pull forward): custom domains (owner's own domain
  pointed at a site, with its own on-demand-TLS-style ownership check); possibly preview URLs per
  non-active deployment; possibly graceful (blue-green) container swap on redeploy instead of brief
  stop-old/start-new downtime.
- **Phase 3+** (sketch only): git integration (push-to-deploy, PR previews) if the owner wants it;
  additional framework presets (Nuxt, SvelteKit, Astro, static) once the Next.js pipeline is proven — each
  is mostly a new `SiteRuntimeTemplates` Dockerfile variant plus a `framework` field on `sites`.

Explicitly out of scope indefinitely, unlike Appwrite: multi-replica/auto-scaling per site (no
orchestration layer — single-node Docker-socket model, same constraint Functions already accepted) and
edge/CDN execution (Appwrite Cloud-specific; self-hosted Praxy has one region, the box it runs on).
