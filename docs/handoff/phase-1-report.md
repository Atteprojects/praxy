# Phase 1 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run against the dev stack by
the implementing session. 139 tests green (78 unit, 61 integration).

## What shipped

**Catalog** — new EF migration `AuthTables` (never touches `InitialCatalog`): `teams`, `memberships`
(roles `text[]`, `confirmed`, invite `secret_hash`), `tokens` (verification/recovery/oauth2, hashed,
expiring), `identities` (project-scoped `(provider, provider_uid)` unique, encrypted access token).

**App-user auth** (`src/Praxy.Auth/AppAuthService.cs`) — email+password signup with session
auto-created on signup, login, logout, account updates (name/password/prefs). Reuses Phase 0's
`IPasswordHasher` and `SessionTokens`. Per-project settings (`ProjectAuthSettings`, stored in
`projects.settings.auth` jsonb) drive method toggles, session cap (default 10, configurable ≤100),
and password policy — no migration needed to add a setting later. Session cap eviction is
oldest-first and publishes `sessions.delete` for each eviction.

**Session cache** — `InMemorySessionCache`, 60s TTL, invalidated via `IEventBus` subscription on
`users.<id>.sessions.<id>.delete` and `users.<id>.update|delete` (so blocking/deleting a user drops
every cached session for them, not just the one addressed by id). Revocation is instant despite the
TTL because invalidation is push, not poll.

**Tokens** — one converging exchange point, `POST /v1/account/sessions/token` (`{userId, secret}`),
used by OAuth today and reserved for magic-url/OTP later. Verification and recovery emails carry a
`?userId=&secret=` link; recovery success revokes every session for the account. `IEmailSender` has
an `SmtpEmailSender` and a `LoggingEmailSender` fallback (writes the full message, including the
link, to the server log) so an unconfigured instance never silently drops a verification email.

**GitHub OAuth** (`src/Praxy.Auth/OAuth/`) — `IOAuthProvider` abstraction (`GitHubOAuthProvider` is
the only implementation; Google slots in with no endpoint changes) behind `OAuthService`: browser
start (`GET /v1/account/sessions/oauth2/{provider}`) → provider consent → callback
(`GET .../callback/{provider}/{projectId}`) → redirect to the caller's success/failure URL carrying
`userId` + a 60s HS256-JWT-wrapped one-time secret → exchanged at the same
`POST /v1/account/sessions/token`. PKCE (S256) end to end. All OAuth/verification/recovery redirect
URLs are validated against the project's platform allowlist (`PlatformAllowlist`) before use —
security-critical per architecture.md §11, integration-tested. State + PKCE verifier ride a signed,
httpOnly cookie scoped to `/v1/account/sessions/oauth2`, never the URL.

**Teams + memberships** (`TeamsService`) — Appwrite semantics: a session call to
`POST /v1/teams/{id}/memberships` emails an invitation (unconfirmed membership + redirect-URL-gated
email), a key/console call adds directly (confirmed, no email). Acceptance
(`PATCH .../memberships/{id}/status {userId, secret}`) is secret-authenticated, not
session-authenticated, and auto-creates a session per spec. Identifier precedence is `userId` over
`email`; inviting an unknown email creates a passwordless, unverified user; accepting an invite
verifies the email (proof of mailbox control).

**Role resolver** (`RoleResolver` implementing `IRoleResolver`) — the one implementation the roadmap
asked for: `any`/`guests` for guests, `any`/`users`[`/verified`]/`user:<id>`[`/verified`]/
`member:<id>`/`team:<id>`/`team:<id>/<role>`/`label:<x>` for app-user sessions, `any` only for API
keys (they authorize via scopes, not roles). Resolved once per request, cached on `HttpContext.Items`
via `RequestRoles.GetAsync`, exposed at `GET /v1/account/roles` for debugging. Phase 3's query
compiler and Phase 4's realtime fan-out must call this same resolver — do not fork it.

**API keys** (`ApiKeyService`) — `<keyId>.<secret>` in `X-Praxy-Key`, SHA-256 at rest like sessions,
scoped (`users.read`, `users.write`, `teams.read`, `teams.write` today), `last_used_at` stamped at
≤60s resolution. Refuses the `console` project at the service level (belt) on top of Phase 0's DB
check constraint (braces). Missing-scope calls are a distinct `general_unauthorized_scope` 401, not
the generic unauthorized, so SDKs can tell "no key" from "wrong key" apart.

**Rate limiting** — `RejectionStatusCode = 429` (the framework default is 503, called out in
dotnet-stack.md), `Retry-After` + `RateLimit-Limit/Remaining/Reset` on every rejection, two policies
(`auth`: 10/min, `auth-email`: 5/10min, both configurable via `Praxy:RateLimits:*`), partitioned on
project-id-or-query-param then IP so one tenant's lockout never touches another's budget.

**Platform allowlist as CORS** (`PlatformCorsMiddleware`) — cross-origin data-plane calls need an
`Origin` whose host matches a registered platform (`*.` wildcard supported); anything else is 403
`general_unknown_origin`. Preflights are answered permissively (no project header on an OPTIONS
request) and enforcement happens on the real request. Console traffic is same-origin by construction
and untouched by this middleware.

**Console** (gated on `capabilities.features.auth`, now `true`) — project sidebar grows Auth
(Users/Teams/Auth settings) and Manage (API keys/Platforms) sections; `g u/t/s/k` command-palette
chords added. Users: searchable table + create modal, detail page with Overview (profile, labels,
block/delete) / Sessions (revoke one or all) / Memberships tabs. Teams: list + create, detail page
with direct add-member and remove. Auth settings: method toggles, GitHub credential fields (secret is
write-only — server never echoes it back, confirmed by a dedicated test), session limit, password
policy. API keys: scope-checkbox create, reveal-once secret modal, revoke. Platforms: add/remove with
the security context spelled out in the copy.

## Deviations & notes

- **`Praxy.Auth` now depends on `Praxy.Events`** (it didn't in Phase 0) — the session cache and every
  write path publish through `IEventBus`. Listed in case a later phase assumed the old dependency
  graph.
- **Instance-wide secret** (`InstanceKey`, sourced from `PRAXY_SECRET_KEY`) signs the OAuth
  state/callback JWTs and AES-256-GCM-encrypts provider access tokens at rest. `deploy/up.sh` now
  generates it into `.env` alongside the Postgres password. If unset (bare `dotnet run`), an ephemeral
  key is generated with a loud startup warning — fine for a dev box, fatal for OAuth continuity across
  restarts in production, which is why `up.sh` always sets it.
- **Wire user/session/team/etc. ids are 32-char lowercase hex** (`Ids.Wire`/`Ids.TryParseWire`), not
  UUIDv7 strings with dashes — matches the existing `Ids.NewResourceId()` project-id convention rather
  than inventing a second format. `sessions.delete` event types embed this form
  (`users.<hex>.sessions.<hex>.delete`), which Phase 4's realtime layer should reuse verbatim.
  Accepts dashed input too so hand-typed curl calls survive.
- **`AppPrincipalFilter` degrades a bad session cookie to guest rather than 401ing.** An explicit
  `X-Praxy-Key`/`X-Praxy-Session` *header* with a bad value is a hard 401 (the caller clearly meant to
  authenticate); a stale cookie on an otherwise-public GET should not break that endpoint. Session- and
  key-requiring endpoints still 401 correctly via `RequireUser`/`RequireScope`.
- **Server-side `/v1/users` (API-key scoped) and `/v1/teams` (session-or-key) are separate surfaces**
  from `/v1/console/projects/{id}/users|teams` (operator-session scoped). They share the DTOs
  (`AuthDtos.cs`) and most service-layer logic but intentionally different authorization: this is what
  the roadmap's "wrong-scope API key → 401" test needs, and it mirrors Appwrite's client/server split.
- **The GitHub provider is exercised in integration tests via a fake `IOAuthProvider`**
  (`FakeOAuthProvider` in `AuthTestBase`), not real network calls — no GitHub app was provisioned for
  this session. The provider abstraction, PKCE plumbing, JWT-wrapped secret, and state-cookie
  round-trip are all exercised for real; only the literal HTTP call to `github.com` is stubbed.
- **CORS is hand-rolled** (`PlatformCorsMiddleware`) instead of `Microsoft.AspNetCore.Cors`, because
  the allowlist is per-project and DB-backed — the built-in middleware's policy model assumes static,
  app-wide origin lists.
- **Rate limiter partitions include the project id even for the OAuth browser-navigated endpoints**,
  which take `project` as a query param rather than a header (browsers can't be told to set custom
  headers on a top-level navigation).

## Known gaps (deliberate, next phases or later)

- Magic URL and email OTP are not implemented — the roadmap narrowed Phase 1 to email+password +
  GitHub OAuth only; the token-exchange endpoint and `tokens` table are already shaped to add them
  without a schema change.
- JWT minting (`POST /account/jwts`) for server-to-server calls is Phase 7 (functions) per the
  roadmap; not built here.
- No per-project Postgres role / `SET LOCAL ROLE` defense-in-depth — that's the v1.1 item noted in
  architecture.md §11, unrelated to this phase.
- Membership role strings are free-form (validated for shape, not against a fixed vocabulary) — matches
  Appwrite's model; no closed enum exists to validate against.
- The console's team member "roles" input is a bare comma-separated text field, not a role picker —
  acceptable for Phase 1's scope; a nicer picker is a console-polish item, not blocking.

## Commands

```
docker run -d --name praxy-dev-pg -e POSTGRES_USER=praxy -e POSTGRES_PASSWORD=praxy \
  -e POSTGRES_DB=praxy -p 5432:5432 postgres:17-alpine   # dev database
dotnet run --project src/Praxy.Api                       # API :5090 (Scalar at /scalar/v1)
npm run dev --prefix console                              # console :5173, /v1 proxied to :5090
dotnet test                                                # 139 tests; Docker required (Testcontainers)
cd deploy && ./up.sh                                       # self-host stack → http://localhost:8080/console
```

## Owner-test checklist (run by this session, all passing)

1. Claimed instance, created project "Acme" in console.
2. Console → Users → Create user (`ada@example.com` / password) → landed on the user detail page.
3. `curl -X POST /v1/account/sessions/email -H "X-Praxy-Project: <id>" -d '{"email":...,"password":...}'`
   → `201` with a session token.
4. Console → user detail → Sessions tab → the curl-created session is listed (provider `email`,
   client `curl/8.7.1`).
5. Clicked Revoke → row disappears; a fresh curl session was independently confirmed dead
   (`GET /v1/account` → `401 general_unauthorized`) after console revocation.
6. Console → Teams → Create team "Engineering" → Add member `ada@example.com` with role `owner` →
   confirmed immediately (console/server semantics).
7. `curl /v1/account/roles` as Ada → roles include `team:<id>` and `team:<id>/owner`.
   (The client-side invite-email → accept path is separately integration-tested end to end in
   `TeamsTests.cs`.)
8. Console → API keys → Create key scoped to `users.read` only → reveal-once secret shown, then
   hidden on next list load. `curl` with that key: `GET /v1/users` → `200`;
   `POST /v1/users` → `401 general_unauthorized_scope`.
9. 11th-session eviction verified by `AppAuthTests.Eleventh_session_evicts_the_first` (integration
   test, passing) — the first of 11 created sessions is dead, exactly 10 remain, the newest still
   works.
10. Console → Auth settings → enabled GitHub OAuth, saved client id/secret → secret field flips to
    "STORED — enter a value to replace" and is never echoed back by the API.
11. Console → Platforms → added `app.example.com` as a web platform → confirms the CORS/redirect
    allowlist entry exists (exercised end to end by `RateLimitAndCorsTests.cs` and
    `OAuthFlowTests.cs`).
