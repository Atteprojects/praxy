# Phase 1 — session prompt

> **Correction (post-Phase-2):** where this prompt says "GitHub OAuth" for app users below, the actual
> corrected scope is **Google OAuth** for app users — GitHub was meant for platform/console operators
> (deferred to future multitenancy work), per owner clarification after this phase shipped. See
> `docs/roadmap.md` and `docs/architecture.md` for current truth.

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 1 (Auth) of Praxy**, a self-hosted BaaS (.NET 10 API + PostgreSQL +
Vite/React console). Phase 0 shipped the solution skeleton, `praxy` catalog, instance claim, console
operator sessions, projects API, the reserved-`console`-project guard, and the console shell served at
`/console`. This session adds app-user authentication end to end. The plan is settled — implement, don't
re-plan.

Read first, in this order:
1. `docs/handoff/phase-0-report.md` — what exists, where it lives, deviations that affect you
2. `docs/roadmap.md` — the Phase 1 scope block and owner-test checklist (your acceptance gate)
3. `docs/architecture.md` §5 (auth), §11 (threat model)
4. `docs/research/dotnet-stack.md` — pins + corrections (rate limiter 429, Argon2 params, Konscious)
5. `docs/research/appwrite-api.md` — token→session exchange, OAuth token flow + PKCE, team invite
   semantics, error-type vocabulary

Build exactly the roadmap's Phase 1 scope:

- **App users** (project-scoped, distinct from console operators): email+password signup with
  **session auto-created on signup**, login, logout, account endpoints. Reuse `IPasswordHasher`
  (Argon2id) and the session-token scheme from Phase 0 (`src/Praxy.Auth`); app sessions get
  `praxy_session_<projectId>` cookies or `X-Praxy-Session`. Per-user session cap 10, oldest evicted.
  60s in-memory session cache invalidated via `IEventBus`; session deletion publishes
  `sessions.delete` (cache honors it now, realtime consumes it Phase 4).
- **GitHub OAuth only** — token flow (callback carries userId + 60s-JWT-wrapped secret, exchanged at
  `POST /v1/account/sessions/token`) + PKCE, behind a provider abstraction so Google slots in later
  without API changes. **Redirect URLs validate against the platform allowlist** (security-critical).
- **Email verification + password recovery** via a small SMTP sender (host/port/user/pass/from
  config). Tokens hashed at rest.
- **Teams + memberships + invitations** (Appwrite semantics: client call emails an invite, server
  call adds directly, acceptance auto-creates a session).
- **Role resolution**: one resolver producing the caller's `string[]` roles (`any`, `guests`,
  `users`, `users/verified`, `user:<id>`, `user:<id>/verified`, `team:<id>`, `team:<id>/<role>`,
  `member:<id>`, `label:<x>`), cached on the request context, with a debug endpoint. This single
  implementation later feeds the query compiler and realtime fan-out — build it to be consumed twice.
- **API keys**: hashed at rest, scoped, `last_used_at`, refuse project `console` (the DB check
  constraint already exists — keep the service-level guard too). Platform allowlist enforced as CORS
  origin check.
- **Rate limiting**: built-in limiter, `RejectionStatusCode = 429` (default is 503!), `Retry-After`
  emitted, tight buckets on auth endpoints, partition on project/key before IP.
- **Console screens** (the phase gate — no screens, no done): users table + create user, user detail
  (overview / sessions / memberships tabs), teams + members, auth settings (method toggles, GitHub
  credentials, session limits, password policy), API keys (create/reveal-once/revoke), platforms
  screen with add-platform flow. Gate new nav entries on `capabilities.features.auth`.

Constraints that hold: conventional commits, small and topical; never commit `.env`; Testcontainers
integration tests (`postgres:17-alpine`, shared collection fixture — harness exists in
`tests/Praxy.Tests.Integration/Infrastructure/`); new error `type` strings registered in
`ErrorTypes.All` (the snake_case lint test enforces the format); catalog changes via a new EF
migration (never edit `InitialCatalog`); identifiers never from request strings; deny by default.

I have full permission for package installs and edits inside this repo. Use subagents where useful.

When done: run the roadmap's Phase 1 owner test yourself (create user in console → sign in via
curl/Scalar → session visible on user detail → revoke → 401 → team invite → accept → `team:<id>` in
the roles debug endpoint → wrong-scope API key 401 → 11th session evicts the 1st), then follow the
handoff protocol at the bottom of `docs/roadmap.md`: write `docs/handoff/phase-1-report.md` and
`docs/handoff/phase-2-prompt.md`, update CLAUDE.md's Commands section if it changed, and print the
Phase 2 prompt.
