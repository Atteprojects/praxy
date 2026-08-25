# Praxy — Architecture

A self-hosted Backend-as-a-Service: authentication, a dynamic database (databases → tables → columns → rows),
realtime subscriptions, webhooks, functions, messaging, self-hosted Next.js site hosting, and an admin console.

**Status: reflects the live system, not a build plan.** This document originally shipped *before* Phase 0
as a pre-build design doc — the "Key revisions since first draft" list below is a fossil from that era,
kept for history. It has since been rewritten to describe what's actually built. The full phase-by-phase
history (including everything shipped after v0.1.0 — Sites, git integration, the two SDKs) lives in
[roadmap.md](roadmap.md); this doc gives the architectural shape, roadmap.md and the
[handoff reports](handoff/) give the blow-by-blow. Where they disagree, **roadmap.md wins** — it's updated
every session, this document is not.

Revisions from the original pre-build draft, kept for history:

- Console interleaved per phase, not a single M5 block; each phase ends with an owner console test.
- Organizations modeled fully from Phase 0 (owner/member only). Read-only and still single-org — no
  switcher, create, rename or member management.
- Console users and app users are separate namespaces; **the console is a reserved project with a hard
  data-plane guard**.
- Instance claim: first account wins; setup token required when `PRAXY_PUBLIC_URL` is set.
- Auth narrowed by owner decision: email+password + **Google OAuth only** (app users; token flow +
  PKCE). Platform/console operators are email+password only — operator OAuth is deferred to future
  multitenancy work.
- Wire formats adopted from Appwrite where battle-tested (permission grammar, query DSL JSON, event grammar,
  message-mode realtime) with fixes documented in [research/appwrite-api.md](research/appwrite-api.md).
- Webhook signatures HMAC-SHA256 over `timestamp.body` (not Appwrite's SHA-1) — GitHub's own webhook
  format (Sites/Functions git integration) is a separate, different scheme; see §7.
- Relationships remain deferred past v0.1.0. No date set.

---

## 1. Locked decisions

| Decision | Choice | Why |
|---|---|---|
| Product shape | Self-hosted product | Others run it. Upgrades, migrations, backups and docs are features, not afterthoughts. |
| Runtime | .NET 10 / ASP.NET Core 10 | Strong async I/O, native WebSockets, first-class Postgres driver. |
| Database | PostgreSQL 16+ (17 in the shipped self-host compose file) | Transactional DDL, rich types, GIN/tsvector, `SKIP LOCKED`, logical replication if we ever need CDC. |
| User data model | Real tables, metadata-driven | A user-defined table is a real Postgres table created by real DDL. Real types, real indexes, real planner. |
| Tenant isolation | Schema per *database* | Each project's each database gets its own Postgres schema. Clean namespacing, per-database export and drop. |
| Realtime source | App-level events on the write path | The data service emits events after commit; the bus fans out to WebSocket subscribers. |
| Sessions | Opaque server-side sessions | Session row in Postgres, opaque secret in an httpOnly cookie or header. Instant revocation, device listing. |
| Compute for Functions/Sites | Docker containers, one host's daemon | No in-process isolate story comparable to V8/Deno — build an image per deployment, invoke/serve over HTTP. Single-node only; no orchestration layer (Kubernetes, Swarm) — one host's Docker socket, matching the rest of the deployment's single-node posture. |

### Consequences worth naming up front

- **Functions and Sites are both Docker containers, but with different lifecycles.** A function's container
  is acquired lazily from a warm pool per invocation and idle-swept; a site's active-deployment container is
  started once and left running (crash-restarted by Docker, reconciled on `api` startup), because it's a
  continuously-reachable web app, not a request/response invocation. Both share the pattern (build an image
  from an uploaded tar or a git clone, run it, tear it down eventually) but not an implementation — see §3's
  `Praxy.Functions`/`Praxy.Sites` split.
- **Postgres has transactional DDL.** Metadata insert + `CREATE TABLE` can commit atomically in one
  transaction. This removes an entire class of "metadata says the column exists but it doesn't" bugs that
  MySQL-based BaaS platforms fight. The one exception is `CREATE INDEX CONCURRENTLY`, which cannot run inside
  a transaction — indexes therefore take the async job-runner path.
- **App-level events miss out-of-band writes.** Anyone who edits rows directly in psql produces no realtime
  event, no webhook, no function trigger. That is an acceptable, documented limitation; logical replication
  is the escape hatch if it ever stops being acceptable.
- **Self-hosted means the operator brings their own third-party accounts.** There is no Praxy-hosted GitHub
  App, SMTP relay, or OAuth client — the self-host guide (`docs/self-host.md`) walks through creating your
  own of each. This keeps Praxy itself free of third-party billing relationships and matches how Appwrite's
  own self-host story works.

---

## 2. System shape

```
                  ┌────────────────────────────────────────────────────────────────────────┐
   browser ──────▶│  Praxy.Api  (ASP.NET Core 10)                                           │
   SDK / curl     │                                                                          │
   WebSocket ────▶│ ┌──────┐┌────────┐┌──────────┐┌──────────┐┌──────────┐┌────────┐┌──────┐│
   git push ─────▶│ │ Auth ││ Tables ││ Realtime ││ Webhooks ││Functions ││Messaging││ Sites ││
   (via Caddy)     │ └──────┘└────────┘└──────────┘└──────────┘└──────────┘└────────┘└──────┘│
                  │     │         │          ▲           │           │          │        │    │
                  │     └─────────┴──▶ IEventBus ◀───────┘           │          │        │    │
                  │                       │ (outbox)                 │          │        │    │
                  │            DDL job runner  Functions/Sites build workers (Docker daemon)   │
                  │            (hosted service)         Praxy.Vcs (GitHub App, shared)         │
                  └───────────┬──────────────────────────────────────────────────────────────┘
                              │
                     ┌────────▼─────────┐        ┌─────────────────┐
                     │   PostgreSQL     │        │  Docker daemon   │◀── function/site containers,
                     │                  │        │  (host socket)   │    each on its own network
                     │  praxy.*         │  system catalog (EF Core migrations)
                     │  px_<dbid>.*     │  user tables (raw DDL)
                     └──────────────────┘        └─────────────────┘
```

Single container plus Postgres is the default self-host deployment (`deploy/docker-compose.yml`); Caddy
fronts it for automatic HTTPS, including **on-demand TLS** for Sites' dynamically-created subdomains and
custom domains (see `docs/self-host.md`'s "Sites and the wildcard subdomain" and "Custom domains" sections
— that mechanism, and the exact issuer-configuration failure mode it took two rounds to get right, is
documented in `docs/research/dotnet-stack.md`'s Caddy section, not here). `IEventBus` has an in-process
implementation for the single-node case — the only one built. `IEventBus` is an interface specifically so a
Redis-backed implementation could be added for multi-node scale-out without touching any publisher/consumer,
but that implementation doesn't exist yet; multi-node is design-ready, not shipped (§12).

### Solution layout

```
Praxy.sln
src/
  Praxy.Api/            ASP.NET Core host — endpoints, middleware, WebSocket, OpenAPI, static console
  Praxy.Core/            domain types, permission model, ID generation, error taxonomy
  Praxy.Persistence/     system catalog: EF Core 10 DbContext + migrations
  Praxy.Tables/          the dynamic engine: DDL emitter, physical naming, query compiler, row repository
  Praxy.Auth/            users, sessions, hashing, OAuth providers, tokens, teams
  Praxy.Realtime/        connection manager, channel registry, permission-filtered fan-out
  Praxy.Events/          event contracts, IEventBus (in-process only today — see §2), outbox
  Praxy.Webhooks/        outbox consumer, HMAC-SHA256 signing, retry/backoff, delivery log
  Praxy.Functions/       Docker executor, deployments, warm pool, sync/async invocation
  Praxy.Messaging/       providers, topics/subscribers, send pipeline, template system
  Praxy.Sites/           Docker executor (separate from Functions'), deployments, reverse proxy,
                         on-demand TLS ask endpoint, preview-container sweeper
  Praxy.Vcs/              GitHub App JWT signing, installation tokens, webhook verification —
                         resource-agnostic; both Praxy.Sites and Praxy.Functions consume it,
                         it references neither
console/                React + TypeScript admin SPA
sdk/
  flutter/               Dart client package (praxy_core/praxy_flutter/praxy_codegen), its own pub workspace
  js/                    Next.js/React client SDK (@praxy/core/react/nextjs/codegen), its own npm workspace
tests/
  Praxy.Tests.Unit/
  Praxy.Tests.Integration/    Testcontainers against a real Postgres, real Docker daemon for Functions/Sites
  Praxy.LoadTests/            schema/websocket/fuzz load tests, not part of `dotnet test`
deploy/
  docker-compose.yml           the self-host stack — api, postgres, caddy (https profile)
  Caddyfile                    on-demand TLS, the Sites reverse-proxy catch-all
  up.sh / backup.sh / restore.sh
docs/
```

**Data access split:** EF Core 10 owns the `praxy` system schema — it is a fixed, relational,
migration-managed schema and EF is excellent at that. The tables engine uses raw Npgsql exclusively; EF
cannot model schemas that don't exist at compile time, and trying to make it is the single most common way
this kind of project dies.

---

## 3. The system catalog

Schema `praxy`, owned by EF Core migrations. Grouped by feature area; not every column, just the shape.

```
-- Projects & access
projects            id, name, slug, settings jsonb, created_at
platforms           id, project_id, type, name, hostname       -- CORS / origin allowlist
api_keys            id, project_id, name, secret_hash, scopes text[], expires_at, last_used_at

-- Tables engine
databases           id, project_id, key, name, schema_name, created_at
tables               id, database_id, key, name, physical_name,
                     row_security bool, enabled bool, created_at, updated_at
columns              id, table_id, key, type, physical_name, required, is_array,
                     size, default_value, format, options jsonb, status, error, position
indexes               id, table_id, key, type, columns text[], orders text[],
                     physical_name, status, error
table_permissions    table_id, action, role
schema_jobs           id, database_id, kind, payload jsonb, status, attempts, error, created_at

-- Auth
users                 id, email, phone, password_hash, name, email_verified, phone_verified,
                     status, labels text[], prefs jsonb, mfa_verified (reserved — see §5), created_at
identities            id, user_id, provider, provider_uid, access_token_enc, refresh_token_enc, expires_at
sessions              id, user_id, secret_hash, provider, ip, user_agent, country,
                     mfa_verified, expires_at, created_at
tokens                id, user_id, type, secret_hash, expires_at        -- verification, recovery, magic-url, OTP
teams                 id, project_id, name, prefs jsonb
memberships           id, team_id, user_id, roles text[], confirmed, joined_at

-- Events (outbox)
events                id, project_id, type, payload jsonb, webhooks_dispatched_at,
                     functions_dispatched_at, created_at

-- Webhooks
webhook_subscriptions id, project_id, name, url, events text[], secret, enabled,
                     disabled_reason, consecutive_failures, created_at, updated_at
webhook_deliveries    id, subscription_id, project_id, event_id, event_type, payload,
                     status, attempts, next_attempt_at, last_attempt_at, last_status_code,
                     last_error, redelivered_from_id, created_at
webhook_delivery_attempts  id, delivery_id, attempt_number, started_at, duration_ms,
                     status_code, response_body, error

-- Functions
functions              id, project_id, key, name, runtime, entrypoint, timeout_seconds, enabled,
                     events text[], execute text[], schedule, next_scheduled_run_at,
                     active_deployment_id, repository_full_name, production_branch, created_at, updated_at
function_deployments   id, function_id, project_id, source_size_bytes, status, source (upload|git),
                     commit_sha, commit_message, branch, build_log, error, image_tag,
                     created_at, updated_at, activated_at
function_deployment_sources  deployment_id (PK, FK), tar bytea      -- deleted once the build finishes
function_env_vars      id, function_id, key, protected_value, created_at, updated_at
function_executions     id, function_id, project_id, trigger, async, method, path, request_body,
                     status_code, response_body, logs, errors, duration_ms, cold_start,
                     triggered_by, created_at, completed_at

-- Messaging
messaging_providers    id, project_id, type, name, enabled, is_default, config, protected_secret,
                     created_at, updated_at
messaging_topics        id, project_id, key, name, description, created_at, updated_at
messaging_targets       id, user_id, type, identifier, enabled     -- one deliverable address per channel
messaging_subscribers    id, topic_id, target_id, created_at
messages                 id, project_id, type, subject, body, status, topic_ids uuid[], user_ids uuid[],
                     created_at, completed_at
message_targets          id, message_id, project_id, target_id, identifier, status, error,
                     delivered_at, created_at
messaging_templates      id, project_id, channel, key, subject, body, created_at, updated_at   -- per-project
                     override of one of Praxy's own auth emails; no row means "use the compiled-in default"

-- Sites
sites                   id, project_id, key, name, root_directory, enabled, active_deployment_id,
                     repository_full_name, production_branch, created_at, updated_at
site_deployments        id, site_id, project_id, source_size_bytes, status, source (upload|git),
                     commit_sha, commit_message, branch, build_log, error, image_tag,
                     container_id, created_at, updated_at, activated_at
site_deployment_sources  deployment_id (PK, FK), tar bytea
site_env_vars            id, site_id, key, protected_value, created_at, updated_at
site_domains             id, site_id, project_id, hostname (globally unique), status (pending|verified),
                     created_at

-- Git integration (Praxy.Vcs — shared by Sites and Functions, references neither)
vcs_installations        id, installation_id, account_login, account_type, created_at   -- instance-wide

-- Cross-cutting
audit_log                id, project_id, actor, action, resource, ip, created_at
quotas / usage counters   see docs/self-host.md's `Praxy:Quotas:*` config table — enforced per
                        organization, one dimension per resource type (schemas, functions, sites, etc.)
```

`users`, `teams`, `sessions` and friends are **project-scoped** — carry `project_id` on `users` and index
`(project_id, email)` uniquely. Two projects may legitimately have the same user email. Every
`protected_*`/`*_enc` column is `Praxy.Auth.InstanceKey`-encrypted (AES-256-GCM) at rest, decrypted only at
the point of use (Docker container env, outbound webhook/message send).

---

## 4. The tables engine

This is the largest and riskiest component. Unchanged in shape since the original design — still the
biggest single piece of the codebase.

### 4.1 Physical naming — the security boundary

User-supplied keys are never interpolated into SQL. Every key maps to a generated physical identifier stored in
metadata:

```
schema  px_<database_ulid>                       e.g. px_01jd7q2m4v8xkr3ntf6yb9wsza
table   <sanitized_key>_<hash6>                  e.g. posts_a1b2c3
column  <sanitized_key>_<hash6>                  e.g. title_9f2e01
index   ix_<sanitized_key>_<hash6>
```

Sanitization strips everything outside `[a-z0-9_]`, lowercases, and truncates to fit Postgres' 63-byte identifier
limit with room for the suffix. The hash suffix guarantees uniqueness after sanitization collisions and after renames.
All emitted identifiers are additionally quoted and validated against a strict regex immediately before execution —
belt and braces, because a single miss here is arbitrary SQL execution.

Renaming a column becomes a metadata-only update; the physical name never changes. Self-hosters who want to read the
mapping from psql get a `praxy.schema_map` view joining metadata to physical names.

Generated SQL always uses fully-qualified `"schema"."table"` names rather than mutating `search_path`, so a single
`NpgsqlDataSource` pool is safe across all projects.

### 4.2 Row shape

Every user table gets system columns:

| Column | Type | Notes |
|---|---|---|
| `_id` | `uuid` | UUIDv7 — time-ordered, so B-tree inserts stay at the right edge. Exposed as a string. |
| `_created_at` | `timestamptz` | |
| `_updated_at` | `timestamptz` | |

Row-level permissions live in a side table created only when `row_security` is on:

```sql
CREATE TABLE px_<db>."posts_a1b2c3__perms" (
  row_id uuid NOT NULL REFERENCES px_<db>."posts_a1b2c3"(_id) ON DELETE CASCADE,
  action text NOT NULL,          -- read | update | delete
  role   text NOT NULL,          -- any | users | user:<id> | team:<id>/<role> | label:<x>
  PRIMARY KEY (row_id, action, role)
);
CREATE INDEX ON px_<db>."posts_a1b2c3__perms" (action, role) INCLUDE (row_id);
```

Listing then filters with `EXISTS (SELECT 1 FROM perms p WHERE p.row_id = t._id AND p.action = 'read' AND p.role = ANY(@roles))`.
A side table beats a `jsonb` array here because permission changes don't rewrite the row and the index is a plain
B-tree rather than a GIN. Tables with `row_security` off skip the join entirely and use table-level permissions only —
the default, since most tables don't need per-row rules and the join is pure cost.

### 4.3 Permission roles

```
any                     everyone, authenticated or not
guest                   unauthenticated only
users                   any authenticated user
user:<id>               one user
team:<id>               any member of a team
team:<id>/<role>        members holding a role in a team
member:<id>             a specific membership
label:<name>            users carrying a label
```

Resolved once per request into a `string[]` of the caller's roles, cached on the request context, and passed to both
the query compiler and the realtime fan-out filter — the **one role resolver** every consumer shares (the query
compiler, realtime fan-out, and Functions' `execute` check all call the same implementation; CLAUDE.md's own
cross-phase rule).

### 4.4 Column types

`string` (with `size`), `integer`, `double`, `boolean`, `datetime`, `email`, `url`, `ip`, `enum`, `relationship`.
The semantic types (`email`, `url`, `ip`) are `text` in Postgres plus a validation rule in metadata — validation
lives in the application so error messages are consistent and changing a rule doesn't require a table rewrite.

Arrays map to native Postgres arrays (`text[]`, `int8[]`). They cannot be indexed the same way.

**Relationships remain out of scope**, deferred past v0.1.0 with no date set — everything else in the engine is
independent of them.

### 4.5 DDL execution

A hosted `SchemaJobRunner` consumes `praxy.schema_jobs` with `FOR UPDATE SKIP LOCKED`, serialized per database so
two DDL statements never contend on the same table.

- Synchronous, in one transaction with the metadata write: `CREATE TABLE`, `ADD COLUMN`, `DROP COLUMN`, `DROP TABLE`.
  These are fast and transactional, so the API can return `available` immediately.
- Asynchronous, via the job queue: `CREATE INDEX CONCURRENTLY`, type changes that rewrite, and backfills. These
  return status `processing`, and the column or index flips to `available` or `failed` with a captured error.
- Every DDL connection sets `lock_timeout` (5s) and `statement_timeout`; a blocked `ALTER TABLE` behind a long read
  will otherwise queue every subsequent query against that table.
- Destructive changes (narrowing a type, dropping a required column) require an explicit `force` flag.

### 4.6 Query compiler

Query DSL, parsed to an AST, validated against column metadata, compiled to parameterized SQL:

```
equal, notEqual, lessThan, lessThanEqual, greaterThan, greaterThanEqual,
between, isNull, isNotNull, startsWith, endsWith, contains, search,
select, orderAsc, orderDesc, limit, offset, cursorAfter, cursorBefore, and, or
```

Non-negotiables:

1. Identifiers come from metadata lookup, never from the request string.
2. Values are always Npgsql parameters.
3. Hard caps: default limit 25, max 100; max 100 query terms; max nesting depth 3; max `select` fields.
4. Prefer keyset pagination — `cursorAfter` compiles to a `(sort_column, _id) > (@v, @id)` comparison. Offset
   pagination is offered but capped, because deep offsets scan.
5. `search` requires a declared fulltext index, which creates a generated `tsvector` column plus a GIN index. Without
   the index, the query is rejected rather than silently doing a sequential `ILIKE`.

---

## 5. Auth

**Passwords:** Argon2id (`Konscious.Security.Cryptography.Argon2`, .NET ships no Argon2). Parameters
configurable so operators can tune to their hardware.

**Session secrets:** 256 bits of CSPRNG randomness. Stored as SHA-256 — the secret already has full entropy, so a slow
KDF buys nothing and costs a hash on every request. Look up by session id, compare hashes in constant time.
Cache the resolved session in memory for ~60s and invalidate through the event bus on logout, so the hot path is not
a database round trip.

**Transport:** httpOnly, Secure, SameSite=Lax cookie for browsers; `X-Praxy-Session` header for native SDKs and
`curl`/scripted access.

**Shipped methods:** email + password, magic URL, email OTP, anonymous sessions, JWT for server-to-server
(including Functions' scoped per-invocation JWTs — see below), OAuth2 with Google behind a provider
abstraction. Phone OTP would need Messaging's SMS provider wired to it — not currently built. **TOTP MFA did
not ship** — `mfa_verified` sits reserved on `sessions`/`users` (the original plan's own hedge against a
later migration) but no enrollment/verification flow was ever built on top of it; still open.

Magic URL and email verification need outbound email: a small SMTP sender with configurable host, plus the
project-overridable `messaging_templates` system (§3) Praxy's own auth emails render through.

**API keys** authenticate server-side callers at project scope with an explicit scope list, and may optionally bypass
row permissions — that bypass defaults off.

**Functions' scoped JWTs:** an invocation triggered by a specific app user (a JWT or authenticated session on
the data-plane invoke endpoint) gets `PRAXY_FUNCTION_JWT` injected into its container — lets function code
call back into the data plane *as that user*, not with elevated access. Absent for console/event/schedule
triggers. See `docs/functions-runtimes.md`.

**Rate limiting:** ASP.NET Core's built-in rate limiter. Buckets partition on **project + caller identity** (the
presented API key or session, hashed), falling back to the source address only for callers that present neither —
a shared NAT must not mean a shared budget. Memory-backed — single-node only today, same caveat as
`IEventBus` above. Auth
endpoints get much tighter limits than the rest; the data plane carries its own ceilings (rows, function
invocation, realtime ticket minting), with function invocation tightest because each permitted request can start a
container. Every limit is configurable (`Praxy:RateLimits:*`, see [self-host.md](self-host.md)).

---

## 6. Realtime

**Endpoint:** `GET /v1/realtime?project=<id>&channels[]=...`, upgraded to a WebSocket.

**Authentication:** browsers send the session cookie. Native clients call `POST /v1/realtime/ticket` to mint a
single-use, 60-second ticket and pass it as a query parameter — this avoids putting a long-lived session secret in a
URL, where it lands in proxy logs.

**Channels** mirror the resource tree:

```
databases.<dbId>.tables.<tableId>.rows
databases.<dbId>.tables.<tableId>.rows.<rowId>
account
teams.<teamId>
```

**Fan-out:** the connection manager holds a `ConcurrentDictionary<string, Connection>` plus a channel → connection
index. Each connection owns a **bounded** `Channel<Message>` (capacity ~256) with a single writer loop. When a
consumer is too slow and the buffer fills, close the connection with code `1013` and let the client resubscribe —
unbounded buffering is how a realtime server runs a node out of memory.

**Permission filtering happens at fan-out, not at subscribe.** Each event carries the affected row's permission roles;
each connection carries its subscriber's resolved roles; the intersection decides delivery. When a user's roles change
(team join, label change), publish a `memberships` event that forces affected connections to re-resolve.

Ping every 30 seconds, drop on missed pong. Cap connections per project as a quota. The console's own
Realtime Inspector (§9) subscribes as an ordinary client and is the cheapest debugging tool for this logic.

---

## 7. Events

```csharp
record PraxyEvent(
    string  Id,            // UUIDv7
    DateTimeOffset At,
    string  ProjectId,
    string  Type,          // "databases.<db>.tables.<t>.rows.<r>.create"
    string[] Permissions,  // roles allowed to see the payload
    JsonNode Payload);
```

One event stream, several consumers, all shipped: **realtime** fan-out (best-effort, in-process, published
after commit), **webhooks** (at-least-once, reading from the `praxy.events` outbox written in the same
transaction as the data change, tracked via `webhooks_dispatched_at`), and **function triggers**
(event-pattern matching against a function's `Events` list, tracked via `functions_dispatched_at` — a row
is only deleted by the retention sweep once *both* dispatch columns are set, so a slow consumer never loses
an event to cleanup racing ahead of it).

**A separate, unrelated webhook format exists for git integration** (Sites/Functions push-to-deploy):
GitHub's own `X-Hub-Signature-256: sha256=<hex HMAC-SHA256(raw body)>` scheme, verified by
`Praxy.Vcs.GitHubWebhookSignature` — deliberately not reusing `Praxy.Webhooks.WebhookSignature`'s
`timestamp.body` scheme above, since it's a different wire format GitHub controls, not Praxy's own.

---

## 8. API conventions

- Base path `/v1`, REST, JSON, snake-free camelCase bodies.
- Headers: `X-Praxy-Project`, `X-Praxy-Key`, `X-Praxy-Session`.
- Error envelope: `{ message, code, type, version }` with stable machine-readable `type` strings — SDKs and users
  switch on `type`, so treat it as public API and never reword one without a deprecation.
- OpenAPI generated by .NET 10's built-in document generation, published per release as
  `docs/openapi/v1.json`; a live, interactive `/scalar/v1` in Development only. See
  [docs/api-reference.md](api-reference.md) for how it ships and what `OpenApiDocumentTests` guarantees
  about it.
- A written versioning and deprecation policy is a v1 deliverable, not a nicety — it is the thing that makes upgrades
  safe for people running this on their own servers.

---

## 9. Console

React + TypeScript + Vite, TanStack Query, Tailwind, built into static assets and served by the API
container at the root path (any hostname reaching the container gets the console; optionally its own
subdomain via `PRAXY_CONSOLE_DOMAIN`, see `docs/self-host.md`). One container, no CORS, no second
deployment for the operator to think about.

**Shipped screens**, grouped by area: onboarding/claim and login; organization and project list/overview;
Auth (users, sessions, teams, auth settings); Databases (database → table → columns/indexes/permissions,
row browser with inline editing); a Realtime Inspector; API keys; platforms; audit log; Webhooks
(subscriptions, delivery log with redeliver); Functions (list, deployments with a build-log tailing sheet,
executions, settings including env vars/schedule/execute-roles/git-repository connect); Messaging
(providers, topics/subscribers, message send/status, templates); Sites (list, deployments, settings
including env vars/custom domains/git-repository connect); an instance-wide GitHub settings page (install
status, shared by Sites and Functions' git integration). Full route list: `console/src/router.tsx`.

---

## 10. Operations

Being a self-hosted product means these are features:

- `cd deploy && ./up.sh` — asks one question (a public domain, or blank for local/plain-HTTP), installs
  Docker if missing, generates `deploy/.env` with fresh secrets, and (domain given) brings up Caddy for
  automatic HTTPS plus a best-effort firewall lockdown.
- Migrations execute at startup, so a fresh clone or an upgrade both converge without a separate step.
- `/v1/health` liveness/readiness. Serilog structured logs.
- `./backup.sh` / `./restore.sh` back up **two kinds of schema** (the `praxy` system catalog and each
  project database's own `px_<dbid>` schema) — both are needed together, neither alone is useful. Tested,
  documented in `docs/self-host.md`.
- Upgrades never break an existing project schema. All system-schema changes go through EF migrations.
- Secrets hashed at rest (session secrets, API keys, tokens); every `protected_*`/`*_enc` column
  (`InstanceKey`, AES-256-GCM) — OAuth provider tokens, function/site env vars, messaging provider config.
- **Functions and Sites both need a reachable Docker daemon at runtime**, not just for tests — self-host
  mounts the host's `/var/run/docker.sock` into the `api` container for this (documented tradeoff:
  root-equivalent host access from inside that container). Each feature gets its own named Docker network
  (`praxy-functions`, `praxy-sites`) so their containers can't reach each other by default.
- **Sites additionally needs a wildcard DNS record** and, for push-to-deploy, an operator-created GitHub
  App and an internet-reachable instance (GitHub must be able to deliver the webhook). Full setup:
  `docs/self-host.md`'s "Sites and the wildcard subdomain" and "Git integration" sections.

---

## 11. Threat model

| Threat | Mitigation |
|---|---|
| SQL injection through table/column keys | Generated physical identifiers, metadata-only lookup, regex validation and quoting at emit time, parameterized values everywhere |
| Cross-tenant data access | Schema per database, project resolved once per request and carried in an immutable request context; fully-qualified identifiers so `search_path` can never rescue a mistake |
| Defence in depth for the above | **Deferred:** a low-privilege Postgres role per project, applied with `SET LOCAL ROLE` per transaction, so the database refuses cross-schema access even if application code slips |
| Resource exhaustion | Caps on tables, columns and indexes per project; query limits; rate limits on auth **and the whole data plane** (rows, function invocation, realtime tickets); WebSocket connection quotas; `statement_timeout` on every connection; per-org quotas on functions/sites/preview containers |
| Unauthorized function/data-plane execution | Per-function `execute` role list resolved through the one role resolver; empty by default, so a new function is reachable by nobody. API keys need the matching scope *and* a matching role |
| Account enumeration | Constant-time comparisons, uniform responses and timing on login, signup and recovery |
| Cross-origin abuse | Per-project platform allowlist enforced as a CORS origin check |
| Slow-consumer memory exhaustion | Bounded per-connection realtime channels with disconnect-on-overflow |
| Outbound webhook SSRF | An SSRF guard on subscription URLs (blocks internal/loopback/link-local targets) before a delivery is ever attempted |
| Arbitrary code execution scope (Functions/Sites) | Each build/run happens in its own Docker container, one network per feature, no shared filesystem with the host beyond what the image itself contains; env vars encrypted at rest and only decrypted into the container at invoke/start time |
| On-demand TLS cert-issuance abuse (Sites) | Caddy's `ask` endpoint (`/v1/sites/_ask-tls`) is a strict allow-list against real, enabled, deployed sites/custom domains only — anything else is refused before Caddy ever attempts an ACME order, protecting the instance's own Let's Encrypt rate limit |
| Forged git-integration webhooks | GitHub's `X-Hub-Signature-256` verified over the *raw* request body before any JSON model binding touches it (constant-time compare); a push for a repository nothing has connected is a silent no-op, not an error that could leak which repositories are wired up |
| GitHub App credential misuse | The App's private key only ever signs short-lived (~10 min) identity JWTs, exchanged for further short-lived (~1 hr) installation tokens scoped to one installation — the long-lived key itself is never used as a bearer credential anywhere |

---

## 12. History and roadmap

The M0–M6 milestone table that used to live here was the pre-build estimate; it's gone because it's no
longer informative — the real, dated, per-phase history (what shipped when, what changed from the original
plan and why, every session's owner-test) lives in **[roadmap.md](roadmap.md)**, with the full prompt/report
pair for each phase in **[handoff/](handoff/)**. That's the source of truth for "what happened," not this
file.

**Not built, still open** (tracked here since they're architectural, not a specific phase's leftover):
Storage (buckets, resumable uploads, image transforms), TOTP MFA (flag reserved, no flow), relationships in
the tables engine, multi-node scale-out, per-project Postgres roles (the threat-model "defence in depth"
row above), additional Sites framework presets beyond Next.js (owner's explicit deferred call,
2026-08-22).

---

## 13. Threat-model risks, in retrospect

The original pre-build risk list, kept because the retrospective is more useful than the prediction was:

1. **"The tables engine is bigger than it looks"** — accurate. It's still the largest single component by
   code volume and the one with the most non-negotiable correctness rules (§4.6). Relationships were indeed
   the cut, and remain cut.
2. **"The console is a second project"** — accurate, and grew further than expected: it now covers seven
   feature areas (§9), not the original four.
3. **"Schema sprawl"** — addressed directly: load tests at 1k schemas / 10k WebSocket connections /
   query-compiler fuzzing are part of the hardening phase and re-run in `tests/Praxy.LoadTests`, not just a
   one-time check.
4. **"Scope creep from 'like Appwrite'"** — held, mostly by design discipline: several Sites/Functions
   git-integration features Appwrite has (commit statuses, PR comments, branch-pattern filters,
   build-command auto-detection) were explicitly cut after checking Appwrite's actual current docs, not
   assumed necessary from memory. See `docs/handoff/sites-phase-4-prompt.md`'s Non-goals for the concrete
   example.

---

## 14. Resolved questions

The original open questions, all since resolved:

- **License.** MIT.
- **Relationships in v1?** No — shipped without them, still deferred with no date.
- **ID format.** UUIDv7 (`Guid.CreateVersion7()`) for row/entity ids — index locality as recommended.
- **Product naming.** Praxy — final, load-bearing in the API prefix, schema prefix (`praxy`/`px_*`), and
  every SDK package name (`praxy_*`, `@praxy/*`).
