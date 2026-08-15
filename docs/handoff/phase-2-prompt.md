# Phase 2 — session prompt

> **Correction (post-Phase-2):** where this prompt says "GitHub OAuth" for app users below, the actual
> corrected scope is **Google OAuth** for app users — GitHub was meant for platform/console operators
> (deferred to future multitenancy work), per owner clarification. See `docs/roadmap.md` and
> `docs/architecture.md` for current truth.

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 2 (Schema engine) of Praxy**, a self-hosted BaaS (.NET 10 API +
PostgreSQL + Vite/React console). Phases 0–1 shipped the solution skeleton, instance claim, console
operator sessions, projects API, and full app-user auth: email+password + GitHub OAuth sessions
(with a per-user cap and a 60s invalidated cache), email verification, password recovery, teams +
memberships + invitations, the single role resolver (`IRoleResolver` — Phase 2 does not touch this,
but Phase 3's query compiler will consume it), API keys, rate limiting, and the platform allowlist
enforced as both CORS and redirect-URL validation. This session adds the dynamic database engine:
databases → tables → columns → indexes, synchronous DDL, and the async job queue for the operations
that can't be transactional. The plan is settled — implement, don't re-plan.

Read first, in this order:
1. `docs/handoff/phase-1-report.md` — what exists, where it lives, deviations that affect you
   (particularly: wire ids are 32-char hex via `Ids.Wire`/`TryParseWire`, not dashed UUIDs; the event
   type grammar already in use for auth, e.g. `users.<id>.update.<attr>`, which your schema-change
   events should match for consistency)
2. `docs/roadmap.md` — the Phase 2 scope block and owner-test checklist (your acceptance gate)
3. `docs/architecture.md` §4 (the tables engine — physical naming, row shape, permission roles, DDL
   execution rules) and §11 (threat model — identifiers never from request strings)
4. `docs/research/dotnet-stack.md` — pins + corrections (`NpgsqlCommandBuilder.QuoteIdentifier` is an
   *instance* method, UUIDv7 sort-order proof, pooling/DataSource notes)
5. `docs/research/appwrite-api.md` — TablesDB shapes adopted (status enum, wire key `default` not
   `xdefault`, row system fields), corrected where Praxy diverges

Build exactly the roadmap's Phase 2 scope:

- **Databases**: `POST /v1/databases` → metadata insert + `CREATE SCHEMA px_<ulid>` in one
  transaction. List/get, scoped to project like everything else in the data plane.
- **Tables & columns**: physical naming per architecture.md §4.1 — every user-supplied key maps to a
  generated, regex-validated, quoted physical identifier; the mapping lives in metadata, never gets
  re-derived from the request. Column types: `string(size)`, `integer`, `float`, `boolean`,
  `datetime`, `email`, `url`, `ip`, `enum` (+ array variants). **Relationships are deferred to v1.1 —
  do not build them.** Row byte budget computed and enforced at definition time with named-offender
  errors.
- **Sync DDL in-transaction**: create/drop table, add/drop column → `available` immediately, same
  transaction as the metadata write.
- **Async DDL via `schema_jobs`**: `CREATE INDEX CONCURRENTLY`, type changes that rewrite. Hosted
  `SchemaJobRunner`, `FOR UPDATE SKIP LOCKED`, serialized per database. `processing` → `available` or
  `failed` + captured error. Every DDL connection sets `lock_timeout` (5s) and `statement_timeout`.
  Destructive changes require `force=true`. **This async-job UX (elapsed time, cancel, retry) is the
  single biggest thing to get right — it's the roadmap's named "beat Appwrite" feature.**
- **Indexes**: `key`, `unique`, `fulltext` (generated `tsvector` + GIN).
- **Table permissions storage + `rowSecurity` flag**, default **off**; tables default **deny-all**
  with a console banner. Storage only this phase — enforcement at the query layer is Phase 3.
- **Row `_id` = UUIDv7** (`Guid.CreateVersion7()`, already validated safe for Postgres' bytewise `uuid`
  comparison — see the research doc).

**Console** — databases list, table sub-sidebar (the second-sidebar pattern from
console-design.md — table switching is the highest-frequency nav in the whole console), the
`<DataGrid />` primitive (TanStack Table + Virtual, built once, reused for columns/indexes/rows in
this and later phases), Columns screen with type icons + status badges + elapsed/cancel/retry on
processing jobs, column create/edit side sheet with a "create more" toggle, Indexes screen + create
sheet (reachable from the column grid's "Indexed" checkbox), table settings: permission matrix with
presets (Public read / Owner only / Team access), row-security switch, danger zone.

Constraints that hold: conventional commits, small and topical; never commit `.env`; Testcontainers
integration tests (`postgres:17-alpine`, shared collection fixture — harness in
`tests/Praxy.Tests.Integration/Infrastructure/`); new error `type` strings registered in
`ErrorTypes.All` (the snake_case lint test enforces the format); catalog changes via a new EF
migration (never edit `InitialCatalog` or `AuthTables`); **identifiers never from request strings** —
metadata lookup, regex validation, `QuoteIdentifier` at emit, belt and braces; deny by default.

I have full permission for package installs and edits inside this repo. Use subagents where useful.

When done: run the roadmap's Phase 2 owner test yourself (create database → create table → add one
column of every type → add a unique index and watch `processing → available` with elapsed time → add
a fulltext index → try dropping a required column without `force` (clear error) → rename a column
(instant, metadata-only) → set permissions via preset → delete table (typed-name confirm)), then
follow the handoff protocol at the bottom of `docs/roadmap.md`: write
`docs/handoff/phase-2-report.md` and `docs/handoff/phase-3-prompt.md`, update CLAUDE.md's Commands
section if it changed, and print the Phase 3 prompt.
