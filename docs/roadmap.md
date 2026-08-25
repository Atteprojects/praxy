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

**Sites Phase 4 — git integration** (kickoff: `docs/handoff/sites-phase-4-prompt.md`, written
2026-08-24) — not yet implemented. Push to a site's production branch builds and auto-activates; push to
any other branch builds a deployment and leaves it on its existing Phase 2 preview URL — no new serving
infrastructure needed for that half. Real design in `research/praxy-sites.md`'s "Phase 4" section,
including two scope-*cutting* findings from re-checking Appwrite's actual deploy-from-git docs (no commit
statuses/PR comments, no build-command auto-detection) that make this phase smaller than the original
sketch assumed. Self-hosted owner configures their own GitHub App, same as Appwrite requires — and the
instance must be internet-reachable for GitHub's webhooks to arrive, so real verification targets
`praxycore.dev`, not local dev.

**Additional framework presets** beyond Next.js — explicitly deferred past all of the above, owner's call
(2026-08-22).

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
