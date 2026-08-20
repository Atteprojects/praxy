# Research — Next.js/TypeScript SDK design

Source: study of Appwrite's SSR auth docs, the new `@appwrite.io/react` library (Next.js App Router +
TanStack Start entrypoints), and Appwrite Sites' rendering docs — cross-referenced against Praxy's actual
`AccountEndpoints.cs`, `OAuthService.cs`, and `AppPrincipal.cs` to separate "what Appwrite does" from "what
Praxy already does or needs." Distilled to the decisions and traps that affect Praxy.

Owner decisions taken 2026-08-20: **App Router only** (no Pages Router), **full v1 parity with the Flutter
SDK's current surface** (no repeat of the "narrow v1, grow additively" experiment).

---

## Decisions taken

### Package structure — four packages, mirroring the Flutter split

Appwrite's own history validates the isomorphic-core pattern: two generations of guidance are live in their
docs simultaneously — a manual `createSessionClient()`/`createAdminClient()` tutorial pattern (Express-style,
copy-pasted into a Next.js server file), superseded by `@appwrite.io/react`, a single `AppwriteProvider` +
hooks package built on their Web SDK + TanStack Query, with dedicated entrypoints for Next.js App Router and
TanStack Start that wire cookie/hydration/admin-client automatically — so the same hooks behave identically
in a server-rendered page and a plain Vite SPA. That's the shape to copy, not the manual tutorial pattern it
replaced.

```
sdk/js/
  packages/core/       @praxy/core     isomorphic, fetch-only, edge-runtime-compatible. Transport, errors,
                                        models, services (Account/Tables/Teams/Functions), query builder.
                                        No React, no Next.js dependency — usable from a plain Node script,
                                        a Vite SPA, or a worker.
  packages/react/       @praxy/react    hooks + <PraxyProvider>, built on @tanstack/react-query — already a
                                        console dependency (console/package.json), so this reuses a library
                                        the codebase has already proven out, not a new one.
  packages/nextjs/       @praxy/nextjs   thin: createServerClient(), createApiKeyClient(), the OAuth
                                        callback Route Handler, praxyMiddleware(). Depends on @praxy/react
                                        for the client-side half.
  packages/codegen/       @praxy/codegen  typed row interfaces from a project's schema, mirrors praxy_codegen.
  examples/nextjs/                       real App Router app, mirrors sdk/flutter/example.
```

### Sessions — Praxy already has the cookie primitive; the Next.js adapter's job is to use it correctly

The single loudest, most-repeated line in Appwrite's SSR docs: **never share a Client instance between
requests.** Their server SDK returns a *new* client from `createSessionClient()`/`createAdminClient()` on
every call, specifically to prevent one request's session leaking into another's response — a real class of
bug in naive SSR integrations (a module-level singleton client is the classic Next.js footgun: it's built
once at cold-start and then silently reused across every subsequent request on that instance).

Praxy already has the server-side half of this solved, and it's a closer match to the cookie flow than the
Flutter SDK's bearer-token model — `AppPrincipalFilter` (`src/Praxy.Api/Infrastructure/AppPrincipal.cs`)
reads `X-Praxy-Session` **or falls back to a `praxy_session_<projectId>` cookie**, and
`AppSessionCookie.Set()` already exists server-side with the exact shape a Next.js app should mirror:

```csharp
// src/Praxy.Api/Infrastructure/AppPrincipal.cs — already shipped, not something to build
public static void Set(HttpContext ctx, string projectId, string token, DateTimeOffset expiresAt) =>
    ctx.Response.Cookies.Append(Name(projectId), token, new CookieOptions {
        HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax,
        Path = "/", Expires = expiresAt,
    });
// Name(projectId) => $"praxy_session_{projectId}"
```

`@praxy/nextjs`'s `createServerClient()` is a **factory function, never a cached singleton** — call it fresh
in every Server Component/Action/Route Handler:

```ts
// packages/nextjs/src/server.ts
export async function createServerClient(config: { endpoint: string; projectId: string }) {
  const jar = await cookies(); // next/headers — request-scoped, cannot leak across requests
  const token = jar.get(`praxy_session_${config.projectId}`)?.value;
  return new Praxy({ ...config, transport: new FetchTransport({ sessionToken: token }) });
}
```

A second factory, `createApiKeyClient()`, mirrors Appwrite's `createAdminClient()` but maps onto something
Praxy actually has: the API-key server surfaces built this session (`UsersServerEndpoints.cs`,
`FunctionEndpoints.cs`'s `/v1/functions` management routes, the data-plane `/v1/databases` twin). Reads an
API key from `process.env`, never from a cookie, never shipped to the client bundle — for privileged
server-only operations a signed-in user's own session shouldn't be able to do.

### The disambiguation trick already in the wire protocol — one cookie slot, two token shapes

`AppPrincipalFilter` tells a raw session secret from a minted JWT by counting dots (a JWT has exactly two):

```csharp
if (sessionToken.Count(c => c == '.') == 2) { /* AccountJwtService.VerifyAsync — scoped JWT */ }
else { /* AppAuthService.ResolveSessionAsync — full session */ }
```

This means the *same* cookie-or-header slot already accepts either shape server-side — nothing to add.
What the SDK needs to decide is which shape goes where:

- **Server-side** (`createServerClient()`): the full session secret, in the httpOnly `praxy_session_<id>`
  cookie set at sign-in. Never exposed to client JS.
- **Client-side** (`@praxy/react`'s hooks, realtime): a **JWT**, minted server-side per request via
  `POST /v1/account/jwts` (already shipped — post-v0.1.0 gap #6) and handed down as initial state to
  `<PraxyProvider initialJwt={...}>`. The endpoint's own doc comment states exactly this use case: "a
  short-lived, stateless JWT the caller can hand to another process... to act as this user without sharing
  the session secret itself." Client components never see the real session token, ever — only a short-lived,
  narrowly-scoped stand-in for it. Realtime's WebSocket ticket flow authenticates the same way, so this one
  bridge covers both plain API calls and realtime from the browser.

### OAuth — the token-exchange flow, and PKCE, are already built; Next.js just needs one Route Handler

Verified against `OAuthService.cs` and `AccountEndpoints.cs`, not assumed:

1. Start: `GET /v1/account/sessions/oauth2/{provider}?project=<id>&success=<url>&failure=<url>` — redirects
   to Google. PKCE verifier/challenge and a signed, short-lived state cookie are generated server-side
   already; the SDK adds nothing here.
2. Callback: `GET /v1/account/sessions/oauth2/callback/{provider}/{projectId}` — on success, redirects to
   the caller-supplied `success` URL with `?userId=<id>&secret=<opaque>` appended (`OAuthService.cs:104-106`).
   On failure, `?error=<type>`.
3. Exchange: `POST /v1/account/sessions/token` with `{userId, secret}` (`TokenExchangeRequest`) returns
   `201 {user, session, token}` (`CreatedSessionResponse`) — a real session, ready to cookie.

So the Next.js integration is exactly one Route Handler, `app/auth/callback/route.ts`, set as the `success`
URL when starting the flow: read `userId`/`secret` from `searchParams`, `POST /v1/account/sessions/token`,
set the `praxy_session_<projectId>` cookie with `AppSessionCookie.Set`'s exact options, redirect into the
app. No token flow to design — it's already the token flow the Flutter SDK research doc's own "server-side
requirements" section asked for, and it already shipped.

### Realtime — client-side only, same JWT bridge, no new transport concept

Server Components can't hold a WebSocket across a request/response cycle, so realtime is exclusively a
`@praxy/react` concern: a `useLiveList` hook analogous to `praxy_flutter`'s `liveList<T>`, opening the socket
with the same server-minted JWT `@praxy/react`'s provider already holds for REST calls — one auth bridge,
not two.

### Codegen — same restraint as `praxy_codegen`

Typed row interfaces and column-key constants from a project's live schema, emitted as committed `.ts`
files a developer runs on demand (`npx praxy-codegen ...`) — deliberately **not** a webpack/Turbopack loader
or a file watcher, for the same reason `praxy_codegen` isn't a `build_runner` builder: generated files that
show up in diffs beat a codegen step that runs silently on every save. No query-builder generation in v1 —
typed interfaces only, matching the Dart codegen's actual scope, not an aspirational wider one.

---

## Server-side requirements this imposes on Praxy

The Flutter SDK's own research doc surfaced seven required server changes before Phase 5 shipped (API
version header, request-id echo, structured field errors, `Retry-After`, a realtime `connected` frame,
genuinely-partial PATCH, token-flow OAuth with PKCE). **This research found zero new ones.** Every item that
list asked for was already built to serve the Flutter SDK, and it turns out to be exactly what a Next.js SDK
needs too — the token-flow-not-cookie-flow OAuth requirement in particular was written for a mobile SDK that
has no browser cookie jar at all, and it happens to be *also* correct for a framework that has one. Nothing
here should reopen or duplicate that work.

---

## What Appwrite Sites reveals about Praxy's own future direction (context only — out of scope for this SDK)

Appwrite Sites' SSR execution runs "at the user's nearest edge location," and its build/deploy pipeline
(git-push-triggered, framework-config scanning to pick static vs. SSR, per-PR preview URLs) is described
as SSR being handled through their existing Functions infrastructure rather than a wholly separate hosting
runtime. Praxy already has the equivalent building block — `FunctionsService` + `DockerExecutor` (Phase 7):
tar upload → Docker build → warm pool → invoke. A future Praxy Sites feature most plausibly extends that
pipeline (a site's build output becomes a function-shaped deployable) rather than inventing hosting from
scratch. This is not a design for Sites — it's a note for whoever eventually plans it, so that work starts
from "extend Functions" rather than re-deriving the same conclusion Appwrite already reached.

---

## Traps found in Appwrite's approach, worth not repeating

- **Two generations of docs, both live.** The manual `createSessionClient()`/`createAdminClient()` tutorial
  pattern is still published and indexed alongside the newer `@appwrite.io/react` library that supersedes
  it, with no clear deprecation signal — a developer following search results has no way to know which one
  is current. Ship one recommended pattern in `docs/research`/README, and if a lower-level manual pattern is
  documented at all (for advanced/non-React use), label it explicitly as the escape hatch, not co-equal
  guidance.
- **"Never share a Client instance between requests" is a prose warning, not something the SDK's types
  enforce.** A developer can still accidentally hoist a client to module scope; Appwrite's docs can only ask
  nicely. `@praxy/nextjs` should make the correct usage the *only* usage it exposes — `createServerClient()`
  as a plain async factory with no exported singleton or cached instance, so there's nothing to accidentally
  hoist.
- **`a_session_<PROJECT_ID>` cookie naming is worth confirming, not copying blind** — Praxy already
  independently arrived at the identical `praxy_session_<projectId>` shape (project-scoped, so one Next.js
  app can talk to two Praxy projects — e.g. staging and prod — without a name collision). Good convergent
  design; no change needed, just noting the validation.

---

## v1 SDK surface — full parity with the Flutter SDK's current surface (owner decision, 2026-08-20)

No narrow-then-grow phase this time — the Flutter SDK already ran that experiment once
(`docs/research/flutter-sdk.md`'s v1 → v1.1 split) and the wider surface proved itself. Port the v1.1 surface
directly:

- **Client/infra:** `Praxy(...)` (core), `createServerClient()`/`createApiKeyClient()`/`praxyMiddleware()`
  (nextjs), `<PraxyProvider>` + hooks (react), typed error hierarchy, `Query`, `Col<T>`.
- **Account (15):** `get`, `create`, `createEmailSession`, `createOAuth2Session` (redirect-based, Next.js
  Route Handler owns the callback), `deleteSession`, `updatePrefs`, `updateName`, `updatePassword`,
  `listSessions`, `sendVerification`/`confirmVerification`, `sendRecovery`/`confirmRecovery`, `roles`,
  `createJwt`.
- **Tables (7):** `list<T>`, `get<T>`, `create<T>`, `update<T>`, `delete<T>`, plus realtime `liveList<T>`.
  (No `upsert` — same real API gap `TablesService`'s own doc comment already documents; not this SDK's
  problem to paper over.)
- **Teams (10):** team CRUD + membership CRUD including invitation acceptance, matching `TeamEndpoints.cs`'s
  client-facing surface — not the console admin one, same boundary the Flutter SDK draws.
- **Functions (2):** `createExecution` (sync/async) + `getExecution`, data-plane invoke only — deployment
  management stays a console/operator concern.
- **Realtime (4):** `rows<T>`, `account`, `connection`, `close` — client-side only, JWT-bridged as above.

Out of v1, same reasoning as Flutter's: no `MessagingService` (`MessagingEndpoints.cs` has no client-facing
route — a server gap, not an SDK one), no Storage/avatars/locale/transactions, no function-deployment
management. Additionally out of scope for this SDK specifically: Pages Router, any Sites-related bindings
(nothing exists server-side to bind to yet), and Next.js `fetch` cache/`revalidateTag` integration (real
value, real complexity, deliberately deferred rather than rushed into v1).
