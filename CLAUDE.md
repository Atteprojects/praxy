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

## Session end — handoff protocol

Before finishing a phase: tests green, `git status` clean, run the owner-test checklist yourself, then write
`docs/handoff/phase-N-report.md` and `docs/handoff/phase-(N+1)-prompt.md`, update the Commands section above
if it changed, and print the next prompt for the owner. Full protocol: bottom of `docs/roadmap.md`.
