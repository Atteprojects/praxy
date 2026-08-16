# Phase 2 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run against the dev stack by the
implementing session, in the browser, end to end. 200 tests green (130 unit, 70 integration).

## What shipped

**Catalog** — new EF migration `AuthTables` → `SchemaEngine` (never touches `InitialCatalog`/`AuthTables`):
`databases`, `tables`, `columns`, `indexes`, `table_permissions`, plus `schema_jobs` extended with
`table_id` (efficient per-table job queries) and `pid` (captures the DDL connection's backend pid for
`pg_cancel_backend`).

**Physical naming** (`src/Praxy.Tables/PhysicalNaming.cs`) — the security boundary from architecture.md
§4.1: `Sanitize` strips to `[a-z0-9_]`, every generated name gets a SHA-256-of-id hash suffix so
sanitization collisions and renames never collide, `IsSafeIdentifier`/`Quote` are the belt-and-braces
gate immediately before every DDL emission (`Quote` throws rather than emit anything that fails the
regex). Schema names are `px_<hex32>` — see deviations. Heavily unit-tested against hostile input
(embedded quotes, `DROP TABLE`/`DROP SCHEMA` payloads, emoji, 500-char keys).

**Column types & row budget** (`ColumnTypes.cs`, `RowByteBudget.cs`) — the nine types from roadmap.md
(`string`, `integer`, `float`, `boolean`, `datetime`, `email`, `url`, `ip`, `enum`; array variants via a
flag, mapping to native Postgres arrays). `RowByteBudget.Assert` sums a declared-max-size estimate per
column (strings at 4 bytes/char UTF-8 worst case, arrays capped at a 10-element assumption) against an
8000-byte default budget and throws `RowSizeExceededException` naming the largest offenders — surfaced as
`row_size_exceeded` with the culprits in the message.

**Permission grammar** (`PermissionStrings.cs`) — Appwrite's `action("role")` string form adopted
verbatim; `write` expands to create+update+delete at parse time and is never stored or returned. Role
shape validated (`any`/`guests`/`users`[`/verified`]/`user:<id>`/`team:<id>`[`/<role>`]/`member:<id>`/
`label:<name>`), lenient on free-form parts matching Phase 1's membership-roles precedent.

**Sync DDL** (`DatabasesService`, `TablesService`, `ColumnsService`) — metadata insert + `CREATE
SCHEMA`/`CREATE TABLE`/`ALTER TABLE ADD|DROP COLUMN` in one Postgres transaction (`SchemaDdl.
InTransactionAsync`), so a rollback undoes both sides together. New tables carry only the three system
columns (`_id uuid`, `_created_at`, `_updated_at`); user columns are added one at a time. Dropping a
required column or a table needs `force=true`; a column an index depends on refuses deletion outright
(`index_dependency`, 409) regardless of force — the index has to go first.

**Async DDL** (`IndexesService`, `SchemaJobRunner`) — every index (`key`/`unique`/`fulltext`) goes
through `schema_jobs`, since `CREATE INDEX CONCURRENTLY` can't run in a transaction even for a
single-column index; fulltext additionally adds a generated `tsvector` column before the `GIN` index.
The runner is a single global `BackgroundService` loop (`FOR UPDATE SKIP LOCKED` claim, `lock_timeout`
+ `statement_timeout` on the DDL connection, `SchemaJobSignal` for near-instant pickup instead of
waiting a full poll tick) — see deviations for why one worker instead of N. Cancel calls
`pg_cancel_backend` on the captured pid; the runner's own catch on SQLSTATE `57014` finalizes the job as
`cancelled` and best-effort drops the resulting `INVALID` index. Retry requeues (`status='queued'`,
`attempts+1`) and the same worker picks it back up. A stuck-`processing` sweep runs once at runner
startup (safe under the single-runner assumption).

**Table permissions** — `table_permissions(table_id, action, role)`, full-replace `PATCH`, `row_security`
off by default; turning it on creates the `__perms` side table (row_id, action, role) plus its
`(action, role) INCLUDE (row_id)` index, per architecture.md §4.2. Enforcement is Phase 3.

**Resource caps** — per architecture.md §11's threat-model line item: max databases/project, tables/
database, columns/table, indexes/table, columns-per-index, all constants in their services, one shared
error type `general_resource_limit_exceeded`.

**API** — dual surface exactly like Phase 1's users/teams: `/v1/databases/...` (data-plane, API-key only,
new `databases.read`/`databases.write` scopes — schema management is a server/CI concern, not an
end-user-session one) and `/v1/console/projects/{id}/databases/...` (operator-session, project-ownership
checked, every write audit-logged). Both sit on the same `Praxy.Tables` services. 16 new error types
registered (`database_*`, `table_*`, `column_*`, `index_*`, `index_dependency`, `schema_job_*`,
`row_size_exceeded`, `general_force_required`, `general_resource_limit_exceeded`).

**Console** — `capabilities.databases` flipped on. Databases list + create. `DatabaseLayout` — the
second-sidebar exception from console-design.md, table list + inline create, `<Outlet/>` for the
selected table. `<DataGrid />` (`components/DataGrid.tsx`) built once on TanStack Table + Virtual, reused
by Columns and Indexes (Phase 3 rows next). Columns screen: type-icon badges, attribute badges, status
badges, create sheet with a "create more" toggle and type-specific fields (size for string, elements for
enum), edit sheet with rename + required toggle + a force-aware delete confirm. Indexes screen: create
sheet with column checkboxes + per-column asc/desc, `<JobStatusBadge/>` wired to real cancel/retry via
the job list (elapsed time ticking live while processing, captured error + retry button on failure).
Table settings: row-security switch with its either/or semantics spelled out, permission matrix with
**Public read** / **Owner only** / **Team access** presets (the last opens a team picker sourced from
Phase 1's existing `useTeams`), danger zone with typed-name-confirm delete. `g d` command-palette chord.

**Tests** — 18 new unit tests (`PhysicalNamingTests`, `RowByteBudgetTests`, `PermissionStringsTests`,
caught a real write-expansion bug — see below); 9 new integration tests (`SchemaEngineTests`) covering
the full owner-test flow end to end against real Postgres (schema/table/perms-table existence verified
via `information_schema`/`pg_indexes`, not just API responses), row byte budget, key validation, SQL-
injection-shaped `name` values proven inert, scope/cross-project enforcement, the console-admin surface,
job terminal-state guards, a real requeue-and-reprocess retry cycle (via a deliberately broken injected
job — avoids racing the real runner, which finishes an empty-table index build faster than a follow-up
HTTP round trip), and the `pg_cancel_backend` → SQLSTATE `57014` mechanism the whole cancel path depends
on. All 200 tests pass (130 unit, 70 integration) — Phase 1's 182 stayed green throughout.

## Deviations & notes

- **Schema/table/column/index addressed by stable wire id in URLs, not by the mutable `key`.** Unlike
  Appwrite (where the collection/attribute id *is* the immutable slug), Praxy's architecture.md already
  separates `id`/`key`/`physical_name` as three concepts, and the roadmap explicitly wants renameable
  keys ("rename a column: instant, metadata-only"). Addressing by id — exactly like Phase 1's teams/
  users — means a rename never changes a resource's URL. `key` is a renameable, uniquely-scoped
  attribute, analogous to a Team's `name`.
- **`px_<hex32>` schema names**, not literal ULIDs — matches Phase 1's already-established deviation
  (wire ids are 32-char hex via `Ids.Wire`, not dashed UUIDs or ULID strings); architecture.md's
  "ulid" wording predates that decision.
- **Column type changes (the `change_type` job kind) are not implemented.** No owner-test step needs
  one, and Phase 2 has no row data yet to make "narrowing a type" meaningfully destructive. `UpdateAsync`
  supports rename + required-toggle only. The job runner already dispatches on `Kind`, so adding
  `change_type` later is additive, not a redesign.
- **`SchemaJobRunner` is a single global worker**, not N workers with a per-database advisory lock.
  Global serialization is a strictly stronger form of "serialized per database" (architecture.md §4.5),
  so correctness isn't affected — it trades away cross-database build parallelism for a much simpler,
  more obviously-correct implementation, which is the right call for a self-hosted default. Scaling to
  N workers is a natural v1.1 step if job volume ever warrants it (nothing about the job/payload shape
  needs to change).
- **`DataGrid` is built on `@tanstack/react-table`'s `legacy` compatibility entrypoint.** The package's
  live-registry version is now a major (v9) with a ground-up reactive-store API (`useTable` +
  `table.Subscribe`, tree-shakeable features) replacing the familiar v8 `useReactTable` hook — no v8
  precedent to lean on for a from-scratch build. TanStack ships and documents the `legacy` entrypoint
  specifically for this migration; using it (`useLegacyTable`, `getCoreRowModel`, `flexRender`) gets a
  well-understood, correctly-typed surface for a "build once, reuse in every later phase" component. The
  console's own `@tanstack/react-virtual` is unaffected (still the familiar v3 `useVirtualizer`). Worth
  revisiting once there's room to adopt the native v9 API deliberately — not this session, given the
  size of everything else in scope.
- **No `DELETE /v1/databases/{id}`** — the roadmap's Phase 2 bullet only asks for create/list/get.
  Table delete (the one the owner-test exercises) always requires `force=true`.
- **The permission role picker is simplified**: three presets (Public read / Owner only / Team access,
  the last backed by Phase 1's team list) plus a free-text role input, not the full searchable user/team
  picker modal console-design.md sketches as an "idea worth stealing." Any valid role string works; a
  richer picker is console polish, not blocking.
- Found and fixed a real bug while writing `PermissionStringsTests`: the initial `write`-expansion
  reused the "storable actions" list (which includes `read`) instead of a `create+update+delete`-only
  list, so `write("users")` was expanding to four grants instead of three. Caught by
  `Write_expands_to_create_update_delete_and_is_never_returned` before it ever reached the API.

## Known gaps (deliberate, next phases or later)

- Column type changes and their async path (`change_type`) — add when there's a concrete need.
- Multi-worker `SchemaJobRunner` with per-database advisory locks — only if job volume warrants it.
- Full role picker (search users/teams by name/avatar) — console polish.
- "Indexed checkbox on the column grid opens a pre-filled create-index sheet" (console-design.md idea) —
  not built; indexes are created from their own screen instead.
- Row-level permission *enforcement* (the query-layer read of `__perms`) is explicitly Phase 3 — this
  phase only stores the flag and creates the side table.

## Commands

```
docker run -d --name praxy-dev-pg -e POSTGRES_USER=praxy -e POSTGRES_PASSWORD=praxy \
  -e POSTGRES_DB=praxy -p 5432:5432 postgres:17-alpine   # dev database
dotnet run --project src/Praxy.Api                       # API :5090 (Scalar at /scalar/v1)
npm run dev --prefix console                              # console :5173, /v1 proxied to :5090
dotnet test                                                # 200 tests; Docker required (Testcontainers)
cd deploy && ./up.sh                                       # self-host stack → http://localhost:8080/console
```

No change to the command set — noted here since the handoff protocol asks, but CLAUDE.md's Commands
section didn't need an update.

## Owner-test checklist (run by this session, all passing)

1. Console → Databases → Create database "Blog" (key `Blog`) → schema `px_<hex32>` confirmed created
   in Postgres (`information_schema.schemata`).
2. Second sidebar appeared with the empty-state CTA → Create table "Posts" → landed on its Columns tab;
   physical table confirmed created (`information_schema.tables`).
3. Added one column of every type (string, integer, float, boolean, datetime, email, url, ip, enum) via
   the create sheet with "Create more" toggled — 9 `201 Created` calls, grid shows all 9 with correct
   type badges, attributes, and `available` status.
4. Added a unique index on `title` → job pipeline (queued → processing → available) confirmed via
   automated tests reaching real Postgres (`pg_indexes`); browser confirmed the settled `available` state
   (the empty table build completes faster than a human could screenshot the `processing` frame — expected).
5. Added a fulltext index on `title` → same pipeline, generated `tsvector` column + GIN index confirmed
   present in Postgres.
6. Added a required `slug` column (no index) → dropped it without `force` → clear
   `general_force_required` error; dropped it again with `force=true` → succeeded (204).
7. Tried dropping the required, indexed `title` column even with `force=true` → clear
   `index_dependency` (409) error naming the blocking index — deleting the index is required first.
8. Renamed the `status` column to `state` (instant PATCH, no DDL) → key changed, physical column and
   enum values untouched, resource stayed at the same URL.
9. Applied the "Owner only" permission preset → row security flipped on (badge appears next to the
   table name), `__perms` side table confirmed created, matrix shows `create` granted to `users` only.
10. Danger zone: "Delete table" stayed disabled until the typed name matched exactly (verified no
    request fired on a premature click), then a matching name enabled it → table deleted (204), console
    navigated back to the empty-database state, and a follow-up `GET` on the table correctly 404s.
