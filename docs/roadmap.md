# Praxy — Roadmap

Phased build plan. **Each phase ends with a console the owner can click through** — that walkthrough is the
acceptance gate. Each phase is implemented in a fresh session; the handoff protocol is at the bottom.

Fixed choices (owner's): .NET backend · Vite console · Flutter SDK first · features = Auth, Databases/Tables,
Realtime, Messaging, Functions, Webhooks · minimal options to start (GitHub is the only OAuth provider;
email+password and GitHub are the only sign-in methods until further notice).

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
container at `/console`. Screens: claim/login, chrome-less create-project card, project list, project
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
- **GitHub OAuth only** — token flow (callback carries userId + 60s-JWT-wrapped secret → exchanged at
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
members, auth settings (method toggles, GitHub credentials, session limits, password policy), API keys
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
sessions, GitHub OAuth via flutter_web_auth_2 (Android intent filter documented; iOS needs nothing — see
[research/flutter-sdk.md](research/flutter-sdk.md), which is the full spec).
**Owner test:** run the example app against local Praxy — sign up, GitHub sign-in, CRUD rows, watch a
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
