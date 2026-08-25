# Praxy

A self-hosted backend-as-a-service. Authentication, a dynamic database where a user-defined table is a real
PostgreSQL table, realtime subscriptions, functions, webhooks, messaging, and self-hosted Next.js site
hosting with push-to-deploy — with an admin console, a Flutter SDK, and a Next.js/React SDK.

**Status: v0.1.0 shipped, actively extended since.** v0.1.0 closed the numbered roadmap: instance claim,
full app-user auth (email+password, Google OAuth, teams, API keys, rate limiting), the dynamic schema
engine (databases → tables → columns → indexes, synchronous DDL, an async job runner with real
cancel/retry, table-level permission storage), the data plane (row CRUD, the 24-method query DSL, keyset
pagination, table- and row-level permission filtering, a catalog cache, and an outbox), realtime (a
WebSocket endpoint, message-mode protocol, permission-filtered fan-out, and a console inspector),
webhooks (an outbox-consuming dispatcher and delivery worker, HMAC-SHA256 signed deliveries with
full-jitter retry/backoff and auto-disable, an SSRF guard, and a console delivery log with redeliver),
functions (a Docker executor for Dart/Node, deployments with build logs, a warm container pool, sync and
async invocations with stored results, event- and cron-triggered execution, encrypted-at-rest env vars,
scoped user JWTs for calling back into the data plane, and a full console), messaging (per-project email
providers, topics/subscribers, send-to-topic and send-to-users with per-target delivery status, and a
project-overridable template system Praxy's own auth emails render through), and hardening (org-level
quotas enforced and surfaced, an unambiguous audit trail, a proven backup/restore and upgrade path, load
tests at 1k schemas / 10k WebSocket connections / query-compiler fuzzing, and a security pass that found
and fixed five real bugs — see [docs/handoff/phase-9-report.md](docs/handoff/phase-9-report.md)).

**Since v0.1.0**: a native **Flutter/Dart SDK** (`praxy_core`/`praxy_flutter`/`praxy_codegen`,
secure-storage sessions, Google OAuth, a real `Stream`-based realtime client with `liveList`, an example
app, real package docs); a **Next.js/React SDK** (`@praxy/core`/`@praxy/react`/`@praxy/nextjs`/
`@praxy/codegen`, session-cookie bridge for SSR, the same realtime/query surface as Flutter's); and
**Sites** — self-hosted Next.js app hosting, built in four phases: subdomain-per-site hosting with
on-demand TLS, preview URLs with graceful blue-green redeploys, custom domains, and GitHub push-to-deploy
(a shared `Praxy.Vcs` project — GitHub App auth, webhook verification — that Functions now reuses too, so
a function can push-to-deploy the same way a site can, one GitHub App covering both). Full history:
[docs/roadmap.md](docs/roadmap.md)'s "Sites" section and [docs/handoff/](docs/handoff/)'s per-phase
reports.

## Stack

| Layer | Choice |
|---|---|
| API | .NET 10 · ASP.NET Core |
| Store | PostgreSQL 16+ |
| Console | Vite · React · TypeScript |
| SDKs | Flutter / Dart · Next.js / React (TypeScript) |
| Sites | Self-hosted Next.js apps, Docker-built, Caddy on-demand TLS |
| Deploy | Docker Compose |

## Repository layout

```
src/          .NET solution — API, domain, persistence, engines (including Sites and Praxy.Vcs,
              the shared git-integration project)
console/      Vite admin console
sdk/flutter/  Dart client package
sdk/js/       Next.js/React client SDK (npm workspace: core, react, nextjs, codegen)
deploy/       Docker Compose and configuration
docs/         Architecture, phase plan, decisions
tests/        Unit, integration, and load tests
```

## Documentation

- [docs/roadmap.md](docs/roadmap.md) — phase breakdown, acceptance gates, handoff protocol
- [docs/architecture.md](docs/architecture.md) — system design, data model, threat model
- [docs/self-host.md](docs/self-host.md) — operator's guide: configuration, Sites' wildcard DNS/TLS,
  git integration setup, backup/restore, upgrades
- [docs/api-reference.md](docs/api-reference.md) — how the OpenAPI reference ships for production
- [docs/functions-runtimes.md](docs/functions-runtimes.md) — what your function code must look like, per runtime
- [sdk/flutter/README.md](sdk/flutter/README.md) — Flutter SDK overview and quick start
- [sdk/js/README.md](sdk/js/README.md) — Next.js/React SDK overview and quick start
- [docs/research/](docs/research/) — research distillations backing the decisions (including
  [praxy-sites.md](docs/research/praxy-sites.md), Sites' own architecture doc)
- [docs/handoff/](docs/handoff/) — per-phase session prompts and completion reports

## Quick start (self-host)

```
cd deploy && ./up.sh
```

First run asks one question — a public domain, or blank for local/plain-HTTP — then handles the
rest: installs Docker if it's missing, generates `deploy/.env` with fresh secrets, and (if you gave
a domain) brings up automatic HTTPS via Caddy and locks down the firewall. Open the console
(`http://localhost:8080`, or `https://your.domain.com`) and claim the instance — the first account
becomes the owner and sign-up closes. Hosting a site needs one more DNS record (a wildcard, for Sites'
on-demand subdomains) and, if you want push-to-deploy, your own GitHub App — both covered in
[docs/self-host.md](docs/self-host.md), which is the full guide.

## Development

Prerequisites: .NET 10 SDK, Docker (also needed at runtime — Functions and Sites build/run containers
via the Docker daemon), Node 20+ (Flutter for SDK work only).

```
docker run -d --name praxy-dev-pg -e POSTGRES_USER=praxy -e POSTGRES_PASSWORD=praxy \
  -e POSTGRES_DB=praxy -p 5432:5432 postgres:17-alpine   # dev database
dotnet run --project src/Praxy.Api                       # API on :5090 (Scalar at /scalar/v1)
npm run dev --prefix console                              # console on :5173, /v1 proxied
dotnet test                                               # unit + Testcontainers integration

cd sdk/flutter && dart pub get                            # Flutter SDK — native pub workspace
dart test praxy_core praxy_codegen && flutter test praxy_flutter example

npm ci --prefix sdk/js && npm run build --prefix sdk/js   # Next.js/React SDK — npm workspace
npm run test --prefix sdk/js && npm run typecheck --prefix sdk/js
```
