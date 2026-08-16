# Praxy

A self-hosted backend-as-a-service. Authentication, a dynamic database where a user-defined table is a real
PostgreSQL table, realtime subscriptions, functions, webhooks and messaging — with an admin console and a
Flutter SDK.

**Status:** Phase 7 complete — solution skeleton, system catalog, instance claim, projects API, full
app-user auth (email+password, Google OAuth, teams, API keys, rate limiting), the dynamic schema engine
(databases → tables → columns → indexes, synchronous DDL, an async job runner with real cancel/retry,
table-level permission storage), the data plane (row CRUD, the 24-method query DSL, keyset
pagination, table- and row-level permission filtering, a catalog cache, and an outbox), realtime
(a WebSocket endpoint, message-mode protocol, permission-filtered fan-out, and a console inspector),
a native Flutter/Dart SDK (`praxy_core`/`praxy_flutter`/`praxy_codegen`, secure-storage sessions,
Google OAuth, a real `Stream`-based realtime client with `liveList`, an example app), webhooks
(an outbox-consuming dispatcher and delivery worker, HMAC-SHA256 signed deliveries with full-jitter
retry/backoff and auto-disable, a connect-time SSRF guard, and a console delivery log with redeliver),
and functions (a Docker executor for Dart/Node, deployments with build logs, a warm container pool,
sync and async invocations with stored results, event- and cron-triggered execution, encrypted-at-rest
env vars, scoped user JWTs for calling back into the data plane, and a full console: functions,
deployments, executions, settings).
Phase 8 (Messaging) is next; see [docs/handoff/](docs/handoff/).

## Stack

| Layer | Choice |
|---|---|
| API | .NET 10 · ASP.NET Core |
| Store | PostgreSQL 16+ |
| Console | Vite · React · TypeScript |
| SDK | Flutter / Dart (first client) |
| Deploy | Docker Compose |

## Repository layout

```
src/          .NET solution — API, domain, persistence, engines
console/      Vite admin console
sdk/flutter/  Dart client package
deploy/       Docker Compose and configuration
docs/         Architecture, phase plan, decisions
tests/        Unit and integration tests
```

## Documentation

- [docs/roadmap.md](docs/roadmap.md) — phase breakdown, acceptance gates, handoff protocol
- [docs/architecture.md](docs/architecture.md) — system design, data model, threat model
- [docs/research/](docs/research/) — research distillations backing the decisions
- [docs/handoff/](docs/handoff/) — per-phase session prompts and completion reports

## Quick start (self-host)

```
cd deploy && ./up.sh
```

First run generates `deploy/.env` with fresh secrets, builds the image, and starts Postgres + API.
Open http://localhost:8080/console and claim the instance — the first account becomes the owner and
sign-up closes. Set `PRAXY_PUBLIC_URL` in `deploy/.env` if the instance is reachable from the
internet; claiming then requires the setup token printed to the api container logs.

## Development

Prerequisites: .NET 10 SDK, Docker, Node 20+ (Flutter for SDK work only).

```
docker run -d --name praxy-dev-pg -e POSTGRES_USER=praxy -e POSTGRES_PASSWORD=praxy \
  -e POSTGRES_DB=praxy -p 5432:5432 postgres:17-alpine   # dev database
dotnet run --project src/Praxy.Api                       # API on :5090 (Scalar at /scalar/v1)
npm run dev --prefix console                             # console on :5173, /v1 proxied
dotnet test                                              # unit + Testcontainers integration

cd sdk/flutter && dart pub get                           # Flutter SDK — native pub workspace
dart test praxy_core praxy_codegen && flutter test praxy_flutter example
```
