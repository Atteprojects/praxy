# Session task — Next.js/TypeScript SDK, v1

> **Status: shipped.** Verified 2026-09-03 against the code, not assumed — see `sdk/js/packages/nextjs` (plus `core`/`react`), built and tested in CI.
> No `nextjs-sdk-report.md` was written at the time, so this prompt used to look like
> outstanding work when scanning `docs/handoff/` for prompts without reports.

## Why this exists

Praxy has one SDK today (Flutter). The owner wants a Next.js SDK next, as groundwork for an eventual
Appwrite-Sites-style hosting feature (**not in scope here — see Non-goals**). Read
`docs/research/nextjs-sdk.md` in full before writing any code — it is the actual design spec this package
follows, grounded in real reads of `AccountEndpoints.cs`, `OAuthService.cs`, and `AppPrincipal.cs`, plus a
comparison against Appwrite's own SSR/React-library approach. Two scope decisions the owner already made,
do not re-litigate them: **App Router only** (no Pages Router), and **full v1 parity with the Flutter SDK's
current surface** (Account/Tables/Teams/Functions/Realtime — no narrow-then-grow phase).

Work on a new branch off `main`. Read `CLAUDE.md` first. This is a new initiative, not a numbered phase and
not part of the (now fully closed) post-v0.1.0 gap backlog — do not conflate the two, and do not re-open any
of that backlog's already-merged items.

## Non-goals — do not build these

- **No Sites feature, no hosting/build-pipeline work of any kind.** This SDK lets a Next.js app talk to
  Praxy at runtime. Deploying/hosting *that app* on Praxy's own infrastructure is a separate, much larger,
  not-yet-started initiative. `docs/research/nextjs-sdk.md`'s closing section notes that Sites would likely
  extend `FunctionsService`/`DockerExecutor` later — that's a forward-looking note for whoever plans Sites,
  not an instruction to start it now.
- **No Pages Router support.** `getServerSideProps`, API Routes, and the `pages/` directory are out of
  scope. An app on Pages Router can still use `@praxy/core` directly (it's isomorphic) — just not
  `@praxy/nextjs`'s App-Router-specific helpers.
- **No `MessagingService`.** Verified already, same as the Flutter SDK's own finding:
  `MessagingEndpoints.cs` has exactly one route group, entirely behind `RequireOperatorFilter`. No
  client-facing endpoint exists for an app to subscribe/unsubscribe through. A server gap, not an SDK one.
- **No Storage, avatars, locale, transactions, function-deployment management.** None exist server-side
  (Storage) or are console/operator-only (deployment management) — not this SDK's problem to invent around.
- **No Next.js `fetch` cache / `revalidateTag` integration.** Real value, real complexity (would need a
  cache-tag design mirroring the realtime event grammar) — explicitly deferred past v1, not rushed in.
- **No second framework adapter** (SvelteKit, plain Vite React, etc.). `@praxy/core` and `@praxy/react` are
  built framework-agnostic-where-possible on purpose so this is cheap *later* — do not build a second
  adapter package now.
- **No npm publishing.** Build it, do not publish `@praxy/*` packages to any registry without being asked —
  same posture the Flutter SDK prompt took.

## Scope

1. **`packages/core`** (`@praxy/core`): isomorphic TS client. Transport (`fetch`-based, edge-runtime-safe —
   no Node-only APIs), typed error hierarchy, `Query`/`Col<T>` builders, and five services — Account, Tables,
   Teams, Functions, Realtime — matching the exact wire shapes in `docs/research/nextjs-sdk.md`'s "v1 SDK
   surface" section. This package has zero React or Next.js dependency; it must work from a plain Node
   script.
2. **`packages/react`** (`@praxy/react`): `<PraxyProvider>` + hooks, built on `@tanstack/react-query` (a
   dependency `console/package.json` already has proven out in this codebase — reuse it, do not introduce a
   different cache library). Provider takes an `initialJwt` prop for the server→client auth bridge (see
   Landmines). Realtime hook (`useLiveList` or similar) opens the WebSocket client-side only, authenticated
   with the same JWT the REST calls use.
3. **`packages/nextjs`** (`@praxy/nextjs`): `createServerClient()` and `createApiKeyClient()` factory
   functions (never singletons — see Landmines), the OAuth callback Route Handler
   (`app/auth/callback/route.ts` or equivalent, packaged so a consuming app can re-export it with minimal
   boilerplate), and `praxyMiddleware()` for `middleware.ts` route protection.
4. **`packages/codegen`** (`@praxy/codegen`): typed row interfaces + column-key constants generated from a
   live project's schema, CLI invoked on demand (`npx praxy-codegen ...`), emitting committed `.ts` files —
   not a bundler plugin, not a watcher. Mirrors `praxy_codegen`'s actual scope; no query-builder generation.
5. **`examples/nextjs`**: a real App Router app exercising sign-in (email + Google OAuth), a protected
   Server Component page reading table rows, a Server Action performing a write, and a Client Component
   using realtime — the end-to-end proof this whole SDK is for.
6. **Wire it up as an npm workspace** at `sdk/js/` (root `package.json` with `workspaces`), matching the
   `console/`'s existing npm choice (`package-lock.json`, not yarn/pnpm) so the repo doesn't gain a second JS
   package manager.
7. **A CI job for this SDK**, extending `.github/workflows/ci.yml` (which now has three jobs: API, console,
   Flutter SDK — this becomes the fourth). Build every package, run the test suite, `tsc --noEmit` across
   the workspace.

## Landmines — read before writing code

Verified against current `main`, not recalled. `docs/research/nextjs-sdk.md` has the full detail and code
excerpts behind each of these; this is the condensed "do not get this wrong" version.

- **`createServerClient()` must be a plain async factory, never a cached/module-level singleton.** This is
  the single most-repeated warning in Appwrite's own SSR docs, for good reason: a client built once at
  cold-start and reused across requests is how one request's session leaks into another's response in a
  serverless/edge environment. Every Server Component, Server Action, and Route Handler that needs a client
  calls the factory itself. Do not add a memoization/cache layer "for performance" — there is no safe way to
  cache this across requests.

- **The session cookie name and shape are not yours to invent — they already exist.**
  `AppSessionCookie.Name(projectId)` (`src/Praxy.Api/Infrastructure/AppPrincipal.cs`) is
  `praxy_session_<projectId>`, and the server already reads it as a fallback to the `X-Praxy-Session`
  header (`AppPrincipalFilter`, same file). Set it with the **exact same `CookieOptions` shape** the C#
  `AppSessionCookie.Set()` helper uses: `httpOnly: true, secure: <request was https>, sameSite: 'lax',
  path: '/', expires: <session.expiresAt>`. Getting `sameSite`/`secure` wrong here is a silent
  auth-never-persists bug, not a loud error.

- **Client Components must never receive the real session token — only a JWT.** `AppPrincipalFilter`
  disambiguates a session secret from a JWT by counting dots (a JWT has exactly two:
  `sessionToken.Count(c => c == '.') == 2`). Mint the JWT server-side via `POST /v1/account/jwts`
  (`{durationSeconds?}` → `{jwt}`) inside the same Server Component/Action that would otherwise read the
  session cookie, and pass *that* to `<PraxyProvider initialJwt={...}>`. If you find yourself passing the
  httpOnly cookie's value to a Client Component in any form, stop — that defeats the entire point of
  `httpOnly`.

- **The OAuth success URL carries `userId`/`secret` in the query string, not a ready session.**
  Verified in `OAuthService.cs`: the callback redirects to `<successUrl>?userId=<id>&secret=<opaque>`. Your
  Route Handler at that URL must call `POST /v1/account/sessions/token` with `{userId, secret}`
  (`TokenExchangeRequest`) to get a real session (`CreatedSessionResponse: {user, session, token}`) *before*
  setting the cookie. PKCE and the state cookie are already handled server-side in `OAuthStart`/
  `OAuthCallback` — nothing to add on the client side beyond supplying `success`/`failure` URLs when starting
  the flow.

- **`praxyMiddleware()` runs on Edge Runtime by default.** `@praxy/core` must stay `fetch`-only with no
  Node-specific APIs (no `node:crypto`, no `node:buffer` assumptions) or the middleware package will fail to
  build/deploy on platforms that enforce the Edge Runtime constraint. Test this explicitly — a Node-only API
  creeping into a shared code path is easy to miss locally and only surfaces at deploy time.

- **`Query`/`Col<T>` should be a value type, not a pre-encoded string** — same reasoning
  `docs/research/flutter-sdk.md` gives for the Dart SDK (`Query.equal()` returning a JSON string forces
  `Query.or()` to re-decode its own inputs). Port the value-object shape, don't reinvent it.

- **No `upsert` on tables.** Same real API gap `TablesService`'s own doc comment documents for the Flutter
  SDK — not this SDK's problem to paper over with a client-side read-then-write.

## Tests

`packages/core/test/` and `packages/react/test/` — a fake-transport test double (mirrors
`sdk/flutter/praxy_core/test/support/fake_transport.dart`'s role), `vitest` as the runner (fast, no server
needed, standard for this kind of package). For each service method: request path/method/body sent
correctly, response decoded correctly, and at least one error-mapping case. `packages/nextjs` needs
integration-style tests for the cookie-setting shape and the OAuth Route Handler's query-param parsing —
these don't need a running Next.js server, just the handler functions invoked directly with mock
`Request`/`cookies()` objects.

## Done means

- Full test suite green across the workspace (`npm test` or equivalent from `sdk/js/`), `tsc --noEmit`
  clean.
- **A real, verified-green GitHub Actions run for the new CI job** — same requirement items #4 and #6 had
  for their own new CI jobs. Push the branch, open the PR, confirm all four jobs (API, console, Flutter SDK,
  this one) pass. `main` requires a passing PR with the two/three currently-required checks before merge
  (`enforce_admins: true` — applies to you too); push a branch and open a PR, do not merge yourself, and
  mention in your final summary that the owner may want to add the new check to the required list once it's
  reported at least once (same procedure used for the Flutter SDK job).
- `git status` clean, conventional commits, on a new branch off `main`.
- READMEs for each package (`sdk/js/README.md` and one per package), matching the Flutter SDK's real-docs
  convention.
- The example app runs against a real local Praxy instance and demonstrably completes: email sign-in, one
  Server Component reading rows, one Server Action writing a row, one Client Component receiving a realtime
  update. Screenshot or terminal transcript in your final summary — "the types compile" is not the same as
  "the auth flow actually round-trips through a real server," and this SDK's entire value proposition is the
  session bridge, so prove the bridge works end to end, not just that each piece compiles in isolation.
- State in your final summary: the exact method count per service (matching the existing docs' "N methods,
  and nothing more" style), confirmation the session-cookie shape matches `AppSessionCookie.Set()` exactly,
  and the new CI job's exact name as it reported.

## Deploying (only if the owner asks)

This task ships no server changes — nothing to deploy on `praxycore.dev`. Publishing to npm is a separate,
later decision; do not publish without being asked.
