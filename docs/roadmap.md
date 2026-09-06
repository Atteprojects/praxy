# Praxy — Roadmap

Phased build plan. **Each phase ends with a console the owner can click through** — that walkthrough is the
acceptance gate. Each phase is implemented in a fresh session; the handoff protocol is at the bottom.

Fixed choices (owner's): .NET backend · Vite console · Flutter SDK first · features = Auth, Databases/Tables,
Realtime, Messaging, Functions, Webhooks · minimal options to start (Google is the only OAuth provider
for app users; email+password and Google OAuth are the only app-user sign-in methods until further
notice — platform/console operators are email+password only, with operator OAuth deferred to future
multitenancy work).

Read before implementing anything: [architecture.md](architecture.md), then the relevant
[research/](research/) files. Version pins live in [research/dotnet-stack.md](research/dotnet-stack.md) —
do not "upgrade" past them without checking.

---

## Phase 0 — Foundation & console shell

**Backend**
- Solution: `Praxy.Api`, `Praxy.Core`, `Praxy.Persistence`, `Praxy.Auth`, `Praxy.Tables`, `Praxy.Realtime`,
  `Praxy.Events` + `Praxy.Tests.Unit` / `Praxy.Tests.Integration` (Testcontainers, `postgres:17-alpine`,
  shared collection fixture).
- `deploy/docker-compose.yml`: postgres + api, persistent volume, secrets generated into `.env` on first run.
- EF Core catalog v1 (schema `praxy`): `organizations`, `organization_members`, `projects`, `platforms`,
  `api_keys`, `users`, `sessions`, `schema_jobs`, `events` (outbox — created now, consumed Phase 6),
  `audit_log`. Users/sessions are project-scoped; **the console is a reserved project** (`console`) whose
  users are the operators.
- Startup migration under session-level `pg_advisory_lock` (dotnet-stack.md has the verified pattern).
- Middleware: request id (echoed in responses + errors), error envelope `{message, code, type, version,
  requestId, fields?}`, Serilog two-stage bootstrap, OpenAPI + Scalar UI **dev-only**.
- **Instance claim:** first console account claims the instance; signup closes after (API-enforced AND
  hidden in UI). If `PRAXY_PUBLIC_URL` is set, require the setup token printed to container logs.
- **The `console` project guard:** data-plane endpoints and API keys refuse project `console`. Write the
  integration test for this in Phase 0, not a comment.
- First org auto-created silently ("Personal") on claim. Orgs are modeled fully, hidden in UI (no switcher
  until multi-org exists).
- `GET /v1/console/capabilities` — server-driven feature flags the console gates screens on.

**Console** — Vite + React 19 + TS 5.9 (pinned) + Tailwind v4 + TanStack Router/Query, served by the API
container at the root path. Screens: claim/login, chrome-less create-project card, project list, project
overview with "waiting for first ping" state, ⌘K palette shell (`g`-chord navigation). Own modern UI —
simple like Appwrite's layout (sidebar + tabs + tables) but our design language; see
[research/console-design.md](research/console-design.md).

**Owner test:** `docker compose up` → claim → sign out/in → create project → see it listed → API docs page
loads in dev → `docker compose down && up` → still signed in, data intact.

## Phase 1 — Auth

**Backend**
- App users: email+password signup (Argon2id via Konscious behind an `IPasswordHasher` seam; OWASP baseline
  m=19456/t=2/p=1; PHC string storage), login, **session auto-created on signup**.
- Opaque sessions: 32-byte CSPRNG secret, SHA-256 at rest, constant-time compare, `praxy_session_<projectId>`
  cookie (httpOnly/Secure/Lax) or `X-Praxy-Session`. Per-user session cap 10, oldest evicted. 60s in-memory
  session cache invalidated via event bus.
- **Google OAuth only** — token flow (callback carries userId + 60s-JWT-wrapped secret → exchanged at
  `POST /account/sessions/token`) + PKCE. Provider abstraction so Google etc. slot in later without API changes.
- Email verification + password recovery via SMTP sender (config: host/port/user/pass/from). **Redirect URLs
  validated against the platform allowlist** — security-critical, see architecture.md threat model.
- Teams + memberships + invitation flow (Appwrite semantics: client call emails invite, server call adds
  directly, acceptance auto-creates session).
- Role resolution: one resolver producing the caller's `string[]` roles (`any`, `users`, `users/verified`,
  `user:<id>`, `user:<id>/verified`, `team:<id>`, `team:<id>/<role>`, `member:<id>`, `label:<x>`), cached on
  request context. This single implementation later feeds both query compiler and realtime fan-out.
- API keys: hashed at rest, scoped, `last_used_at`. Platform allowlist enforced as CORS origin check.
- Rate limiting: built-in limiter, `RejectionStatusCode=429` (default is 503!), `Retry-After` emitted, tight
  buckets on auth endpoints, partition on project/key before IP.
- Session deletion publishes `sessions.delete` (Phase 4 uses it to kill live sockets; cache honors it now).

**Console** — users table + create user, user detail (overview / sessions / memberships tabs), teams +
members, auth settings (method toggles, Google credentials, session limits, password policy), API keys
(create/reveal-once/revoke), platforms screen with add-platform flow.

**Owner test:** create user in console → sign in via curl/Scalar as that user → session appears on user
detail → revoke it → API returns 401 → invite user to team → accept → `team:<id>` shows in resolved roles
(debug endpoint) → API key with wrong scope gets 401 → 11th session evicts the 1st.

## Phase 2 — Schema engine

**Backend**
- Databases: `POST /v1/databases` → metadata + `CREATE SCHEMA px_<ulid>` in one transaction.
- Tables/columns: types `string(size)`, `integer`, `float`, `boolean`, `datetime`, `email`, `url`, `ip`,
  `enum` (+ `array` variants). **Relationships deferred to v1.1** (decided). Physical-naming scheme per
  architecture.md §4.1 (generated identifiers, regex validation + `QuoteIdentifier` at emit — instance
  method, see dotnet-stack.md). Row byte budget computed at definition time, rejected with named offenders.
- Sync DDL in-transaction: create/drop table, add/drop column → status `available` immediately. Async via
  `schema_jobs` (`FOR UPDATE SKIP LOCKED`, serialized per database): `CREATE INDEX CONCURRENTLY`, type
  changes → status `processing` → `available`/`failed` + captured error. `lock_timeout=5s` +
  `statement_timeout` on every DDL connection. Destructive changes need `force=true`.
- Indexes: `key`, `unique`, `fulltext` (generated tsvector + GIN). Job rows expose elapsed time; jobs are
  cancellable and failed jobs retryable — **this is the beat-Appwrite feature, their #1 complaint cluster.**
- Table permissions storage + `rowSecurity` flag (default **off**; tables default **deny-all** with a console
  banner saying so).
- Row `_id` = UUIDv7 (`Guid.CreateVersion7()` — sorts correctly, validated).

**Console** — databases list, table sub-sidebar, the `<DataGrid />` primitive (TanStack Table + Virtual —
built once, reused for columns/indexes/rows), Columns screen with type icons + status badges + elapsed/cancel
/retry on processing, column create/edit side sheet with "create more" toggle, Indexes screen + create sheet
(reachable from the column grid's "Indexed" checkbox), table settings: permission matrix with **presets**
(Public read / Owner only / Team access), row-security switch, danger zone.

**Owner test:** create database → create table → add one column of every type → add a unique index and watch
`processing → available` with elapsed time → add a fulltext index → try dropping a required column without
`force` (clear error) → rename a column (instant, metadata-only) → set permissions via preset → delete table
(typed-name confirm).

## Phase 3 — Data plane

**Backend**
- Row CRUD. **PATCH is genuinely partial.** Create accepts client `rowId` or `unique()`.
- Query DSL per [research/appwrite-api.md](research/appwrite-api.md): JSON-per-query wire format, the 24 v1
  methods, caps (100 queries × 4096 chars, depth 3, limit ≤100 default 25). Compiler: AST → validate against
  column metadata → parameterized SQL. Identifiers only ever from metadata lookup.
- Keyset pagination default (`cursorAfter` → `(sort, _id) >` tuple compare); offset capped.
- Permission filtering: table-level check, plus `__perms` side-table EXISTS join when rowSecurity on.
  `search` without a fulltext index is rejected, never silently ILIKE'd.
- **Catalog cache**: in-memory per project, invalidated by schema-change events. Build it now — every row
  request otherwise costs 5 catalog round trips.
- Writes emit events to the outbox + in-process bus (payload includes row permission roles, computed
  pre-commit — this is what Phase 4 fans out and what makes DELETE events authorizable).

**Console** — row browser on `<DataGrid />`: virtualized infinite scroll, inline cell editing (only dirty
fields sent; datetimes ISO-8601 UTC end-to-end — Appwrite's console corrupts them on every save), NULL vs
FALSE visually distinct, filters popover → chips → `?query=` URL, sort, row side sheet with prev/next +
**raw JSON view** + copy-as-JSON, per-row permission sheet (no Create column at row level), bulk select +
delete, ghost-sheet empty states with real headers.

**Owner test:** insert rows → filter/sort → paginate past one page → edit inline and confirm only the edited
field changed (`_updated_at` moves, others untouched) → flip rowSecurity and watch a non-owner session's
reads change → cursor-paginate via API → exceed a query cap and get a clear 400 with `fields`.

## Phase 4 — Realtime

**Backend** — message-mode protocol per appwrite-api.md: `connected`/`subscribe`(batched, client ids)/
`unsubscribe`/`ping`/`event` envelope with `subscriptions[]` matched-ids. Ticket endpoint for non-browser
clients (single-use, 60s). Roles resolved **once at connect**, indexed project→role→channel→connection;
fan-out is hash lookups against the event's precomputed roles. Membership/session events set a
revalidation flag; `sessions.delete` **closes that session's sockets**. Bounded per-connection channel
(256, single writer) → close `1013` on overflow. Early subscribe is queued, never `1008`-closed. API keys
may subscribe (scoped). 30s ping, drop on missed pong. Connection quota per project.

**Console** — realtime inspector: live event tail with channel filter + payload viewer; live connection
count on project overview.

**Owner test:** two browser tabs — edit a row in one, watch the event in the other's inspector < 1s →
subscribe to a table the session can't read → no events → revoke the session → socket closes → row-level
channel delivers only that row.

## Phase 5 — Flutter SDK

`sdk/flutter/`: `praxy_core` (pure Dart) + `praxy_flutter` + example app. The ~20-method surface, sealed
exceptions, `TableRef<T>`/`RowCodec<T>` typed rows, real `Stream` realtime with `liveList`, secure-storage
sessions, Google OAuth via flutter_web_auth_2 (Android intent filter documented; iOS needs nothing — see
[research/flutter-sdk.md](research/flutter-sdk.md), which is the full spec).
**Owner test:** run the example app against local Praxy — sign up, Google sign-in, CRUD rows, watch a
realtime update arrive from the console, kill/restart app → still signed in.

## Phase 6 — Webhooks

Outbox consumer (at-least-once, `SKIP LOCKED`), per-project webhook subscriptions on the event grammar with
`*` wildcards, HMAC-SHA256 signature (`v1=<hex>` over `timestamp.body`, separate timestamp header), retries
with exponential backoff + jitter, delivery log with per-attempt status/latency/response code, disable-after-N
-failures with console warning. 15s timeout, no redirects followed cross-origin, SSRF guard (deny
private/loopback ranges unless self-host config allows).
**Console:** webhook list/create (URL + event picker + signing secret reveal-once), delivery log with payload
+ redelivery button.
**Owner test:** register hook against a local echo server → create a row → delivery logged, signature
verifies → point at a dead URL → watch retries/backoff → redeliver from console.

## Phase 7 — Functions

Docker executor on the open-runtimes contract (HTTP server in container + shared-secret header; build phase /
start phase split). `Docker.DotNet.Enhanced` (not stale `Docker.DotNet`). Deployments (tar upload → build →
activate), warm pool, sync executions 30s hard cap, **async executions store their output**, event triggers
on the same grammar, cron schedules, scoped user JWT injected into invocations, env vars encrypted at rest.
Dart runtime first (dogfoods the SDK), Node second.
**Console:** function list, deployments + build logs, executions + logs, settings (vars, triggers, schedule,
timeout). **Owner test:** deploy from console → invoke sync → see logs → trigger via row create → async
execution shows stored output → failed build shows its log.

## Phase 8 — Messaging

Email only initially (owner's minimal-options rule): SMTP provider config (reuses Phase 1 sender), topics,
targets (user email), subscribers, send-to-topic + send-to-users, per-message delivery status, templates for
the auth emails moved here. Providers/SMS/push are additive later — model `providers` generically now.
**Console:** messages list + composer, topics + subscribers, provider settings.
**Owner test:** create topic → subscribe two users → compose + send → delivery status per target → auth
verification email still renders with the project template.

## Phase 9 — Hardening → v0.1.0

Org-level quotas (`limits jsonb`) enforced + surfaced; audit log (admin actions distinguished from user
actions); backup/restore documented + tested per schema (`pg_dump -n px_<id>`); upgrade test from the
previous tag against real data (release gate); load tests — 1k schemas, 10k WebSocket connections, query
compiler fuzzing; security pass — the threat-model table in architecture.md verified item by item + the
`console`-project guard + SSRF + rate limits; error-type lint (`^[a-z0-9_]+$`); docs: self-host guide,
API reference from OpenAPI, SDK readme. Tag v0.1.0.

---

## Sites (post-v0.1.0 initiative)

Not a numbered phase — like the other post-v0.1.0 work (`docs/handoff/*-prompt.md` files without a phase
number), this is a fresh initiative with its own internal phase breakdown. Full design:
[research/praxy-sites.md](research/praxy-sites.md). Owner ask: research how Appwrite implemented Sites and
add the equivalent to Praxy, starting with Next.js.

**Sites Phase 1 — shipped 2026-08-21** (report: `docs/handoff/sites-phase-1-report.md`; PR #7, plus
follow-up fixes PR #8 for Caddy's on-demand-TLS wildcard depth and PR #9/#10 for console polish and
sites-card preview screenshots). Next.js hosting only. A new `Site` resource under Project (console tar
upload → Docker multi-stage build requiring `next.config.js`'s `output: "standalone"` → a long-lived
container per active deployment, crash-restarted by Docker, not idle-swept like Functions). Public
reachability via **subdomain-per-site** (`<key>.<projectId>.sites.<domain>`), served by
`SiteProxyMiddleware` (YARP direct forwarding, not Functions' JSON-envelope invoke model — that has no
streaming/binary support, the wrong shape for a web app), fronted by Caddy **on-demand TLS** with a strict
allow-list `/v1/sites/_ask-tls` endpoint. Env vars injected at both build and runtime. Separate
`praxy-sites` Docker network. `QuotaService` gained a `sites` dimension. Live in `src/Praxy.Sites/`,
`SiteEndpoints.cs`, and the console's `SitesPage.tsx`/`SiteDeploymentsPage.tsx`/`SiteSettingsPage.tsx`.
Full architecture, data model, and deviations found while building it: see
[research/praxy-sites.md](research/praxy-sites.md) and the phase-1 report above.

**Sites Phase 2 — preview URLs + graceful redeploy — shipped 2026-08-23** (report:
`docs/handoff/sites-phase-2-report.md`). Every `ready` deployment now gets its own reachable preview URL
(`<deploymentId>.<key>.<projectId>.sites.<domain>` — a third leading label), cold-started on first request
and idle-swept once nobody's hit it in a while — never the always-on production one.
`SiteContainerRegistry` moved from keyed-by-site (one entry, active deployment only) to keyed-by-deployment
to support that. Redeploys now swap containers gracefully (start-new fully through the readiness probe,
swap the registry pointer, then stop-old) instead of Phase 1's brief stop-old-then-start-new gap. New
`Praxy:Quotas:MaxPreviewContainersPerProject` caps concurrent previews per project. Caddy needed a third
site block (`*.*.*.{$PRAXY_SITES_DOMAIN}`) for the extra wildcard label, verified against real Caddy the
same way Phase 1's own fix was — see `research/dotnet-stack.md`'s Caddy section. No new DNS record needed
(the existing wildcard already covers any depth). Full design: `research/praxy-sites.md`'s "Phase 2"
section.

**Sites Phase 3 — custom domains — shipped 2026-08-24** (report:
`docs/handoff/sites-phase-3-report.md`). A site owner can point their own domain at a site's active
deployment via a new `site_domains` table (globally unique hostname, `pending`/`verified` status) — a
new `SiteCustomDomainLookup` exact-match DB lookup sits alongside `SiteHostPattern`'s pure-parse
`TryParse`, consumed by both `SiteProxyMiddleware` and `_ask-tls`. On-demand TLS (Phase 1) generalizes to
arbitrary hostnames almost for free via a fourth Caddy site block, a bare `https:// { tls { on_demand }
}` catch-all — verified live against real Caddy for automation-policy shadowing, the same discipline
Phase 1 and 2's own Caddy fixes were held to. A domain flips `pending → verified` on the first
successfully proxied request through it, not inside `_ask-tls` (which only permits an ACME attempt, not
proof it succeeded). Full design: `research/praxy-sites.md`'s "Phase 3" section.

**Sites Phase 4 — git integration — shipped 2026-08-24** (kickoff: `docs/handoff/sites-phase-4-prompt.md`;
report: `docs/handoff/sites-phase-4-report.md`). Push to a site's production branch builds and
auto-activates; push to any other branch builds a deployment and leaves it on its existing Phase 2 preview
URL — no new serving infrastructure needed for that half, exactly as designed. Real design in
`research/praxy-sites.md`'s "Phase 4" section, including two scope-*cutting* findings from re-checking
Appwrite's actual deploy-from-git docs (no commit statuses/PR comments, no build-command auto-detection)
that made this phase smaller than the original sketch assumed. **The GitHub App/webhook/installation layer
is a new shared project, `Praxy.Vcs`** (`Praxy.Core`/`Praxy.Persistence` only, no reference to
`Praxy.Sites`) **— not Sites-specific**, raised by the owner before this phase started:
`FunctionsService.CreateDeploymentAsync` is nearly identical in shape to `SitesService`'s own, so a future
Functions git-integration phase can be a small addition to `Praxy.Vcs`'s consumers, not a rebuild of the
GitHub App/token/signature layer — that future phase is explicitly not started here, only designed for.
GitHub App JWT signing, webhook HMAC verification, and the commit clone all needed zero new NuGet
packages (hand-rolled on the BCL / shelled out to the system `git` CLI) — see
`research/dotnet-stack.md`'s own section. Self-hosted owner configures their own GitHub App, same as
Appwrite requires (exact steps: `docs/self-host.md`'s "Git integration" section) — the instance must be
internet-reachable for GitHub's webhooks to arrive, so the real owner-test targets `praxycore.dev`, not
local dev. This closes the four-phase Sites sequence the owner committed to; framework presets beyond
Next.js remain deferred (below).

**Functions git integration — shipped 2026-08-25** (kickoff:
`docs/handoff/functions-git-integration-prompt.md`; report:
`docs/handoff/functions-git-integration-report.md`). The small addition to `Praxy.Vcs`'s consumers Sites
Phase 4 predicted: push-to-deploy for Functions, reusing the GitHub App/webhook/token layer entirely
as-is (zero changes to `Praxy.Vcs` itself). Push to a function's production branch builds and
auto-activates; push to any other branch builds a deployment that finishes `ready` without activating —
Functions has no preview-URL infrastructure to land on the way Sites' deployments do, so an unactivated
git build just sits reachable via the console's existing explicit Activate action, the same state an
unactivated upload could already reach. `VcsEndpoints.Webhook` now dispatches one parsed push event to
both `SitesService.HandleGitPushAsync` and `FunctionsService.HandleGitPushAsync` unconditionally, so the
same repository can be connected to a site and a function at once and a single push deploys both
independently. Self-hosted owner reuses the GitHub App Sites Phase 4 already walked them through
creating — no second App, same five `Praxy:Vcs:GitHub:*` config values (`docs/self-host.md`'s "Git
integration" section now documents both resource types).

**Sites build caching — shipped 2026-08-30** (kickoff: `docs/handoff/sites-build-caching-prompt.md`;
report: `docs/handoff/sites-build-caching-report.md`). Found during an informal self-host-vs-Appwrite
comparison: Appwrite's Sites builds reuse `npm install` between deployments of the same site; Praxy's
built from scratch every time. Root cause was a Dockerfile-ordering bug, not a Docker-daemon limitation —
`SiteRuntimeTemplates.Dockerfile(...)` did `COPY . .` before `RUN npm install`, so any app-code change
(i.e. every deployment) invalidated the install layer even when `package.json`/`package-lock.json`
hadn't changed. Reordered to copy+install dependencies before the full source copy — Docker's local
layer cache (already enabled, no plumbing change) now skips `npm install` whenever the lockfile is
unchanged. Verified against a real Docker daemon, not just by reading the diff: the classic (non-
BuildKit) builder's `Step N/M : RUN npm install` line is immediately followed by `---> Using cache` on a
redeploy that only touches app code. Persisting Next.js's own `.next/cache` (webpack/SWC — the deeper
win Appwrite's log lines actually showed) was researched as a stretch goal and explicitly not attempted;
see the report for why.

**Sites request logs — shipped 2026-08-31** (kickoff: `docs/handoff/sites-request-logs-prompt.md`;
report: `docs/handoff/sites-request-logs-report.md`). The fourth and last finding from the same
Appwrite comparison: Appwrite's Sites has a "Logs" tab showing real per-request activity; Praxy's had
no equivalent — `SiteProxyMiddleware` forwarded every request and recorded nothing. Every request a
site's container actually serves is now written to a new `site_requests` table (method, path, status,
duration — metadata only, no bodies) via a bounded in-memory channel drained by a background worker, so
logging never adds latency to real site traffic and degrades by dropping entries rather than blocking
under sustained overload. Retention-eligible from day one (`Praxy:Retention:SiteRequestsMaxAgeDays`,
default 7 — deliberately shorter than every other retention window, since this table's volume — every
request to every deployed site, unconditionally — is expected to dwarf the others), unlike
`function_executions`, which deferred that question. New "Logs" tab on the site detail view.

**Additional framework presets** beyond Next.js — explicitly deferred past all of the above, owner's call
(2026-08-22).

---

## Table relationships (post-v0.1.0 initiative)

**Shipped 2026-09-01.** Deferred since before v0.1.0 shipped ("Relationships deferred to v1.1" —
Phase 2's own scope note, above). Design doc: `docs/research/table-relationships.md` — full
architecture, the tradeoffs behind reusing Praxy's existing array-column mechanism instead of an
Appwrite-style relationType/junction-table model, and why. Three phases, each independently useful:

- **Phase 1 — shipped 2026-09-01** (kickoff: `docs/handoff/relationships-phase-1-prompt.md`; report:
  `docs/handoff/relationships-phase-1-report.md`) — the primitive: a new `relationship` column type
  storing a scalar `uuid` (real Postgres FK, `ON DELETE RESTRICT` — one-to-one falls out for free via the
  existing `unique` index type) or a `uuid[]` (array flag every column type already has, no native FK
  possible on array elements). Write-time existence checking (batched one query per distinct target
  table), basic query support (`equal`/`notEqual`/`isNull`/`isNotNull`, array `contains`), a plain
  target-table `<select>` in the console's column creator, Flutter codegen's raw-id passthrough. New error
  type `relationship_target_not_found` (400). No delete-blocking yet (a blocked scalar delete still
  500s via the raw FK violation, a documented rough edge), no `?expand=`, no console search-picker.
- **Phase 2 — shipped 2026-08-31** (kickoff: `docs/handoff/relationships-phase-2-prompt.md`; report:
  `docs/handoff/relationships-phase-2-report.md`) — delete-time integrity: `RowsService.DeleteAsync`
  catches the scalar FK's real `23503` and rejects an array reference via a new `EXISTS` pre-check, both
  as `row_referenced` (409); `TablesService.DeleteAsync` gains a `relationship_dependency` (409) gate
  (more specific than the pre-existing generic `general_force_required`) with `force=true` as the one
  escape hatch for both. Found and fixed a gap the design doc didn't anticipate: `ColumnDef.TargetTableId`
  had its own metadata-level FK (`Restrict`) that made `force=true` structurally impossible to honor —
  switched to `SetNull` so a force-deleted target table's referencing columns are cleanly orphaned
  (column and data survive, `TargetTableId` clears) instead of blocking the delete outright; also fixed a
  `CatalogCache` staleness bug this surfaced (a referencing table's cached columns need invalidating too,
  not just the deleted table's own cache slot).
- **Phase 3 — shipped 2026-09-01** (kickoff: `docs/handoff/relationships-phase-3-prompt.md`; report:
  `docs/handoff/relationships-phase-3-report.md`) — read-time expansion: `?expand=<columnKey>[,...]`
  on list/get-row endpoints, a batched enrichment pass (one query per distinct target table, reusing
  `QueryCompiler.CompilePermissionPredicate` with the caller's own roles) that embeds the related row's
  full JSON in place of its raw id, falling back to the raw id for a target table that was force-deleted,
  a row that no longer exists, or a row the caller can't read — three causes, one uniform outcome, never
  an error. `OPERATORS_BY_TYPE` gains a `relationship` entry (`equal`/`notEqual`/`isNull`/`isNotNull`
  always, `contains` only when array). The console's plain text row-id input is replaced by a real
  search-as-you-type picker (`RelationshipPicker.tsx`), modeled on `RolePicker.tsx`'s
  portal-popover-with-search structure, searching the target table by `$id` prefix (client-side over one
  fetched page — no display-field concept exists to search by anything more meaningful).

Explicitly out of scope for the whole sequence, not just deferred within it: typed cross-table Flutter
codegen (a `praxy_codegen` architecture change, not a relationships deliverable — sits alongside
Storage/TOTP/multi-org as its own future initiative).

---

## Geo columns and `near` queries (post-v0.1.0 initiative)

Design doc: `docs/research/geo-nearby.md`. First slice of "geo operations," starting with the owner's
own priority — nearby/radius queries — using real great-circle distance via **PostGIS**, not flat-plane
approximation (the owner's explicit choice over the lighter `earthdistance`/`cube` contrib alternative).
This is architecturally new in a way relationships wasn't: the first Postgres extension this codebase has
ever loaded, the first non-btree/GIN index (GiST), and the first column type whose single value isn't a
bare JSON scalar. Confirmed, not assumed: **no new .NET package needed** — every column type already
reads/writes via plain `NpgsqlParameter`/`GetFieldValue<T>` scalars, never a strongly-typed mapped object,
and geo fits that same shape through plain PostGIS SQL functions (`ST_MakePoint`, `ST_DWithin`, `ST_X`/
`ST_Y`).

- **Phase 1 — shipped 2026-09-02** (kickoff: `docs/handoff/geo-nearby-phase-1-prompt.md`; report:
  `docs/handoff/geo-nearby-phase-1-report.md`) — a scalar `geo` point column
  (`geography(Point, 4326)`, wire shape `{"lat","lng"}`), a new `spatial` (GiST) index type, and
  `near(lat, lng, radiusMeters)` as a pure radius filter — rejected without a declared spatial index,
  mirroring `search`'s existing fulltext-index requirement exactly. Postgres image swapped to
  `postgis/postgis:17-3.6-alpine` in both `deploy/docker-compose.yml` and the Testcontainers fixture —
  the only two files that referenced the image tag, confirmed by grep. Found and handled a gap the
  design doc didn't anticipate: `postgis/postgis` publishes no `arm64` manifest at all (any Postgres
  major, any PostGIS minor) — both files now pin `Platform`/`platform: linux/amd64` explicitly so an
  arm64 host (this was written and verified on one) runs it under emulation instead of a hard
  "no matching manifest" failure.

- **Phase 2 — kicked off 2026-09-01** (kickoff: `docs/handoff/geo-nearby-phase-2-prompt.md`) — a new
  `orderNear(lat, lng)` query method for nearest-first distance-sorting, compiling to PostGIS's
  GiST-index-assisted KNN operator (`ORDER BY col <-> ST_MakePoint(@lng,@lat)::geography`), with full
  keyset-cursor pagination alongside it — not offset-only, corrected from this doc's earlier, more
  cautious framing of the sort/cursor model needing "real surgery." Design: the new "Phase 2" section
  of `docs/research/geo-nearby.md`.

- **Phase 3 — kicked off 2026-09-02** (kickoff: `docs/handoff/geo-nearby-phase-3-prompt.md`) — a
  returned `$distance` system field (meters, emitted with `orderNear`) and
  `withinBox(minLat, minLng, maxLat, maxLng)`, the map-viewport/bounding-box filter. Both are things
  Appwrite does not have, which is why they're sequenced ahead of the parity work below. Design: the
  "Phase 3" section of `docs/research/geo-nearby.md`.

**Competitive note, verified 2026-09-02** (details in `docs/research/geo-nearby.md`'s "Competitive
position"): Appwrite already ships `point`/`line`/`polygon` and twelve spatial predicates, so Phases 4-5
below are **catch-up to parity, not differentiation**. What Appwrite has no equivalent of is
distance-*ordering* — its four distance operators are all filters — so Phase 2's `orderNear` is already
the differentiator, and Phase 3 extends that same lead.

**Phases 4-5, agreed in scope but not yet designed** (each needs its own design pass before a prompt):
- **Phase 4** — `polygon` and `line` column types plus `within`/`intersects`/`contains`. The geofencing
  capability gap and the expensive phase: new wire shapes for multi-coordinate geometry, a console
  editor that's a real UI problem rather than a form field, and codegen decisions Phases 1-3 never
  faced. The two types belong together since they share nearly all of that plumbing.
- **Phase 5** — the parity tail: `crosses`/`touches`/`overlaps` and their negations, plus array-valued
  geo columns. Array-geo is last deliberately — relationships already model "one record, many
  locations" better and `line` covers ordered paths more meaningfully; it's in scope because the owner
  asked for the full sweep, not because the need is otherwise unmet.

---

## Storage (post-v0.1.0 initiative)

Design doc: `docs/research/storage.md`. The largest remaining product gap — Praxy has no file storage at
all today, so an app has nowhere to put an avatar, an attachment or an export. Unlike relationships and
geo (column types on an existing engine) or Sites (reusing Functions' container machinery), this is a
genuinely new pillar with its own resource hierarchy and permission surface.

**The decision it hangs off, made by the owner 2026-09-03**: file bytes live in Postgres, **split across
fixed-size chunk rows**, behind an `IFileStore` seam. This honors the fixed "PostgreSQL only — no second
datastore" decision and matches the existing `bytea` precedent (`FunctionDeploymentSource.Tar`,
`SiteDeploymentSource.Tar`), while chunking fixes what a single `bytea` value cannot do: the ~1 GB
per-value ceiling, streaming in both directions without materializing a file in memory, and cheap
`Range` support later. The seam is what keeps a disk or S3 backend addable later without touching the
API or the metadata model.

**What that costs, recorded up front rather than discovered**: every stored byte lands in every backup,
since `deploy/backup.sh` `pg_dump`s the schema. That is inherent to files-in-the-database and is managed
by `MaxStorageBytesPerProject`, not engineered away.

The resource model deliberately mirrors Tables — bucket ≈ table, file ≈ row, `BucketPermission` with
`TablePermission`'s exact shape — so the one-role-resolver and deny-by-default cross-phase rules apply
unchanged, and no second authorization concept is introduced.

- **Phase 1 — the primitive** — **shipped 2026-09-03** (kickoff:
  `docs/handoff/storage-phase-1-prompt.md`, report: `docs/handoff/storage-phase-1-report.md`) — the
  `Praxy.Storage` project, bucket CRUD + permissions, streaming upload, full-file download, delete, the
  chunk store behind its interface, three new quota dimensions, outbox events, console screens, and
  Flutter/JS SDK upload+download.
- **Phase 2 — access control and serving** — **shipped 2026-09-04** (kickoff:
  `docs/handoff/storage-phase-2-prompt.md`, report: `docs/handoff/storage-phase-2-report.md`) —
  per-file permissions following the existing row-security shape (`praxy.file_permissions`, gated by
  the bucket's `file_security` flag), HTTP `Range` pushed down into the `IFileStore` seam, and
  *opt-in* inline serving against a hard-coded safe-type allowlist. The property to keep in view:
  per-file permissions are **additive, not restrictive**, exactly like row security — a bucket-level
  `read("any")` grant means everyone reads every file, so "users only read their own uploads" means
  granting no bucket read at all. Design: the "Phase 2" section of `docs/research/storage.md`.
- **Phase 3 — image transforms** — **shipped 2026-09-05** (kickoff:
  `docs/handoff/storage-phase-3-prompt.md`, report: `docs/handoff/storage-phase-3-report.md`) —
  on-the-fly resize/crop/format/quality with cached derivatives, keyed by
  `(file_id, width, height, format, quality)` in a new `file_derivatives` table pointing at their own
  chunk rows (`file_derivative_chunks`), never `files`/`file_chunks` themselves — a derivative is a
  representation of its source file, not a resource of its own, and resolves through exactly the
  source's own `FileAccessRules` decision. Two decisions worth knowing without opening the design:
  **SkiaSharp, not ImageSharp** — ImageSharp's v4 build-time licence enforcement would land on every
  self-hoster's `docker compose up --build`; and **requested dimensions snap up to a fixed ladder**
  (64/128/256/512/1024/2048), because arbitrary `?width=` plus a cache is a storage-amplification
  vector — a size above the top rung is a clean `400`, not a silent clamp. Design: the "Phase 3"
  section of `docs/research/storage.md`. This completes the Storage sequence.

**Follow-up, kicked off 2026-09-05** (`docs/handoff/storage-transform-gravity-prompt.md`): two bugs
found reviewing Phase 3 against Appwrite's `getFilePreview`, both observed on a running instance rather
than inferred — a transparent PNG converted to JPEG lands on **black** rather than white, and **EXIF
orientation is ignored**, so phone photos transform sideways (it affects only the transform path, so
synthetic test images never catch it). Plus `gravity`, the crop anchor, since centre-cropping a
portrait to a square cuts heads off — which is the avatar case transforms exist for.
Border/radius/opacity/rotation are deliberately excluded: **every transform parameter multiplies the
derivative key space**, which is exactly what the ladder exists to bound. `gravity` earns its place as
a nine-value enum; `rotation` at 360 values and `background` at 16M colours do not, so `background`
must be bounded into the cache key or dropped for a fixed white flatten.

**Explicitly out of scope for the whole sequence**: CDN integration, signed time-limited URLs, antivirus
scanning.

## Security review (post-v0.1.0 initiative)

**Not a feature initiative.** An adversarial read of the three subsystems that turn untrusted network
input into something the host acts on — Storage (bytes served over HTTP with a caller-chosen MIME type
and filename), Sites (an attacker-authored app on an attacker-influenced hostname, proxied by the API),
and Functions (attacker-authored **code**, built and run against a root-equivalent Docker socket).
Tables/Auth/Realtime/Messaging are out of scope: they had Phase 9's hardening pass and are covered by
`tests/Praxy.LoadTests`. These three were all built after v0.1.0 and have never had a dedicated pass.

The case for it is the Storage sequence's own defect record (2026-09-03 → 09-05): a stored XSS
introduced by a *design document* rather than an implementation slip, a production crash, three
physical-naming 500s, two transform bugs found only by comparing against Appwrite on a running
instance, and a flaky test. **The suite caught none of them**, and the XSS could not have been caught
by tests at all — it was a design defect, faithfully implemented.

Two actors, and rating each finding under **both** is the point: an **anonymous network caller**, and a
**project developer** — who today is the owner and people they trust, since `CLAUDE.md` defers
multitenancy. A finding that is harmless under one trusted operator but critical the moment a second
untrusted developer shares an instance is not "low"; it is a **multitenancy prerequisite**, recorded as
one, because that is the class of defect that silently blocks a future feature. A host-root attacker is
explicitly *not* in scope — the compose file already concedes that boundary in writing; mapping what
else becomes reachable because of it is what this reviews.

Phased by **threat surface, not by subsystem**, because Functions and Sites build near-identical
`HostConfig` blocks and splitting them across sessions would mean making the same isolation decisions
twice with drift between them:

- **Phase 1 — the container boundary — shipped 2026-09-05** (kickoff:
  `docs/handoff/security-review-phase-1-prompt.md`; report:
  `docs/handoff/security-review-phase-1-report.md`) (Functions + Sites) — the Docker execution seam: `HostConfig`
  hardening applied consistently to both executors, network topology, what lands in a container's
  environment, resource limits, egress, and the image-build path as a supply-chain surface. **Runs
  first** — the only surface where untrusted *code* executes. Two findings are already verified and
  waiting for it: function containers share a Docker network with the Postgres container (Compose's
  `default` is renamed `praxy-functions`, and `postgres` joins `default` — confirmed live; Sites' own
  network has no database on it, so the asymmetry is accidental, not designed), and neither executor
  sets `PidsLimit`, `CapDrop`, `SecurityOpt`, `ReadonlyRootfs` or `User` — zero repo-wide matches for
  any of them.
- **Phase 2 — the HTTP edge — shipped 2026-09-05** (kickoff:
  `docs/handoff/security-review-phase-2-prompt.md`; report:
  `docs/handoff/security-review-phase-2-report.md`) (Storage + the Sites proxy) — the derivative/transform
  path, `ByteRanges` arithmetic, `SiteProxyMiddleware`'s header and host handling, `SiteHostPattern` and
  the `_ask-tls` endpoint sharing it, preview-URL enumeration, and whether `site_requests` logging can
  be poisoned. Storage's download edge was already well defended (`ContentDisposition`, `InlineTypes`'
  two gates, unconditional `nosniff`, verified still sound) — the real find was newer: a single-axis
  transform request (`?width=` alone) derived its other dimension from the source's own aspect ratio
  with no bound at all, so a real, honestly-encoded extreme-aspect-ratio image (no crafted file needed)
  crashed the request or silently produced a huge allocation; the fix bounds a derivative's total pixel
  area (not either axis alone, which would reject every ordinary non-square photo). Also fixed:
  one oversized HTTP method/path on a proxied site request aborted the whole batch transaction
  `SiteRequestLogWorker` writes, silently dropping every other request's log row alongside it.
- **Phase 3 — authorization and project isolation** (all three) — the permission model itself: Storage's
  additive bucket/per-file grants, the scope and lifetime of a function's minted credentials, and
  whether project isolation holds at *every* entry point, including the ones that bypass the normal API
  surface (the proxy, the webhook endpoint, the schedulers and workers).

A review phase produces **findings, not features**, so it is judged differently: every finding is
recorded whether or not it is fixed (attack, impact, severity under both actor models, fix or explicit
acceptance); **not everything gets fixed**, because a review that tries to becomes a rewrite; every fix
carries a regression test, since untested security fixes come back and the missing tests are the whole
reason this exists; and no new features. Design: `docs/research/security-review.md`.

---

## Self-hosted and managed (assessment only — not scheduled)

**Decided 2026-09-06**: follow Appwrite's shape — ship the self-hosted product *and* run a managed
version of it. Two products from one codebase, with different security requirements: a self-hosted
instance is run by someone who trusts everyone on it; a managed one hosts strangers.
`docs/research/multitenancy.md` works out what that costs, checked against the running system.
Nothing is scheduled, and there is deliberately no phase prompt.

**The reframe that matters**: four designs look like multitenancy debt — the Docker socket, a single
Postgres superuser with isolation enforced only in application code, a flat container network, and
tenant content on the console's own origin. They are better read as **four decisions that stay right
for self-host forever**, and are only problems for the managed product. `deploy/up.sh`'s one-question
setup is a selling point; none of this hardening should land in the self-hosted path if it costs that.
Appwrite does the same — the OSS product keeps the simple execution model and Cloud adds isolation
that isn't in the repo.

**One fork decides whether those four matter at all**: *one instance per tenant* (isolation at the
infrastructure layer, all four evaporate, the codebase needs almost nothing, higher cost per customer)
versus *many tenants per instance* (far cheaper, and all four become real engineering). Worth deciding
first, since it can make the rest moot — and it commits nobody to building anything.

**What's already in Praxy's favour**, verified: the tenant seam exists and is load-bearing
(`Organization`/`OrganizationMember` since Phase 0, org quotas enforced, authorization already joins
through membership); the Docker client is confined to exactly two files with fifteen consumers going
through them; the daemon endpoint and network are already configuration, just instance-wide rather
than per-tenant; and superuser is needed for exactly one statement at migration time (PostGIS), so a
non-superuser runtime looks like configuration rather than redesign.

**Working direction, taken 2026-09-06**: fork A (one instance per tenant), on the grounds that it
keeps both products as the same software and A→B is far easier than un-sharing a shared instance.
Recorded for consistency, not committed — free-tier economics haven't been modelled, and that is the
thing that would argue for B.

**The one thing needed under either fork** is the org lifecycle — create/rename/switch, invites,
operator OAuth (which `CLAUDE.md` already defers to exactly this). Ordinary feature work, commits you
to neither fork, and the only part that can start before the fork is decided.

---

## Organization lifecycle (post-v0.1.0 initiative)

The one piece of managed-hosting groundwork required under **either** infrastructure fork
(`docs/research/multitenancy.md`), buildable now and committing to neither. Also worth having on its
own: an operator today gets exactly one organization, created at signup and named "Personal", with no
way to make another, rename it, or let a colleague in.

The model has been there since Phase 0 and is load-bearing — `Organization`/`OrganizationMember`,
projects belong to orgs, org quotas enforced, authorization already joins through membership. Three
things are missing, and one of them is a trap: **`OrganizationMember.Role` is written once at signup
and never read**, so every member is effectively an owner; enforcing it is a behaviour change, not a
new feature. The console's `HomeRedirect` also states its assumption outright — *"list orgs, take the
first — there is exactly one"* — which is the single line multi-org switching invalidates.

**Organizations are not Teams.** Both have `owner`/`member`; they are different layers.
Organizations hold console *operators* and own projects; Teams hold *app users* and live inside one
project. A future session will conflate them if it doesn't read the design doc's comparison table
first — and security-review Phase 3's Finding D was a membership information leak in Teams, so the
resemblance is a trap with precedent.

- **Phase 1 — the org itself**: create, rename, delete (empty only — no cascade, no `force`, matching
  how the engine treats every other destructive action), and multi-org switching in the console. Goes
  first because it makes "exactly one org" false, which is what everything else assumes. No membership
  changes.
- **Phase 2 — members and roles**: invite by email (mirroring Teams' proven
  `SecretHash`/`InvitedAt`/`Confirmed` shape, as a pattern rather than shared code), accept, remove,
  change role, and enforce `owner` vs `member` for the first time. The phase that most needs the
  security review's habits, since it adds a whole new authorization surface — and Phase 3's Finding D
  is the specific thing to re-read before shipping it.
- **Phase 3 — operator OAuth**: what `CLAUDE.md` means by deferring operator OAuth to "future
  multitenancy work". Separable and last — an invited colleague can already accept with
  email+password — and it needs its own design pass, since operator OAuth is not app-user OAuth and
  the existing Google provider code is written for the latter.

**Explicitly out of scope for the whole sequence**: per-project operator roles, organization billing
or plans, and transferring a project between organizations. Design: `docs/research/organizations.md`.

---

---

## Rules that hold across every phase

1. **DDL is synchronous and transactional**; long operations are explicit, queryable, cancellable jobs.
2. **One role resolver.** Query compiler and realtime fan-out consume the same implementation.
3. **Deny by default.** New tables/resources are unreachable until permissions are granted.
4. **Identifiers never come from request strings** — metadata lookup, regex validation, quoting at emit.
5. **Every limit is configurable, observable (`RateLimit-*`/`Retry-After`), and loud when tripped.**
6. **Error `type` strings are public API** — snake_case, tested, never reworded casually.
7. **The outbox is written from Phase 3 even though nothing consumes it until Phase 6.**
8. **Console tests are the acceptance gate.** A phase without its console screens is not done.
9. Commit style: conventional commits, small and topical. Never commit `.env` or generated secrets.

## Handoff protocol (session-per-phase)

The owner starts each phase in a **fresh session**. At the end of phase N, the implementing session must:

1. Ensure `git status` is clean and tests pass (`dotnet test` + console build).
2. Write `docs/handoff/phase-N-report.md` — what shipped, deviations from this roadmap and why, known gaps,
   exact commands to run the stack.
3. Write `docs/handoff/phase-(N+1)-prompt.md` — a self-contained prompt for the next session: context in two
   sentences, pointer to the docs to read (`roadmap.md`, `architecture.md`, relevant `research/*`, previous
   report), the phase scope, and the owner-test checklist it must end with.
4. Print that prompt in the final message for the owner to paste.

`docs/handoff/phase-0-prompt.md` exists now; Phase 0's session starts from it.
