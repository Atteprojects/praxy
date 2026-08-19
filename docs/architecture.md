# Praxy — Architecture & Build Plan

A self-hosted Backend-as-a-Service: authentication, a dynamic database (databases → tables → columns → rows),
realtime subscriptions, and an admin console. Storage, webhooks, functions and messaging follow in later phases.

**Status:** planning complete. Phases and acceptance gates live in [roadmap.md](roadmap.md); research-backed
decisions in [research/](research/). Where this file and those disagree, **roadmap.md and research/ win** —
they postdate this document. Key revisions since first draft:

- Console interleaved per phase, not a single M5 block; each phase ends with an owner console test.
- Organizations modeled fully from Phase 0 (owner/member only). The owning org is now visible in the
  console: home resolves it, its id sits in the URL (`/organization/<id>`), and its name heads the
  projects list. Read-only and still single-org — no switcher, create, rename or member management.
- Console users and app users are separate namespaces; **the console is a reserved project with a hard
  data-plane guard**.
- Instance claim: first account wins; setup token required when `PRAXY_PUBLIC_URL` is set.
- Auth v1 narrowed by owner decision: email+password + **Google OAuth only** (app users; token flow +
  PKCE). Platform/console operators are email+password only — operator OAuth is deferred to future
  multitenancy work. Sessions
  auto-created on signup; per-user session cap (10, oldest evicted). Redirect URLs validated against the
  platform allowlist. Session revocation kills live WebSockets.
- Wire formats adopted from Appwrite where battle-tested (permission grammar, query DSL JSON, event grammar,
  message-mode realtime) with fixes documented in [research/appwrite-api.md](research/appwrite-api.md).
- Webhook signatures HMAC-SHA256 over `timestamp.body` (not Appwrite's SHA-1).
- Flutter SDK (Phase 5) constrains the API from day one — request ids, `Retry-After`, structured field
  errors, partial PATCH, API version header: [research/flutter-sdk.md](research/flutter-sdk.md).
- Relationships confirmed deferred to v1.1. Roadmap phases replace the M0–M6 table in §12 below.

---

## 1. Locked decisions

| Decision | Choice | Why |
|---|---|---|
| Product shape | Self-hosted product | Others run it. Upgrades, migrations, backups and docs are features, not afterthoughts. |
| Runtime | .NET 10 / ASP.NET Core 10 | Installed and default (`10.0.100`). Strong async I/O, native WebSockets, first-class Postgres driver. |
| Database | PostgreSQL 16+ | Transactional DDL, rich types, GIN/tsvector, `SKIP LOCKED`, logical replication if we ever need CDC. |
| User data model | Real tables, metadata-driven | A user-defined table is a real Postgres table created by real DDL. Real types, real indexes, real planner. |
| Tenant isolation | Schema per *database* | Each project's each database gets its own Postgres schema. Clean namespacing, per-database export and drop. |
| Realtime source | App-level events on the write path | The data service emits events after commit; the bus fans out to WebSocket subscribers. |
| Sessions | Opaque server-side sessions | Session row in Postgres, opaque secret in an httpOnly cookie or header. Instant revocation, device listing. |
| v1 scope | Auth + Database + Realtime + Console | Webhooks, Storage, Functions, Messaging are phase 2+. |

### Consequences worth naming up front

- **Functions will be Docker containers.** .NET has no in-process isolate story comparable to V8. Phase 2 Functions
  means: build an image per deployment, keep a warm container pool, invoke over HTTP. Design the Functions *API
  surface* now so the executor stays swappable; don't design the executor yet.
- **Postgres has transactional DDL.** Metadata insert + `CREATE TABLE` can commit atomically in one transaction. This
  removes an entire class of "metadata says the column exists but it doesn't" bugs that MySQL-based BaaS platforms fight.
  The one exception is `CREATE INDEX CONCURRENTLY`, which cannot run inside a transaction — indexes therefore take the
  async path.
- **App-level events miss out-of-band writes.** Anyone who edits rows directly in psql produces no realtime event.
  That is an acceptable, documented limitation; logical replication is the escape hatch if it ever stops being acceptable.

---

## 2. System shape

```
                  ┌──────────────────────────────────────────────┐
   browser ──────▶│  Praxy.Api  (ASP.NET Core 10)                │
   SDK / curl     │                                              │
   WebSocket ────▶│  ┌────────┐ ┌──────────┐ ┌────────────────┐  │
                  │  │  Auth  │ │  Tables  │ │   Realtime     │  │
                  │  └────────┘ └──────────┘ └────────────────┘  │
                  │       │           │              ▲           │
                  │       └───────────┴──▶ IEventBus ┘           │
                  │                          │                   │
                  │       DDL job runner ◀───┘  (hosted service)  │
                  └───────────┬──────────────────────────────────┘
                              │
                     ┌────────▼─────────┐
                     │   PostgreSQL     │
                     │                  │
                     │  praxy.*         │  system catalog (EF Core migrations)
                     │  px_<dbid>.*     │  user tables (raw DDL)
                     └──────────────────┘
```

Single container plus Postgres is the default self-host deployment. `IEventBus` has an in-process implementation for
that case and a Redis implementation for multi-node; Redis stays optional in v1 and earns its place later for
distributed rate limiting and session caching.

### Solution layout

```
Praxy.sln
src/
  Praxy.Api/            ASP.NET Core host — endpoints, middleware, WebSocket, OpenAPI, static console
  Praxy.Core/           domain types, permission model, ID generation, error taxonomy
  Praxy.Persistence/    system catalog: EF Core 10 DbContext + migrations
  Praxy.Tables/         the dynamic engine: DDL emitter, physical naming, query compiler, row repository
  Praxy.Auth/           users, sessions, hashing, OAuth providers, tokens, teams
  Praxy.Realtime/       connection manager, channel registry, permission-filtered fan-out
  Praxy.Events/         event contracts, IEventBus (in-proc + Redis), outbox
console/                React + TypeScript admin SPA
tests/
  Praxy.Tests.Unit/
  Praxy.Tests.Integration/   Testcontainers against a real Postgres
deploy/
  docker-compose.yml
docs/
```

**Data access split:** EF Core 10 owns the `praxy` system schema — it is a fixed, relational, migration-managed
schema and EF is excellent at that. The tables engine uses raw Npgsql exclusively; EF cannot model schemas that
don't exist at compile time, and trying to make it is the single most common way this kind of project dies.

---

## 3. The system catalog

Schema `praxy`, owned by EF Core migrations.

```
projects            id, name, slug, settings jsonb, created_at
platforms           id, project_id, type, name, hostname       -- CORS / origin allowlist
api_keys            id, project_id, name, secret_hash, scopes text[], expires_at, last_used_at

databases           id, project_id, key, name, schema_name, created_at
tables              id, database_id, key, name, physical_name,
                    row_security bool, enabled bool, created_at, updated_at
columns             id, table_id, key, type, physical_name, required, is_array,
                    size, default_value, format, options jsonb, status, error, position
indexes             id, table_id, key, type, columns text[], orders text[],
                    physical_name, status, error
table_permissions   table_id, action, role
schema_jobs         id, database_id, kind, payload jsonb, status, attempts, error, created_at

users               id, email, phone, password_hash, name, email_verified, phone_verified,
                    status, labels text[], prefs jsonb, mfa_enabled, created_at
identities          id, user_id, provider, provider_uid, access_token_enc, refresh_token_enc, expires_at
sessions            id, user_id, secret_hash, provider, ip, user_agent, country,
                    mfa_verified, expires_at, created_at
tokens              id, user_id, type, secret_hash, expires_at        -- verification, recovery, magic-url, OTP
teams               id, project_id, name, prefs jsonb
memberships         id, team_id, user_id, roles text[], confirmed, joined_at

events              id, project_id, type, payload jsonb, created_at   -- outbox; phase 2 consumers
audit_log           id, project_id, actor, action, resource, ip, created_at
```

`users`, `teams`, `sessions` and friends are **project-scoped** — carry `project_id` on `users` and index
`(project_id, email)` uniquely. Two projects may legitimately have the same user email.

---

## 4. The tables engine

This is the largest and riskiest component. Budget accordingly.

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
make that the default, since most tables don't need per-row rules and the join is pure cost.

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
the query compiler and the realtime fan-out filter. One resolver, two consumers — never two implementations.

### 4.4 Column types

`string` (with `size`), `integer`, `double`, `boolean`, `datetime`, `email`, `url`, `ip`, `enum`, `relationship`.
The semantic types (`email`, `url`, `ip`) are `text` in Postgres plus a validation rule in metadata — keep validation
in the application so error messages are consistent and changing a rule doesn't require a table rewrite.

Arrays map to native Postgres arrays (`text[]`, `int8[]`). They cannot be indexed the same way — document that.

**Relationships are the biggest optional chunk of v1.** One-to-many and many-to-one are an FK column; many-to-many is
an auto-created junction table. Nested population needs a depth cap (3 is a sane limit) and careful N+1 avoidance via
a single join or a batched second query. If the schedule slips, cut relationships to v1.1 — everything else in the
engine is independent of them.

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

**Passwords:** Argon2id. .NET ships no Argon2 — use `Konscious.Security.Cryptography.Argon2`. Parameters in config so
operators can tune to their hardware; document the defaults and the memory cost.

**Session secrets:** 256 bits of CSPRNG randomness. Stored as SHA-256 — the secret already has full entropy, so a slow
KDF buys nothing and costs a hash on every request. Look up by session id, compare hashes in constant time.
Cache the resolved session in memory for ~60s and invalidate through the event bus on logout, so the hot path is not
a database round trip.

**Transport:** httpOnly, Secure, SameSite=Lax cookie for browsers; `X-Praxy-Session` header for native SDKs.

**v1 methods:** email + password, magic URL, email OTP, anonymous sessions, JWT for server-to-server, OAuth2 with
Google behind a provider abstraction. Phone OTP waits for Messaging. TOTP MFA is phase 2 — but put the
`mfa_verified` flag on `sessions` now so it isn't a migration later.

Magic URL and email verification need outbound email in v1: a small SMTP sender with configurable host, plus templates.
That is the minimum, not a full Messaging module.

**API keys** authenticate server-side callers at project scope with an explicit scope list, and may optionally bypass
row permissions — that bypass is exactly the flag that leaks data when it defaults wrong, so default it off.

**Rate limiting:** ASP.NET Core's built-in rate limiter. Buckets partition on **project + caller identity** (the
presented API key or session, hashed), falling back to the source address only for callers that present neither —
a shared NAT must not mean a shared budget. Memory-backed on a single node, Redis-backed for multi-node. Auth
endpoints get much tighter limits than the rest; the data plane carries its own ceilings (rows, function
invocation, realtime ticket minting), with function invocation tightest because each permitted request can start a
container. Every limit is configurable (`Praxy:RateLimits:*`, see [self-host.md](self-host.md#configuration)).

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

Ping every 30 seconds, drop on missed pong. Cap connections per project as a quota.

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

One event stream, several consumers: realtime fan-out now; webhooks, function triggers and the audit log in phase 2.
Realtime is best-effort and publishes in-process after commit. Webhooks need at-least-once, so they read from the
`praxy.events` **outbox** table written inside the same transaction as the data change. Create that table in v1 even
though nothing consumes it yet — retrofitting an outbox after the fact means touching every write path a second time.

---

## 8. API conventions

- Base path `/v1`, REST, JSON, snake-free camelCase bodies.
- Headers: `X-Praxy-Project`, `X-Praxy-Key`, `X-Praxy-Session`.
- Error envelope: `{ message, code, type, version }` with stable machine-readable `type` strings — SDKs and users
  switch on `type`, so treat it as public API and never reword one without a deprecation.
- OpenAPI generated by .NET 10's built-in document generation, published per release. v1 ships the spec plus a thin
  hand-written TypeScript client; generated SDKs for other languages come in phase 2.
- A written versioning and deprecation policy is a v1 deliverable, not a nicety — it is the thing that makes upgrades
  safe for people running this on their own servers.

---

## 9. Console

React + TypeScript + Vite, TanStack Query, Tailwind and shadcn/ui, built into static assets and served by the API
container at the root path (any hostname reaching the container gets the console; optionally its own
subdomain via `PRAXY_CONSOLE_DOMAIN`, see docs/self-host.md). One container, no CORS, no second deployment for
the operator to think about.

Blazor WASM would keep everything in C#, but the component ecosystem for the two hardest screens here — an editable
data grid and a schema designer — is meaningfully weaker. Not worth the language purity.

v1 screens: onboarding and login, project list, project overview, Auth (users, sessions, teams, settings), Databases
(database → table → columns / indexes / permissions, plus a row browser with inline editing), a realtime inspector
that tails the live event stream, API keys, and settings.

The realtime inspector is worth building early. It is the cheapest possible debugging tool for the fan-out logic and
it demos extremely well.

---

## 10. Operations

Being a self-hosted product means these are features:

- `docker compose up` with a `.env` that generates its own secrets on first run.
- Migrations execute at startup behind a Postgres advisory lock, so multi-node rollouts don't race.
- `/v1/health` liveness and readiness. Serilog structured logs, OpenTelemetry traces and metrics.
- Backup and restore documented per project schema (`pg_dump -n px_<dbid>`), and tested.
- Upgrades never break an existing project schema. All system-schema changes go through EF migrations, and a
  release is not shippable until an upgrade from the previous version has been run against real data.
- Secrets hashed at rest (session secrets, API keys, tokens); OAuth provider tokens encrypted with a project key.

---

## 11. Threat model

| Threat | Mitigation |
|---|---|
| SQL injection through table/column keys | Generated physical identifiers, metadata-only lookup, regex validation and quoting at emit time, parameterized values everywhere |
| Cross-tenant data access | Schema per database, project resolved once per request and carried in an immutable request context; fully-qualified identifiers so `search_path` can never rescue a mistake |
| Defence in depth for the above | **v1.1:** a low-privilege Postgres role per project, applied with `SET LOCAL ROLE` per transaction, so the database refuses cross-schema access even if application code slips |
| Resource exhaustion | Caps on tables, columns and indexes per project; query limits; rate limits on auth **and the whole data plane** (rows, function invocation, realtime tickets); WebSocket connection quotas; `statement_timeout` on every connection |
| Unauthorized function execution | Per-function `execute` role list resolved through the one role resolver; empty by default, so a new function is reachable by nobody. API keys need the `functions.execute` scope *and* a matching role |
| Account enumeration | Constant-time comparisons, uniform responses and timing on login, signup and recovery |
| Cross-origin abuse | Per-project platform allowlist enforced as a CORS origin check |
| Slow-consumer memory exhaustion | Bounded per-connection channels with disconnect-on-overflow |

---

## 12. Roadmap

| Milestone | Scope | Estimate |
|---|---|---|
| **M0 — Foundations** | Solution skeleton, Docker Compose, EF migrations for the system catalog, config, logging, health, error envelope, OpenAPI, Testcontainers integration harness | 1–2 weeks |
| **M1 — Projects & keys** | Project CRUD, schema provisioning, API key auth middleware, project context resolution, platform allowlist | 1 week |
| **M2 — Auth** | Users, password auth, sessions, cookies, account endpoints, teams and memberships, role resolution, magic URL and email OTP, SMTP sender, Google OAuth, rate limits | 2–3 weeks |
| **M3 — Tables engine** | Databases, tables, columns, indexes, DDL job runner, physical naming, row CRUD, permission model, query compiler, pagination, validation | 3–4 weeks |
| **M4 — Realtime** | WebSocket endpoint, ticket auth, channel registry, permission-filtered fan-out, backpressure, heartbeats | 1–2 weeks |
| **M5 — Console** | The admin SPA, all v1 screens | 3–4 weeks |
| **M6 — Hardening & release** | Quotas, audit log, backup and upgrade testing, load test, security pass, docs, v0.1.0 | 2 weeks |

Roughly **four to five months** of focused solo work to a credible v0.1.

**Phase 2:** Storage (buckets, resumable uploads, S3 backend, image transforms), Webhooks (outbox consumer, HMAC
signing, retry with backoff, delivery log), Functions (Docker executor, deployments, warm pool), TOTP MFA,
generated SDKs.

**Phase 3:** Messaging (email, SMS and push providers, topics, targets, subscribers), scheduled functions,
multi-node scale-out with Redis, per-project Postgres roles.

---

## 13. Risks

1. **The tables engine is bigger than it looks.** Query compiler plus permissions plus relationships is where projects
   of this kind stall. Relationships are the designated cut.
2. **The console is a second project.** Three to four weeks assumes reusing a component library and not designing from
   scratch. If it slips, ship a minimal console — tables, rows, users — and iterate.
3. **Schema sprawl.** Load-test with a thousand schemas early. `pg_dump`, `pg_catalog` queries and connection startup
   all degrade in ways that are much cheaper to discover in month one than month five.
4. **Scope creep from "like Appwrite."** Appwrite is years of work by a team. The v1 line above is already ambitious;
   defend it.

---

## 14. Open questions

- **License.** MIT/Apache-2 versus BSL matters a lot for a self-hosted product you might want to monetize, and it is
  much easier to decide now than after contributors arrive.
- **Relationships in v1?** Recommendation: no. Ship the engine, add them in v1.1 with real usage to guide the design.
- **ID format.** UUIDv7 recommended above for index locality; ULID strings are friendlier in URLs. Pick one and make
  it total.
- **Product naming.** Is "Praxy" final? It fixes the header names, the API prefix, the schema prefix and the SDK
  package names — cheap now, expensive later.
