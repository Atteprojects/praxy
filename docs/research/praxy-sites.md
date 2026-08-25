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

- **Phase 1 — shipped 2026-08-21.** Everything above, plus two follow-up fixes: Caddy's on-demand TLS
  needed a two-label wildcard (`*.*.{$PRAXY_SITES_DOMAIN}`), not one — its automation-policy subject
  matching is as strict about wildcard depth as a real TLS wildcard cert, and the one-label version
  silently refused every real site with no error above debug-level Caddy logs; and a rollback bug where
  the console's Activate button stayed permanently disabled on a superseded deployment. Full report:
  `docs/handoff/sites-phase-1-report.md`.
- **Phase 2 — preview URLs + graceful redeploy** (kickoff: `docs/handoff/sites-phase-2-prompt.md`). Real
  design below — this is the immediate next phase.
- **Phase 3 — custom domains.** Real design below, one phase out — do not start until Phase 2 ships and
  gets its own kickoff prompt written from this sketch.
- **Phase 4 — git integration** (push-to-deploy, PR previews). Sketch only, the least detailed of the
  three below — the largest and most structurally different phase, needs its own dedicated
  research/scoping pass when its turn comes.
- **Additional framework presets** (Nuxt, SvelteKit, Astro, static) — explicitly deferred past all of the
  above, owner's own call (2026-08-22): "we will do that another time." Each is mostly a new
  `SiteRuntimeTemplates` Dockerfile variant plus a `framework` field on `sites` when it happens.

Explicitly out of scope indefinitely, unlike Appwrite: multi-replica/auto-scaling per site (no
orchestration layer — single-node Docker-socket model, same constraint Functions already accepted) and
edge/CDN execution (Appwrite Cloud-specific; self-hosted Praxy has one region, the box it runs on).

### Phase 2 — preview URLs + graceful redeploy

Bundled because both touch the same container-lifecycle code (`SiteContainerRegistry`,
`SitesService.ActivateAsync`, `SiteBuildWorker`) rather than because they're small.

**Preview URLs for non-active deployments.** Every `ready` deployment, not just the active one, gets its
own reachable URL: `<shortDeploymentId>.<key>.<projectId>.{Domain}` — a third label in front of today's
two-label pattern.

- `SiteHostPattern.TryParse` (`src/Praxy.Sites/SiteHostPattern.cs`) currently hard-rejects anything but
  exactly 2 labels (`labels.Length != 2`). It's the single shared parse both `SiteProxyMiddleware` and the
  `_ask-tls` endpoint consume, by explicit design (its own doc comment: "a looser one in either place
  widens what an attacker can probe") — extend it in that one place to accept 2 *or* 3 labels, don't add a
  second parser.
- The Caddyfile's on-demand-TLS block currently matches `*.*.{$PRAXY_SITES_DOMAIN}` (exactly two wildcard
  labels) — a 3-label preview hostname needs its own matching block or pattern. **Verify this against real
  Caddy, the same way Phase 1 eventually had to for the 2-label fix** — that postmortem is explicit that
  `caddy validate` only checks syntax, not whether the automation policy's subject pattern actually matches
  real hostnames, and that a depth mismatch fails silently (no ask call, no ACME attempt, just a TLS
  `internal_error` alert with nothing above debug-level Caddy logs). Test against a real 3-label hostname
  getting a real cert before calling this done.
- `SiteContainerRegistry` (`src/Praxy.Sites/SiteContainerRegistry.cs`) is currently one entry per **site**
  (`Dictionary<Guid, RunningSiteContainer>` keyed by site id), by design — its own doc comment says so,
  because today only the active deployment ever runs. Previews need it keyed by **deployment** id instead;
  the active container for a site becomes "look up `site.ActiveDeploymentId` in the same registry." This
  is a real refactor of a class every Sites code path already depends on — re-run the full Sites test suite
  after.
- Container lifecycle for previews should differ deliberately from the active deployment's always-on
  model: starting a container for every `ready` deployment forever is unbounded growth. Recommend
  on-demand start (first proxied request to a preview hostname cold-starts it, bounded by the existing
  `StartupTimeoutSeconds`) plus idle-sweep after a new `Praxy:Sites:PreviewIdleSeconds` — closer to
  Functions' `WarmPool` than to Sites' current model. Starting a container from inside
  `SiteProxyMiddleware.InvokeAsync` on a request thread needs real concurrency control (two simultaneous
  first-requests to the same cold preview must not race to start two containers) — a per-deployment
  async start-lock, or routing through a signal to a background starter the way builds already use
  `SiteBuildSignal`, are both reasonable; pick whichever fits once actually building it.
- New quota: cap concurrent preview containers per site/project, so a project with many stale `ready`
  deployments can't exhaust host resources.

**Graceful (blue-green) container swap on redeploy.** Today, `SiteBuildWorker.BuildAsync` calls
`SitesService.ActivateAsync` on build success, which — per the Phase 1 report's own "known gaps" — does a
brief stop-old-then-start-new, a real (if short) downtime window. Fix: start the new deployment's container
first, run it through the same readiness probe `SiteDockerExecutor` already has for cold activation, and
only once it's genuinely responding, atomically swap `SiteContainerRegistry`'s entry for the site from old
to new — then stop/remove the old container. The proxy middleware should never have a moment with no entry
to serve. If preview-container infrastructure ships in the same phase, a redeploy that was already being
previewed can potentially be promoted directly (already warm) instead of starting fresh — a nice-to-have,
not required for the core mechanism.

### Phase 3 — custom domains

New `site_domains` table: `id, site_id, project_id, hostname, status(pending|verified), created_at,
verified_at`. Console: add/remove a domain per site, shown alongside the existing `*.sites.<domain>` URL.

Checked how Appwrite itself handles this for **self-hosted** instances specifically (not Cloud's
managed-nameserver approach, which doesn't apply here): the owner points an A/AAAA record (or a CNAME, for
a subdomain of their own domain — apex domains can't use CNAME, a DNS protocol limitation, not an Appwrite
one) at the box, and Appwrite's own self-host docs are candid that they *don't* fully automate certificates
for custom site domains — either a manual `ssl --domain=` command per site or a Traefik DNS-challenge setup
for wildcards is required.

Praxy is in a better position here because of the Phase 1 on-demand-TLS choice: a custom domain's cert can
go through the exact same `on_demand_tls { ask ... }` mechanism already built, no DNS-provider credentials
or manual per-domain commands needed. And "verification" doesn't need a separate DNS-polling worker either
— a domain's first successful on-demand TLS cert issuance (an HTTP-01 challenge, which requires the domain
to actually resolve to the box) is itself as strong a proof of control as a dedicated DNS-TXT-record check
would be. Propose: mark a `site_domains` row `verified` the moment its first cert issuance succeeds via the
`_ask-tls` flow, no polling job.

This does widen `_ask-tls`'s responsibility meaningfully: `SiteHostPattern`'s strict 2-label parse doesn't
apply to a custom domain (no fixed shape), so `_ask-tls` — and `SiteProxyMiddleware`'s host resolution —
need a second lookup path: an exact-hostname match against `site_domains`, alongside the existing
`<key>.<projectId>.{Domain}` pattern match. It becomes the only thing standing between the box and
answering an on-demand-TLS "ask" for arbitrary attacker-supplied hostnames, not just within a wildcard
suffix — get this exactly as strict as the existing wildcard check.

Caddy needs a second, non-wildcard on-demand-TLS site block (or a catch-all) for custom domains, ordered so
it never accidentally shadows the console/API's own domain block or the sites-wildcard block.

### Phase 4 — git integration

Real design, scoped 2026-08-24 after re-checking Appwrite's actual git-deploy docs (`deploy-from-git`,
not just the self-host App-setup page cited in the original sketch) — two findings there meaningfully
**cut** scope versus that sketch, not added to it:

1. **Appwrite does not post commit statuses or PR comments back to GitHub.** Its own docs make no mention
   of it. Drop that from Praxy's design too — it was speculative in the original sketch, not something
   the reference implementation actually does.
2. **Build settings (install/build/output command) are manually configured, not auto-detected**, in
   Appwrite's own flow. Moot for Praxy anyway — the build pipeline is already fixed to Next.js +
   `SiteRuntimeTemplates`'s generated Dockerfile; there is no framework-detection step to add.

The other key finding **reuses** existing infrastructure directly: Appwrite's own branch model is "push to
the production branch → build and activate immediately; push to any other branch → build, don't activate,
generate a preview link." That preview-link half is *exactly* what Sites Phase 2 already built — a
non-production-branch push just needs to create a `SiteDeployment` and let it sit `ready`-but-not-active,
and the existing `<deploymentId>.<key>.<projectId>.{Domain}` preview URL mechanism (on-demand start, idle
sweep, quota) already does the rest with zero new serving-side code.

**Self-hosted setup** (Appwrite's `version-control` self-host doc, re-confirmed): the instance owner
creates and configures **their own** GitHub App — Appwrite provides no shared one for self-hosters, and
neither should Praxy. Six config values in Appwrite's own naming
(`_APP_VCS_GITHUB_APP_NAME`/`APP_ID`/`CLIENT_ID`/`CLIENT_SECRET`/`PRIVATE_KEY`/`WEBHOOK_SECRET`), a webhook
URL (`/v1/vcs/github/events` in their naming), an OAuth installation callback, and — important operational
caveat carried into Praxy's own design — **the instance must be internet-reachable** for GitHub to deliver
webhooks; a bare `localhost` dev setup needs a tunnel (ngrok or equivalent) to test this phase at all.
`praxycore.dev` is already public, so the real owner-test should target that, not local dev.

### Not Sites-only — one GitHub App integration is meant to serve Functions too, eventually

Raised by the owner before this phase started, and worth designing for now rather than retrofitting
later: `FunctionsService.CreateDeploymentAsync` (`src/Praxy.Functions/FunctionsService.cs`) is nearly
identical in shape to `SitesService.CreateDeploymentAsync` — same tar-size validation, same entity
pattern, same build-signal notify. A future "git integration for Functions" phase would want the exact
same GitHub App, the exact same installation, the exact same webhook signature verification — none of
that is Sites-specific in any real sense, it's just where the *first* consumer happens to live.

So the instance-level pieces below live in a **new, small shared project, `Praxy.Vcs`** (sibling to
`Praxy.Sites`/`Praxy.Functions`, referencing only `Praxy.Core`/`Praxy.Persistence`/`Praxy.Auth`, and
critically **not** referencing `Praxy.Sites` or `Praxy.Functions` — dependencies point inward, the same
direction every other sibling project already follows): the `VcsInstallation` entity, GitHub App JWT
signing + installation-token exchange, and GitHub webhook signature verification as a pure function.
Endpoints live at `/v1/vcs/github/callback` and `/v1/vcs/github/webhook` — no `/sites/` prefix, matching
Appwrite's own resource-agnostic naming and signaling that these aren't a Sites feature that Functions
happens to reuse, they're shared infrastructure Sites is simply the first to consume.

What does **not** move into `Praxy.Vcs`, deliberately: routing a parsed push event to the right
deployments. `Praxy.Vcs` verifies the signature and hands back a typed, parsed payload (repository, ref,
commit); the **webhook endpoint itself** (in `Praxy.Api`, or a thin dispatcher Sites owns) is what queries
`sites` for a matching `repository_full_name` and creates a `SiteDeployment`. This is a deliberate
restraint, not an oversight — inventing an abstract "connected resource" interface now, before a second
consumer (Functions) actually exists, risks designing the wrong abstraction on spec. When Functions git
integration ships later, that same endpoint gains a second, parallel query against `functions` — two
straightforward DB queries side by side, the same "duplicate a little rather than build a shared
abstraction prematurely" judgment call this codebase has made consistently (Webhooks/Functions/Sites are
already independent siblings that don't share a generic worker-loop abstraction either).

**Praxy's shape**:

- New `Praxy.Vcs` project. Config lives at `Praxy:Vcs:GitHub:*` (not `Praxy:Sites:GitHub:*` — instance-
  wide config for instance-wide infrastructure): `AppId`, `ClientId`, `ClientSecret`, `PrivateKey` (PEM),
  `WebhookSecret`.
- `GET /v1/vcs/github/callback` — GitHub's installation-flow redirect target; exchanges the installation
  for a stored record (new `vcs_installations` table, owned by `Praxy.Vcs`: `id, installation_id,
  account_login, account_type, created_at` — instance-wide, since one GitHub App installation can cover
  repositories used by any project, any resource type).
- `POST /v1/vcs/github/webhook` — verifies GitHub's own signature format (see Landmines — **not** the same
  scheme `Praxy.Webhooks`' `WebhookSignature` class uses) via `Praxy.Vcs`, parses `push` events, then (this
  part lives outside `Praxy.Vcs`, see above) matches `repository.full_name` against connected `sites` rows
  and creates a `SiteDeployment` sourced from a fresh clone rather than an upload.
- `sites` gains `repository_full_name`, `production_branch` (both nullable — a site can still be
  tar-upload-only, unchanged). `site_deployments` gains `source` (`upload`|`git`), `commit_sha`,
  `commit_message`, `branch` — for console display and to know which deployments came from a push.
- Build source: a git-sourced deployment clones (shallow, at the pushed commit, using a short-lived
  installation access token minted via `Praxy.Vcs` — see Landmines for the two-step token exchange)
  instead of reading an uploaded tar. `SiteRuntimeTemplates.BuildContextAsync` currently starts from a
  `MemoryStream` of tar bytes; the cleanest extension is a sibling method building the same Docker context
  directly from a checked-out directory, skipping the tar round-trip entirely for this path — the
  generated Dockerfile itself doesn't need to change at all, only where the source files come from.
- Console: a "Git repository" card on Site Settings (mirroring the "Custom domains" card Phase 3 just
  shipped) — connect/disconnect, pick production branch from the repo's real branch list, show the
  connected repo + branch once set. The instance-level "install/connect GitHub App" control itself is a
  new, separate console surface (not per-site, not per-project) — exactly where it belongs in the
  console's existing navigation is an implementation call for the session that builds it.

Explicitly **not** in this phase (see Non-goals in the eventual kickoff prompt for the full list): commit
statuses/PR comments, branch-pattern filters beyond one fixed production branch (Appwrite only added that
in a later 1.9.5 release — a real future enhancement, not v1), any git provider besides GitHub, and —
despite the shared-infrastructure design above — **actually wiring Functions up to consume any of this**.
That's its own future phase, deliberately not bundled in here; this phase only needs to make sure that
future phase is small when it comes, not build it now.
