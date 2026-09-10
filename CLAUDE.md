# Praxy — agent instructions

Self-hosted BaaS: .NET 10 API + PostgreSQL, Vite/React console, Flutter SDK. Built phase-by-phase, one
session per phase.

## Session start — do this first

1. Read `docs/handoff/`. **Your scope is whichever prompt the owner names when starting the session** —
   named-initiative prompts (`storage-phase-1-prompt.md`, `geo-nearby-phase-2-prompt.md`, …) are the
   normal shape now, and the original numbered `phase-N` sequence is complete. Don't infer the live
   prompt from "which one lacks a matching report": several completed initiatives never had a report
   written, so that scan produces false positives. Completed prompts carry a
   `> **Status: shipped.**` banner under their title — a prompt without one, that the owner has
   pointed you at, is the live one.
2. Read `docs/roadmap.md` for that phase's scope and owner-test checklist.
3. Consult `docs/research/dotnet-stack.md` before adding any package — it holds **machine-verified pins and
   API corrections**. Do not upgrade past the pins or trust memory over that file.

This session implements exactly one phase. Do not re-plan, re-litigate settled decisions, or pull work
forward from later phases.

## Fixed decisions (owner's — never reopen)

- .NET 10 backend · Vite + React console (own modern design; simple Appwrite-like layout) · Flutter SDK
  first, Next.js SDK second (added 2026-08-20, see `docs/research/nextjs-sdk.md`) · Sites, Next.js
  hosting built on that SDK groundwork, Phase 1 shipped 2026-08-21 (see
  `docs/research/praxy-sites.md`, `docs/handoff/sites-phase-1-report.md`)
- PostgreSQL only — no second datastore
- Auth: **app users** get email+password and **Google OAuth only** until the owner says otherwise;
  platform/console operators are email+password only (operator OAuth is deferred to future
  multitenancy work) — minimal options everywhere
- Features: Auth, Databases/Tables, Realtime, Messaging, Functions, Webhooks
- The owner click-tests the console at the end of every phase — **a phase without its console screens is not
  done**

## Cross-phase rules

- DDL is synchronous and transactional; long operations are explicit, queryable, cancellable jobs
- One role resolver — query compiler and realtime fan-out consume the same implementation
- Deny by default: new tables/resources are unreachable until permissions are granted
- SQL identifiers never come from request strings — metadata lookup, regex validation, quoting at emit
- Error `type` strings are public API: snake_case, unit-tested, never reworded casually
- Every limit is configurable and loud when tripped (`Retry-After`, `RateLimit-*`)
- Writes go through the outbox (`praxy.events`) from Phase 3 onward
- Datetimes are ISO-8601 UTC end-to-end; PATCH sends only changed fields

## Conventions

- Conventional commits, small and topical. Never commit `.env` or secrets.
- Integration tests: Testcontainers, `postgres:17-alpine`, shared collection fixture.
- EF Core owns only the `praxy` system schema; the tables engine is raw Npgsql. Never point EF at user
  schemas.
- Console: TypeScript pinned 5.9.x, Tailwind v4 (see the breaking-changes list in
  `docs/research/dotnet-stack.md`), TanStack Router/Query.
- Docs win over this file's brevity: `docs/roadmap.md` > `docs/research/*` > `docs/architecture.md`.

## Commands

Filled in as phases land — keep this section current.

- Self-host stack: `cd deploy && ./up.sh` — asks one question on first run (public domain, or blank
  for local/plain-HTTP), then handles the rest: installs Docker if missing, generates `.env`, and
  (domain given) brings up Caddy for automatic HTTPS + binds the plain-HTTP port to loopback-only +
  best-effort `ufw` lockdown. Console at `http://localhost:8080/console` (or `https://<domain>/console`).
- Tests: `dotnet test` (integration needs Docker for Testcontainers)
- Dev API: `dotnet run --project src/Praxy.Api` — port 5090, Scalar at `/scalar/v1`; expects local
  Postgres `praxy/praxy/praxy` on 5432 (see README dev section). Since Phase 7, also needs a reachable
  Docker daemon at runtime (not just for tests) — Functions builds/runs containers via
  `/var/run/docker.sock` by default (override with `Praxy:Functions:DockerEndpoint` or `DOCKER_HOST`).
  Self-host (`deploy/docker-compose.yml`) mounts the host socket into the api container for this —
  root-equivalent host access from inside that container; the compose file documents the tradeoff and
  the escape hatch inline. Since api itself runs in a container there, `Praxy:Functions:DockerNetwork`
  is also set (to the compose file's explicitly-named `praxy-functions` network) so function
  containers are reached by container IP on that network instead of a host-published port, which
  wouldn't be reachable from inside api's own container — see `docs/self-host.md`'s Functions section.
  Tunable via `Praxy:Functions:*` (base images, timeouts, warm pool size,
  upload size cap — see `docs/handoff/phase-7-report.md`'s Commands section for the full list).
- Sites (post-v0.1.0 initiative, Phase 1) shares that same Docker daemon requirement — build/run a
  hosted Next.js app's container via `Praxy:Sites:DockerEndpoint`, own network
  `Praxy:Sites:DockerNetwork` (`praxy-sites` in the compose file, separate from Functions'
  `praxy-functions`). A site's public hostname is `<key>.<projectId>.{Praxy:Sites:Domain}`
  (`sites.localhost` in dev — resolves to 127.0.0.1 with no setup; `dotnet run`'s port doesn't proxy
  it, so hit `http://<key>.<projectId>.sites.localhost:5090` directly, not through the console's 5173).
  Tunable via `Praxy:Sites:*` — see `docs/handoff/sites-phase-1-report.md`'s Commands section. Since
  Phase 2 (2026-08-23), every `ready`
  deployment also gets its own preview URL — a third leading label, `<deploymentId>.<key>.<projectId>.
  {Praxy:Sites:Domain}` — cold-started on first request and idle-swept by `SitePreviewSweeper`
  (`Praxy:Sites:PreviewIdleSeconds`/`PreviewSweepIntervalSeconds`, capped by
  `Praxy:Quotas:MaxPreviewContainersPerProject`); redeploys now swap containers gracefully
  (start-new/swap/stop-old, no downtime window) instead of Phase 1's brief stop-old-then-start-new.
  See `docs/handoff/sites-phase-2-report.md`'s Commands section for the full new-knob list. Since
  Phase 4 (2026-08-24), a site can also connect a GitHub repository for push-to-deploy — needs the
  instance's own GitHub App configured via `Praxy:Vcs:GitHub:AppId`/`ClientId`/`ClientSecret`/
  `PrivateKey`/`WebhookSecret` (owned by the new `Praxy.Vcs` project, sibling to `Praxy.Sites`, shared
  infrastructure for a future Functions git integration too — see `docs/handoff/sites-phase-4-report.md`)
  and a reachable `git` CLI at runtime (the deploy image installs it; `dotnet run` needs it on `PATH`
  yourself). Since 2026-08-25, a function can connect a repository the same way (`Praxy.Vcs` reused
  as-is, zero changes to it — `docs/handoff/functions-git-integration-report.md`); one GitHub App and
  webhook endpoint cover both resource types, and the same repository can be connected to a site and a
  function at once. Exact GitHub App setup steps: `docs/self-host.md`'s "Git integration" section. The
  instance must be internet-reachable for GitHub's webhook (`POST /v1/vcs/github/webhook`) to arrive at
  all — `localhost` needs a tunnel. Since 2026-08-31, every request a site's container actually serves
  is logged (method/path/status/duration, no bodies) to a new `site_requests` table, shown on the
  site's Logs tab — written asynchronously off a bounded channel (`Praxy:Sites:RequestLogChannelCapacity`)
  so logging never adds request-path latency, and retention-eligible from day one
  (`Praxy:Retention:SiteRequestsMaxAgeDays`, default 7 — much shorter than the other retention windows
  given this table's expected volume) — see `docs/handoff/sites-request-logs-report.md`.
- Storage (post-v0.1.0 initiative, Phases 1-3, 2026-09-03/04/05): file bytes live in Postgres, split across
  `praxy.file_chunks` rows behind an `IFileStore` seam (`src/Praxy.Storage`) — no second datastore,
  nothing on disk. Needs no extra runtime dependency. Tunable via `Praxy:Storage:ChunkSizeBytes`
  (512 KiB; recorded per file, so retuning never invalidates stored bytes) and
  `Praxy:Storage:DefaultBucketMaxFileSizeBytes`. The three quotas that matter are
  `Praxy:Quotas:MaxBucketsPerProject` / `MaxFileSizeBytes` / `MaxStorageBytesPerProject` —
  **Kestrel's request-body limit is derived from `MaxFileSizeBytes`**, so never configure the two
  apart. **Every stored byte lands in every backup** (`backup.sh` dumps the `praxy` schema);
  `MaxStorageBytesPerProject` is the control — see `docs/self-host.md`'s "Storage and backup size"
  and `docs/handoff/storage-phase-1-report.md`. Phase 2 (2026-09-04) added **per-file permissions**
  (`praxy.file_permissions`, gated by the bucket's `file_security` flag — **additive like row
  security: a bucket grant reaches every file and no per-file grant narrows it**), **HTTP Range**
  (through `IFileStore.OpenRead`'s offset/length, never above the seam), and **opt-in inline
  serving** (per-bucket `inline_types`, intersected with the hard-coded `InlineTypes.Safe` set that
  can never hold `text/html`/`image/svg+xml`; `nosniff` on every response regardless). No new config
  knobs — both new controls are per-bucket settings on the console's bucket Settings tab. See
  `docs/self-host.md`'s "Serving files inline" and `docs/handoff/storage-phase-2-report.md`. Phase 3
  (2026-09-05, completing the Storage sequence) added **image transforms** — `?width=`/`?height=`/
  `?format=`/`?quality=` on the download endpoint, generating a cached derivative via **SkiaSharp**
  (`SkiaSharp.NativeAssets.Linux.NoDependencies`, pinned in `docs/research/dotnet-stack.md` — no
  `apt-get` line needed, this feature renders no text). Requested dimensions **snap up to a fixed
  ladder** (64/128/256/512/1024/2048; above the top rung is a clean `400`, never a silent clamp) —
  the control that keeps the cache bounded rather than attacker-walkable. Derivatives live in
  `praxy.file_derivatives`/`file_derivative_chunks` (own chunk rows behind the same `IFileStore`
  seam, FK `ON DELETE CASCADE` off `files`) and are **never a resource of their own** — no
  permissions, resolved entirely through the source file's own `FileAccessRules` check. Tunable via
  `Praxy:Storage:MaxSourceImagePixels` (40 MP; the decoded pixel-count ceiling checked before the
  full decode, so a decompression bomb is rejected before it can allocate anything) — derivative
  bytes count against `MaxStorageBytesPerProject` like any other bytes, so backup size grows with
  them too. Phase 3 also added **replacing a file's bytes in place** (`PUT .../files/{fileId}`, same
  id) — Phase 1 had no such capability, and adding it was necessary because it is the one place
  derivative invalidation doesn't fall out of the schema on its own (re-uploading over a file id
  purges its derivatives explicitly; deleting the file itself already cascades). See
  `docs/self-host.md`'s "Image transforms" and `docs/handoff/storage-phase-3-report.md`.
- Dev console: `npm run dev --prefix console` — port 5173, proxies `/v1` to 5090
- Console prod build: `npm run build --prefix console` · EF migration: `dotnet ef migrations add <Name>`
  from `src/Praxy.Persistence` (local tool manifest pins dotnet-ef 10.0.11)
- Flutter SDK: `cd sdk/flutter && dart pub get` (native pub workspace, no melos — resolves
  `praxy_core`/`praxy_flutter`/`praxy_codegen`/`example` together) · tests:
  `dart test praxy_core praxy_codegen && flutter test praxy_flutter example` · analyze the whole
  workspace: `dart analyze .` · run the example: `flutter run --dart-define=PRAXY_ENDPOINT=...
  --dart-define=PRAXY_PROJECT_ID=<id> --dart-define=PRAXY_DATABASE_ID=<id>
  --dart-define=PRAXY_TABLE_ID=<id>` from `sdk/flutter/example` (ids are real generated ids, not
  keys — create the database/table via the console first) · codegen:
  `dart run praxy_codegen --endpoint ... --project <id> --api-key <key> --database <key>
  --table <key> --output lib/db/x_columns.dart` from `sdk/flutter/praxy_codegen` · real docs at
  `sdk/flutter/README.md` and each package's own `README.md` since Phase 9 (were unmodified
  boilerplate before then).
- Backup/restore (self-host stack, Phase 9): `cd deploy && ./backup.sh [output-dir]` and
  `./restore.sh <backup-dir>` — stop the `api` container before restoring (catalog cache goes stale
  under a raw `pg_restore`). Full runbook, config reference, and upgrade procedure:
  `docs/self-host.md`.
- Load tests (Phase 9, not part of `dotnet test`): `dotnet run --project tests/Praxy.LoadTests --
  schemas|websockets|fuzz [options]` — see `tests/Praxy.LoadTests/README.md`.
- API reference: `docs/api-reference.md` explains how the OpenAPI document ships (dev-only live at
  `/scalar/v1`/`/openapi/v1.json`; a committed, regeneratable snapshot at `docs/openapi/v1.json` for
  everyone else).
- Security review Phase 1 (2026-09-05, `docs/handoff/security-review-phase-1-report.md`): the
  container boundary. Functions got its own dedicated Docker network (`praxy-functions`, no longer
  shared with `postgres` — verified live) — **upgrading past this point needs `docker compose down`
  before `docker compose up -d --build`, not the usual in-place upgrade**, or Compose refuses to
  reuse the renamed network; see `docs/self-host.md`'s Upgrading section for the exact error and why
  it fails safe rather than partially. Every function/site container now also runs `CapDrop: ["ALL"]`,
  `SecurityOpt: ["no-new-privileges"]`, a `PidsLimit` (`Praxy:Functions:PidsLimit`/
  `Praxy:Sites:PidsLimit`, 256/512 default), and a non-root user baked into the generated Dockerfile —
  automatic, nothing to configure. Also fixed: a warm function container could carry one app user's
  `PRAXY_FUNCTION_JWT`/`PRAXY_FUNCTION_USER_ID` into a *different* user's later invocation of the same
  function (`WarmPool` never updates a reused container's env) — any invocation carrying an
  invocation-scoped credential is now never pooled. Because that means every app-user-triggered
  invocation gets its own container, how many may run at once is capped by
  `Praxy:Functions:MaxConcurrentIsolatedContainers` (16) with a
  `Praxy:Functions:IsolatedContainerWaitSeconds` (5) wait, then `503 function_capacity_exceeded` +
  `Retry-After` — without that cap a single signed-up app user can drive the host out of memory,
  since `WarmPoolSize` does not bound non-pooled containers. Follow-up review of that phase also
  closed its four accepted risks: containers now run **read-only-rootfs with size-capped tmpfs**
  (`Praxy:Functions:TmpfsSizeMb` 64 / `Praxy:Sites:TmpfsSizeMb` 256 — sites need `/app/.next/cache`
  too, for ISR and the image optimizer), which is the portable stand-in for the per-container disk
  quota Docker can't give; the **preview-container quota race** is closed by holding
  `SiteContainerRegistry.EnterProjectColdStartAsync` across the check and the registration (**always
  take that gate before the per-deployment one** — the two would otherwise deadlock); **egress stays
  allowed** but is now a documented one-line `internal: true` switch in the compose file; and every
  build log records the **resolved base-image digest**, since tag drift was invisible rather than
  wrong (digest-pinning the default would freeze security patches on a product with no auto-update).
- Security review Phase 2 (2026-09-05, `docs/handoff/security-review-phase-2-report.md`): the HTTP
  edge. Storage's image transforms now bound a derivative's *total pixel area*
  (`DimensionLadder.MaxOutputPixels`, `TopRung²×2`) — not either axis independently, which was tried
  first and rejects every non-square photo at the top rung — so a `?width=`/`?height=`-only request's
  *derived* axis (computed from the source's own aspect ratio, previously unbounded) can no longer
  blow up. A real, honestly-encoded extreme-aspect-ratio image (no crafted file needed) could
  otherwise derive an unbounded output dimension, crashing the request (an unchecked `null` from
  SkiaSharp's encoder) or silently producing a multi-hundred-megabyte allocation and derivative for
  formats that don't crash. Also fixed: one oversized HTTP method or path on a proxied site request silently dropped every other
  request's log row batched in the same flush (`site_requests`' column widths rejected the row,
  aborting the whole implicit transaction) — `SiteRequestLogWorker` now truncates before persisting,
  never rejects. No new configuration from either fix. `ByteRanges`, `SiteProxyMiddleware`'s
  `X-Forwarded-*` handling, and `SiteHostPattern`/`_ask-tls` were all independently re-verified sound
  against a running instance, not just read.
- Security review Phase 3 (2026-09-06, `docs/handoff/security-review-phase-3-report.md`, completing
  the initiative): authorization and project isolation. No new configuration; one new error type
  (`vcs_repository_already_connected`). The one to know about:
  **`SiteContainerRegistry.TryGet` now requires the owning site id** — it is a single process-wide
  dictionary holding every project's containers, and the preview branch checked it *before* the
  ownership query (which only ran on a miss), so any deployment id learned from anywhere reached that
  project's live container as a full proxy pass-through. The id is mandatory rather than
  defence-in-depth: the unsafe call can no longer be written. Also: a `BypassRowPermissions` API key
  now only reaches the realtime firehose resources its own scopes cover
  (`Connection.AllowedBypassResources`, null = operator console = unrestricted, as before);
  `PRAXY_FUNCTION_JWT`'s lifetime is the invocation's own timeout plus a few seconds rather than a
  flat 15 minutes; and **a repository may not be connected to two different projects** — one push
  would redeploy both, since `HandleGitPushAsync` matches on repository name alone. That last guard is
  connect-time only: pairs that already exist are untouched, and `docs/handoff/security-review-phase-3-report.md`
  carries the detection query.
- Organizations Phase 1 (2026-09-06, `docs/handoff/organizations-phase-1-report.md`): the console's
  organization lifecycle — create, rename, delete-when-empty, and multi-org switching. **No membership
  changes**: `OrganizationMember.Role` is still written at signup and never read, so every member is
  effectively an owner until Phase 2 enables it deliberately. Delete has no `force` — an org holding
  projects is a `409`, and an operator always keeps at least one. New knob:
  `Praxy:Quotas:MaxOrganizationsPerOperator` (10) — **the only quota scoped to an operator rather than
  an organization**, because organizations are what every other quota is scoped *to*; without it an
  operator at their `MaxProjects` ceiling could create another org for a fresh allowance, making every
  per-org limit advisory. `POST /v1/console/projects` now takes an optional `organizationId` and
  infers one only while unambiguous, failing loudly rather than silently picking the oldest membership
  once several exist. Design and the remaining phases (members/roles, then operator OAuth):
  `docs/research/organizations.md`.
- Organizations Phase 2 (2026-09-06, `docs/handoff/organizations-phase-2-report.md`): members and
  roles — `OrganizationMember.Role` is now enforced, not just stored. Invite by email (an unconfirmed
  row on `OrganizationMember` itself, `SecretHash`/`InvitedAt`/`Confirmed` mirroring Teams' invite
  shape as a pattern, not shared code — Teams' own `Membership*`/`TeamInvalidSecret` error types stay
  Teams-only), accept (public, no session — a wrong secret and a nonexistent invite return the same
  `organization_invite_invalid`), remove, and change role. `owner` manages the org itself
  (rename/delete/invite/remove/change-roles) and `member` uses the projects inside it — one check,
  `OrganizationsService.RequireOwnerAsync`, gates every owner-only action; reading membership stays
  additive on top of Phase 1's membership-only joins, never a role check. The last owner of an org can
  never be removed or demoted (`organization_last_owner`, distinct from Phase 1's
  `organization_last_one`, which is an *operator's* last organization, not an *org's* last owner).
  `EnsureOrganizationQuotaAsync` (Phase 1's knob) is now owner-scoped, per that phase's own note: being
  invited into someone else's organization no longer spends this operator's own creation allowance.
  No new configuration — an invite email goes through the existing instance-wide `IEmailSender`
  singleton, not the per-project template system, since organization membership isn't scoped to any
  developer project. **Leaving your own last organization is allowed and, unlike deleting your last
  organization, has no guard against it** — deliberate, matching the phase's own owner-test script,
  but it means an operator can reach zero organizations for the first time; the console's
  `HomeRedirect` now offers a create-organization form in that state instead of an unrecoverable error
  screen. Design and the last phase (operator OAuth): `docs/research/organizations.md`.
- Organizations Phase 2 (2026-09-06, `docs/handoff/organizations-phase-2-report.md`): members and
  roles. `OrganizationMember.Role` is **finally read** — `owner` manages the org (rename, delete,
  invite, change roles, remove others), `member` uses its projects — through the single
  `OrganizationsService.RequireOwnerAsync`; a future owner-only action calls that or it hasn't
  adopted the check. Invites are a pending row on the same table (`Confirmed`/`SecretHash`/
  `InvitedAt`), **so every access-control query now also filters `Confirmed`** — six of them; an
  unconfirmed invite must never grant what a membership grants, and `Confirmed` is orthogonal to
  `Role`, not a tightening of it. The migration backfills `confirmed = true`, which is the only
  correct value for a row predating invites. New knob from the phase's review:
  `Praxy:Quotas:MaxMembersPerOrganization` (25, per-org overridable) — seats, counting pending
  invites, since an unaccepted one has already created a console account and sent mail; the invite
  route's `auth-email` rate limit bounds outbound mail per window, this bounds the total. Also note
  **leaving your only organization is now reachable** (deliberate — self-removal is blocked only by
  the last-owner rule), so zero-organization is a real state the console handles rather than an error.
- Organizations Phase 3 (2026-09-06) shipped **operator Google sign-in**, and it was **removed
  again on 2026-09-08** — see `docs/handoff/console-oauth-removal-report.md`. The reasoning, kept
  because it will come up again: console SSO is nearly free to offer in a *managed* service (one
  OAuth client, configured centrally, every tenant benefits) and genuinely annoying to offer
  *per self-hosted instance* — the redirect URI is per-domain, so every single installation had to
  register its own Google client before the feature did anything. That is the asymmetry behind
  Appwrite reserving console OAuth for their Cloud tier, and it means the value lands in the
  managed offering rather than in self-host, where email+password is what people actually want to
  start with. Removing it also retired the four `console_oauth_*` error types, the
  `googleOAuthEnabled` capability flag, and `Praxy:ConsoleAuth:Google:*`. **The Organizations
  sequence itself is complete and unaffected** — orgs, members, roles and invites all stand; only
  the Google door onto them is gone, and the password door was always the primary one. If console
  SSO is ever rebuilt for managed hosting, the removed implementation and a half-finished security
  review of it are in git history (branch `oauth-security-review-wip`, and PRs #75/#79).
- Console/API contract (2026-09-07, `docs/handoff/console-api-contract-report.md`, one phase,
  sequence complete): `console/src/api/types.ts` (897 hand-written lines) is now mostly a thin
  re-export layer over `console/src/api/generated/schema.ts` — committed, generated from
  `docs/openapi/v1.json` by `openapi-typescript`, never hand-edited. `npm run generate:api --prefix
  console` regenerates it; `npm run check:api-types --prefix console` regenerates into the working
  tree and `git diff --exit-code`s it against the committed copy — the CI gate, mirroring
  `OpenApiDocumentTests`' own snapshot check, and also why the console CI job's path filter now
  watches `docs/openapi/v1.json` in addition to `console/**` (a backend-only PR can make this
  stale). `npm run build` never runs codegen, so the console still builds from a clean checkout with
  no database and no network. Getting there needed two real backend fixes, both zero-wire-shape-
  change: a new `OpenApiWireNullability` document transformer (`src/Praxy.Api/Infrastructure`)
  correcting the OpenAPI document's nullability to match `Program.cs`'s `WhenWritingNull` (the
  document said most fields were "present and possibly null"; the wire truth is "present-or-absent,
  never null" — verified by reverting the fix and confirming a generator would reproduce PR #55's
  exact bug), and `OrganizationMemberResponse.UserId` promoted `string` → `Guid` so the schema's
  `format: "uuid"` correctly flags both of the API's two dashed-`Guid` ids (previously only
  `ConsoleAccount.Id`) for the new `console/src/api/ids.ts` `WireId`/`GuidId` branded types to pick
  up — closing the Organizations Phase 2 id-encoding-mismatch bug class at the type level. Also
  fixed: two webhook endpoints' `.Produces<>()` annotations documented the wrong response shape
  entirely (anonymous `{webhook, secret}`/`{delivery, payload, attempts}` objects declared as bare
  `WebhookResponse`/`WebhookDeliveryResponse`) — replaced with real named DTOs,
  `CreatedWebhookResponse`/`WebhookDeliveryDetailResponse`. One disclosed gap, not fixed here (a
  real wire-shape question — a C# `enum`'s default JSON representation is its ordinal number, not
  the strings already on the wire): several DTOs model an enum-like field as bare `string`, so
  `docs/openapi/v1.json` can't express the closed set of values, and `types.ts` still hand-narrows
  those few fields (`ColumnType`, every `*Status`, etc.), documented inline everywhere it happens.
- Site container reclamation (2026-09-08): site containers used to leak, one per abandoned
  redeploy or killed test run, with nothing ever reclaiming them — found as 17 running on a dev
  machine and 2 on the production droplet, the oldest up two weeks. `SitePreviewSweeper` now does a
  **startup reclaim** (mirroring `FunctionPoolSweeper`'s): remove any `praxy.site=true` container
  that **no `site_deployments` row references** and that predates this process. Deliberately not the
  blanket `praxy.site=true` sweep `Praxy.Functions.DockerExecutor.RemoveOrphanedContainersAsync`'s
  doc rejected — that would take every hosted site down on restart; keying on the DB reference keeps
  every live container (the proxy and `SiteReconciler` both resolve containers only through that
  column) and the age guard keeps the one `SiteReconciler` may be starting concurrently. Same
  **one-api-process-per-Docker-daemon** assumption the Functions sweep already documents: running
  `dotnet test` on a machine that also runs a dev instance reclaims that instance's site containers,
  which `SiteReconciler` then restarts — the Functions sweep already clears its warm pool the same
  way. No new configuration. The leak's *source* is fixed too: `SitesService.ActivateAsync` cleared
  the outgoing deployment's `container_id` unconditionally but stopped the container only when the
  in-memory registry happened to hold it, so **the first redeploy after any restart abandoned one** —
  it now reads the recorded id before erasing it, and the startup reclaim is the second line of
  defence for the crash case rather than the only one for the ordinary case. **Known residual**: a
  full `dotnet test` still leaves about one site container behind, roughly 15 minutes in. It does not
  reproduce running `SiteTests` alone, all eight `Site*` classes together, or `FunctionGitDeploymentTests`
  (the only other class that touches sites and the only one with no cleanup override), and the
  `SiteReconciler`-restarts-what-teardown-stopped theory is wrong — those classes set
  `ReconcileIntervalSeconds` to 3600 precisely to park it, and its only pass is at startup. The
  startup reclaim takes it on the next run, so this is bounded and self-clearing rather than the
  unbounded growth it replaced; don't chase it with a speculative fix. Also: catalog migrations no longer run under the data plane's
  `statement_timeout` (`CatalogMigrator` sets `statement_timeout = 0` for its own session, the way
  `SchemaJobRunner` already raises it for long index builds) — a migration is not request work, and
  being cancelled by a request-shaped budget means the instance fails to *start*, not that a request
  degrades. `StatementTimeoutTests` asserts both that the opt-out works and that Npgsql's pool reset
  keeps it from leaking to the next borrower, since a leak's only symptom is a timeout that stops
  firing.
- Deployment image reclamation (2026-09-09): every site/function build produced a Docker image and
  **nothing ever removed one** — found on the production droplet as every image ever built still
  present, oldest two weeks old, alongside a build cache that had previously reached 42 GB of a
  77 GB disk. Two different defects with two different owners, and worth keeping apart: the
  **images** are the product's to bound (now `DeploymentImageSweeper`, in `Praxy.Api/Infrastructure`,
  on the retention interval), the **build cache** is the operator's — it is generated almost entirely
  by `docker compose up -d --build` building `api` itself (verified: on production every single cache
  entry dated from the last deploy, none from a site or function build), and `Docker.DotNet.Enhanced`
  4.3.3 exposes **no build-cache prune API at all** (only the read-only
  `SystemDataUsageInfoResponse.BuildCacheUsage`; there is no raw-request escape hatch on
  `DockerClient` either — checked by reflection), so it is a documented
  `docker builder prune --keep-storage` line in `docs/self-host.md`, not code. New knobs:
  `Praxy:Sites:KeepDeploymentImages` / `Praxy:Functions:KeepDeploymentImages` (5 each). **Bounded
  retention, not a prune** — an old deployment image is exactly what the console's Activate button
  rolls back to, so the policy keeps the N most recent `ready` builds *per resource* plus the active
  deployment whatever its age (roll back and stay there and the running image is never a candidate).
  The rule that makes this safe without an age guard: the image tag *contains the deployment id*, and
  a row exists from `queued` onward, so "no row at all" cannot mean "build in flight" — the inverse
  of `SitePreviewSweeper`'s container reclaim, which does need an age guard because a container id is
  written *after* the container exists. Don't "fix" that asymmetry. Reclaiming nulls `image_tag`, so
  a reclaimed deployment keeps its build log and commit but can no longer be activated
  (`site_deployment_image_reclaimed` / `function_deployment_image_reclaimed`, two new error types) and
  — for a site — loses its preview URL, since previews cold-start from that same image. Also fixed
  here: **`FunctionsService.ActivateAsync` never checked `ImageTag` at all** (Sites' did), so
  activating an imageless deployment used to succeed and repoint `ActiveDeploymentId` at something
  that could only fail later at invoke time. **Known residual**: a process killed between a successful
  build and the row update recording its `ImageTag` leaves an image on a row stuck in `building` —
  matched by neither rule, bounded by crash frequency rather than deploy volume; reclaiming it would
  need exactly the age guard the two rules are shaped to avoid.
- SDK generation (2026-09-09/10, `docs/research/sdk-generation.md`): **Phase 1** gave all 290
  operations a stable `operationId` derived from the handler `MethodInfo` (`OpenApiOperationIds`) —
  from the *method*, not the route, so renaming a route never renames a public SDK method. **Phase 2**
  added API-key auth to `praxy_core` and `sdk/flutter/praxy_sdk_gen`, which generates
  `praxy_core/lib/src/services/generated/users_service.dart` from `docs/openapi/v1.json`.
  Regenerate with `dart run praxy_sdk_gen` from `sdk/flutter/`; CI regenerates and
  `git diff --exit-code`s, mirroring the console's `check:api-types` — and the Flutter path filter now
  watches `docs/openapi/v1.json` for the same reason the console's does. The CI step runs
  `git add --intent-to-add` first: `git diff` doesn't report untracked files, so a newly registered
  service whose output was never committed would otherwise pass while missing from the repo.
  **The Dart SDK is one dual-mode package** (owner's call), not Appwrite's `appwrite`/`node-appwrite`
  split — so the guard is that `PraxyFlutter` offers no way to pass an `apiKey`, and a server client
  **never reads the session store at all** rather than preferring the key, or a background job would
  inherit whatever session happened to be persisted. **Generate the mechanical surface only**: a
  schema in neither `modelTypes` nor `generateModels` is a hard error, never a guessed type, because
  `praxy_core` already hand-writes `AppUser`/`AppSession`/`SessionList` and a generated duplicate
  would silently diverge. **The live landmine for Phase 3: `docs/openapi/v1.json` documents no query
  parameters at all** — only path ones. 37 reads across 16 endpoint files take `limit`/`offset`/
  `search`/`expand`/`queries` from `HttpContext`, which .NET's OpenAPI generation cannot see, so
  `users.list` is documented as taking nothing and the generated method cannot paginate. That is
  declared in the generator's `documentGaps` and lands in the generated doc comment rather than being
  emitted silently; a note that outlives its operation is a hard error. Operation summaries are still
  1 of 290 (XML docs reached schemas, not operations), so generated methods have no prose docs yet.

## Session end — handoff protocol

Before finishing a phase: tests green, `git status` clean, run the owner-test checklist yourself, then write
`docs/handoff/phase-N-report.md` and `docs/handoff/phase-(N+1)-prompt.md`, update the Commands section above
if it changed, and print the next prompt for the owner. Full protocol: bottom of `docs/roadmap.md`.
