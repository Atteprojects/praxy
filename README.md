# Praxy

A self-hosted backend-as-a-service. Authentication, a dynamic database where a user-defined table is a real
PostgreSQL table, realtime subscriptions, functions, webhooks and messaging — with an admin console and a
Flutter SDK.

**Status:** planning. Nothing is implemented yet.

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

## Development

Prerequisites: .NET 10 SDK, Docker, Node 20+, Flutter (for SDK work only).

Setup instructions land with Phase 0.
