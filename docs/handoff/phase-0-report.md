# Phase 0 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run against the containerized
stack by the implementing session. 59 tests green (38 unit, 21 integration).

## What shipped

**Solution** — `Praxy.sln` with `src/` (`Praxy.Api`, `Praxy.Core`, `Praxy.Persistence`, `Praxy.Auth`,
`Praxy.Tables`, `Praxy.Realtime`, `Praxy.Events`) and `tests/` (`Praxy.Tests.Unit`,
`Praxy.Tests.Integration`). All package versions pinned per `docs/research/dotnet-stack.md`; a local
tool manifest pins `dotnet-ef` 10.0.11. `Praxy.Tables`/`Praxy.Realtime` are intentionally empty
placeholders — no work pulled forward.

**Catalog v1** (`praxy` schema, EF Core 10, snake_case, `InitialCatalog` migration):
`organizations` (with `limits jsonb` for Phase 9), `organization_members`, `projects`, `platforms`,
`api_keys`, `users`, `sessions` (with `mfa_verified` from day one), `schema_jobs`, `events` (outbox),
`audit_log`. Users/sessions are project-scoped; `(project_id, email)` unique. Startup migration runs
under session-level `pg_advisory_lock` (key `0x5052415859`) on one dedicated connection, then seeds
the reserved `console` project row idempotently (org-less — invisible to the projects API by
construction).

**Middleware** — `X-Praxy-Request-Id` (server-generated, response header + error bodies, Serilog
enriched), error envelope `{message, code, type, version, requestId, fields?}` for every thrown
error including unmatched `/v1` routes, Serilog two-stage bootstrap, OpenAPI + Scalar mapped
dev-only (verified 404 in the production container), `/v1/health`.

**Instance claim** — `POST /v1/console/claim`: first account wins (concurrent claims serialized by a
transaction-scoped advisory lock), silently creates the "Personal" org + owner membership + session
atomically; 409 `instance_already_claimed` forever after (API-enforced; UI also hides the form).
With `PRAXY_PUBLIC_URL` set, claiming requires the setup token logged at startup (constant-time
compared, regenerated each restart while unclaimed). Sessions: Argon2id (Konscious, OWASP
m=19456/t=2/p=1, PHC strings, behind `IPasswordHasher`), opaque `<sessionId>.<secret>` tokens,
SHA-256 at rest, constant-time compare, `praxy_session_console` cookie (httpOnly/Lax/Secure-on-https)
or `X-Praxy-Session` header. Login burns a dummy hash when the account doesn't exist (timing).

**Console guard** — data-plane endpoints run behind a `ProjectGuardFilter` that resolves
`X-Praxy-Project` and refuses `console` with 403 `project_reserved` (case-insensitive). Integration
tests cover the HTTP guard, and a DB `CHECK` constraint (`ck_api_keys_project_not_console`) makes
console API keys impossible even if application guards slip — also integration-tested.

**Projects API** — create (custom id validated `^[a-z0-9][a-z0-9-]{0,35}$`, `console` reserved,
duplicates 409; generated ids are hex UUIDv7, time-ordered), list, get — all scoped to the
operator's orgs. `GET /v1/ping` stamps `last_ping_at`. `GET /v1/console/capabilities` reports
`{version, claimed, setupTokenRequired, features{auth…webhooks: all false}}`, unauthenticated.

**Console** (Vite 8 + React 19.2 + TS 5.9.3 + Tailwind v4 + TanStack Router/Query + cmdk, served at
`/console`): claim/login screen (claim form only while unclaimed; already-authed visitors bounce off
`/login`), chrome-less first-project card, project list cards with copyable id chips + ping dots,
project overview with "waiting for first ping" state that polls every 3 s and flips to Connected
automatically, ⌘K palette with `g p`/`g o` chords. Own dark design language (ink/iris tokens in
`console/src/styles.css`); all user-facing nouns in `console/src/strings.ts`.

**Deploy** — `deploy/up.sh` generates `.env` (Postgres password, port, `PRAXY_PUBLIC_URL` slot) on
first run then execs `docker compose up`; compose fails loudly with instructions if `.env` is
missing. Multi-stage Dockerfile (node 24 console build → dotnet publish → aspnet runtime with the
console in `wwwroot/console`). Persistent named volume; api waits on Postgres health.

## Deviations & notes

- **Operator/user ids are `uuid` (UUIDv7), project ids are text.** Custom ids for *app users*
  (Appwrite allows them) were not needed for operators; if Phase 1 wants client-supplied user ids,
  that's a column-type decision to make then.
- **`SchemaJob.database_id` has no FK** — the `databases` table lands in Phase 2; add the FK there.
- **Ping is `GET /v1/ping`** (header-authenticated by project id alone, no key). It's the minimal
  Phase 0 data-plane endpoint proving the guard + onboarding flow; API-key auth arrives Phase 1.
- **Login/claim responses include the session token in the body** alongside the cookie — needed by
  tests today, by SDKs (`X-Praxy-Session`) later.
- **Pin drift:** `@types/react-dom` 19.2.9 doesn't exist on npm; used 19.2.4 (+ `@types/react`
  19.2.18). `Microsoft.EntityFrameworkCore.Relational` pinned explicitly at 10.0.11 to silence the
  10.0.4 skew the research doc predicted. Testcontainers' parameterless `PostgreSqlBuilder()` is
  obsolete in 4.14; the image tag goes through the constructor.
- **One real bug found by the container walkthrough:** `WebApplication` auto-prepends `UseRouting`,
  so the `/console/{*path}` SPA fallback matched before `UseStaticFiles` and every asset came back
  as `text/html`. Fixed with an explicit `app.UseRouting()` after static files (comment in
  `Program.cs` explains).

## Known gaps (deliberate)

- No rate limiting yet (Phase 1 per roadmap). No CORS/platform enforcement (Phase 1).
- No forwarded-headers handling — behind a TLS proxy the cookie won't be marked `Secure`; address
  when `PRAXY_PUBLIC_URL` deployments get documented (Phase 9 hardening at latest).
- Session cache (60 s) and per-user session caps are Phase 1 scope.
- `console/src/screens/ProjectListPage.tsx` create-modal is minimal (no focus trap).

## Commands

```
cd deploy && ./up.sh                      # self-host stack → http://localhost:8080/console
dotnet test                               # 59 tests; Docker required (Testcontainers)
dotnet run --project src/Praxy.Api       # dev API :5090 (Scalar at /scalar/v1)
npm run dev --prefix console             # dev console :5173, /v1 proxied to :5090
```

Dev API expects Postgres `praxy/praxy/praxy` on localhost:5432
(`docker run -d -e POSTGRES_USER=praxy -e POSTGRES_PASSWORD=praxy -e POSTGRES_DB=praxy -p 5432:5432 postgres:17-alpine`).

## Owner-test checklist (run by this session, all passing)

1. `./up.sh` → stack up, `.env` generated. 2. Claim at `/console` → lands on first-project card.
3. Create project → overview "waiting for first ping" → `curl /v1/ping -H "X-Praxy-Project: <id>"` →
flips to Connected without reload. 4. Project listed with copyable id. 5. Sign out → login form
(no claim form) → sign in. 6. Scalar loads in dev; 404s in the container. 7. `docker compose down &&
up` → still signed in, project intact. 8. Bonus: ping with `X-Praxy-Project: console` → 403
`project_reserved`.
