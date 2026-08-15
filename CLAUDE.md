# Praxy — agent instructions

Self-hosted BaaS: .NET 10 API + PostgreSQL, Vite/React console, Flutter SDK. Built phase-by-phase, one
session per phase.

## Session start — do this first

1. Read `docs/handoff/` — the highest-numbered `phase-N-prompt.md` without a matching `phase-N-report.md` is
   the current phase. Its prompt defines your scope.
2. Read `docs/roadmap.md` for that phase's scope and owner-test checklist.
3. Consult `docs/research/dotnet-stack.md` before adding any package — it holds **machine-verified pins and
   API corrections**. Do not upgrade past the pins or trust memory over that file.

This session implements exactly one phase. Do not re-plan, re-litigate settled decisions, or pull work
forward from later phases.

## Fixed decisions (owner's — never reopen)

- .NET 10 backend · Vite + React console (own modern design; simple Appwrite-like layout) · Flutter SDK first
- PostgreSQL only — no second datastore
- Auth: email+password and **GitHub OAuth only** until the owner says otherwise; minimal options everywhere
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

- Self-host stack: `cd deploy && ./up.sh` (generates `.env` on first run; console at
  `http://localhost:8080/console`)
- Tests: `dotnet test` (integration needs Docker for Testcontainers)
- Dev API: `dotnet run --project src/Praxy.Api` — port 5090, Scalar at `/scalar/v1`; expects local
  Postgres `praxy/praxy/praxy` on 5432 (see README dev section)
- Dev console: `npm run dev --prefix console` — port 5173, proxies `/v1` to 5090
- Console prod build: `npm run build --prefix console` · EF migration: `dotnet ef migrations add <Name>`
  from `src/Praxy.Persistence` (local tool manifest pins dotnet-ef 10.0.11)

## Session end — handoff protocol

Before finishing a phase: tests green, `git status` clean, run the owner-test checklist yourself, then write
`docs/handoff/phase-N-report.md` and `docs/handoff/phase-(N+1)-prompt.md`, update the Commands section above
if it changed, and print the next prompt for the owner. Full protocol: bottom of `docs/roadmap.md`.
