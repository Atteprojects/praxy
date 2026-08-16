# Phase 3 — session prompt

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 3 (Data plane) of Praxy**, a self-hosted BaaS (.NET 10 API + PostgreSQL +
Vite/React console). Phases 0–2 shipped the solution skeleton, instance claim, console operator
sessions, projects API, full app-user auth (email+password + Google OAuth, teams/memberships, the one
`IRoleResolver` — do not fork it), and the dynamic schema engine: databases → tables → columns →
indexes, synchronous DDL, the `schema_jobs` async runner with real cancel/retry, table-level permission
storage (`row_security` flag + `table_permissions`), and a console with a `<DataGrid />` primitive
(TanStack Table + Virtual) already built for reuse. This session adds row CRUD, the query DSL, keyset
pagination, permission filtering (finally consuming `row_security` and `IRoleResolver` for real), a
catalog cache, and the outbox. The plan is settled — implement, don't re-plan.

Read first, in this order:
1. `docs/handoff/phase-2-report.md` — what exists, where it lives, deviations that affect you
   (particularly: resources are addressed by stable wire id, not by the mutable `key`; column type
   changes were deferred — don't assume a `change_type` job kind exists; the row-permissions side
   table `<physical>__perms` already exists whenever `row_security` is on, created empty — you populate
   it at row-write time)
2. `docs/roadmap.md` — the Phase 3 scope block and owner-test checklist (your acceptance gate)
3. `docs/architecture.md` §4.2 (row shape, the `__perms` side table), §4.6 (query compiler rules —
   identifiers from metadata only, parameterized values, the hard caps), §5 (role resolution — reuse
   `IRoleResolver`, don't fork it), §7 (events — the outbox starts being *written* this phase, per
   CLAUDE.md's cross-phase rule)
4. `docs/research/appwrite-api.md` — the 24-method query DSL wire format, row system fields (`$id`,
   `$createdAt`, `$updatedAt`, `$permissions`, `$tableId`, `$databaseId` — no `$sequence`), list
   response shape (`{total, rows}` with a `total:false` opt-out), PATCH-is-partial
5. `docs/research/dotnet-stack.md` — UUIDv7 sort-order proof (rows use `Guid.CreateVersion7()` for
   `_id`, per roadmap.md), pooling/DataSource notes if you need a second connection path for anything

Build exactly the roadmap's Phase 3 scope:

- **Row CRUD**: create (client-supplied `rowId` or server `unique()`), get, list, update (**genuinely
  partial** — only changed fields sent, only changed fields touched; `_updated_at` moves, nothing else
  does), delete. `_id` is `Guid.CreateVersion7()`.
- **Query DSL**: JSON-per-query wire format from appwrite-api.md, the 24 v1 methods (`equal` through
  `and`/`or`). Compiler: parse → validate against column metadata (types, existence) → parameterized
  SQL. **Identifiers only ever from metadata lookup** — reuse `Praxy.Tables.PhysicalNaming` and the
  existing column/table metadata, never touch a request string directly. Caps: 100 queries × 4096 chars,
  nesting depth 3, `limit` default 25 max 100.
- **Pagination**: keyset default (`cursorAfter` → `(sort_column, _id) > (@v, @id)` tuple compare), offset
  offered but capped.
- **Permission filtering**: table-level check always; when `row_security` is on, join the existing
  `__perms` side table (`EXISTS (SELECT 1 FROM ... WHERE row_id = t._id AND action = 'read' AND role =
  ANY(@roles))`) — table `row_security`/`table_permissions` and the perms table itself already exist
  from Phase 2, this phase is the first to *read* them. `search` without a declared fulltext index is a
  400, never a silent `ILIKE`.
- **Catalog cache**: in-memory per project, invalidated by schema-change events — this is also where the
  outbox starts mattering (see below).
- **Outbox**: writes go through `praxy.events` from this phase on (CLAUDE.md cross-phase rule) —
  Phase 2 didn't write it yet, this is the first write path. Payload includes the row's permission
  roles, computed pre-commit (this is what makes DELETE events authorizable later and what Phase 4's
  realtime fan-out and Phase 6's webhooks will consume — you're not building those consumers, just
  writing correct events).

**Console** — row browser on the existing `<DataGrid />`: virtualized infinite scroll, inline cell
editing (only dirty fields sent — Appwrite's console corrupts datetimes by resending unchanged ones on
every save, don't repeat that), NULL vs FALSE visually distinct, filters popover → chips → `?query=` URL
param, sort, row side sheet with prev/next + raw JSON view + copy-as-JSON, per-row permission sheet (no
Create column at row level — that's the `withCreate` distinction console-design.md calls out), bulk
select + delete, ghost-sheet empty states with real column headers (reuse the `EmptyState` pattern
already in `components/ui.tsx`).

Constraints that hold: conventional commits, small and topical; never commit `.env`; Testcontainers
integration tests (`postgres:17-alpine`, shared collection fixture); new error `type` strings registered
in `ErrorTypes.All` (the snake_case lint test enforces the format); **identifiers never from request
strings** — metadata lookup, regex validation, `PhysicalNaming.Quote` at emit, belt and braces; deny by
default (a table with no permissions and `row_security` off already denies everyone — don't accidentally
relax that while wiring the query compiler's default path).

I have full permission for package installs and edits inside this repo. Use subagents where useful.

When done: run the roadmap's Phase 3 owner test yourself (insert rows → filter/sort → paginate past one
page → edit inline and confirm only the edited field changed → flip `row_security` and watch a non-owner
session's reads change → cursor-paginate via API → exceed a query cap and get a clear 400 with `fields`),
then follow the handoff protocol at the bottom of `docs/roadmap.md`: write
`docs/handoff/phase-3-report.md` and `docs/handoff/phase-4-prompt.md`, update CLAUDE.md's Commands
section if it changed, and print the Phase 4 prompt.
