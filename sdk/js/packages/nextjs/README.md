# @praxy/nextjs

Next.js **App Router** integration for Praxy (no Pages Router support — see the workspace root
README). Thin on purpose: `createServerClient()`/`createApiKeyClient()`, the OAuth callback Route
Handler, and `praxyMiddleware()`. Depends on `@praxy/react` for the client-side half and re-exports
its entire surface from the main entry point, so an app only needs to install `@praxy/nextjs`.

## Install

Workspace-internal only — not published to npm. From `sdk/js/`: `npm install`.

## Contents

- [The session bridge](#the-session-bridge)
- [`createServerClient()`](#createserverclient--a-plain-factory-never-a-singleton)
- [`createApiKeyClient()`](#createapikeyclient--server-only-privileged-operations)
- [Sign up (a Server Action)](#sign-up-a-server-action)
- [Sign in with email (a Server Action)](#email-sign-in-a-server-action)
- [Sign out (a Server Action)](#sign-out-a-server-action)
- [Google OAuth — one Route Handler](#google-oauth--one-route-handler)
- [`praxyMiddleware()`](#praxymiddleware--route-protection)
- [The Client Component bridge](#the-client-component-bridge--a-jwt-never-the-real-session)
- [Everything else](#everything-else--full-praxycore-surface-server-side)

## The session bridge

Praxy's server already has the cookie primitive this package's job is to use correctly:
`AppSessionCookie.Name(projectId)` is `praxy_session_<projectId>`, read as a fallback to the
`X-Praxy-Session` header by `AppPrincipalFilter`. This package's cookie-setting code
(`setSessionCookie()`, the OAuth callback handler) sets it with the **exact same `CookieOptions`
shape** the C# `AppSessionCookie.Set()` helper uses:

```
httpOnly: true, secure: <request was https>, sameSite: 'lax', path: '/', expires: <session.expiresAt>
```

`secure` is derived from the request's `x-forwarded-proto` header (falling back to
`NODE_ENV === "production"` when that header is absent, e.g. local `next dev`) — never hardcoded.

### `createServerClient()` — a plain factory, never a singleton

```ts
// Any Server Component, Server Action, or Route Handler:
import { createServerClient } from "@praxy/nextjs";

const client = await createServerClient({ endpoint, projectId });
const user = await client.account.get();
```

Call this **fresh in every Server Component/Action/Route Handler that needs a client.** It reads the
httpOnly session cookie via `next/headers`'s `cookies()` (request-scoped, cannot leak across
requests) and builds a new `Praxy` client every time. There is no exported singleton or cached
instance to accidentally hoist to module scope — a client built once at cold-start and reused across
requests is how one request's session leaks into another's response in a serverless/edge environment,
the single most-repeated warning in every framework's own SSR auth docs.

### `createApiKeyClient()` — server-only, privileged operations

```ts
const admin = createApiKeyClient({ endpoint, projectId }); // reads PRAXY_API_KEY from the environment
```

Never reads from a cookie, never ships to the client bundle.

### Sign up (a Server Action)

```ts
"use server";
import { setSessionCookie } from "@praxy/nextjs";
import { Praxy } from "@praxy/core";

export async function signUp(email: string, password: string, name?: string) {
  const client = new Praxy({ endpoint, projectId }); // no session yet — a plain client
  const created = await client.account.create({ email, password, name });
  await setSessionCookie({ projectId, token: created.token, expiresAt: created.session.expiresAt });
}
```

### Email sign-in (a Server Action)

```ts
"use server";
import { setSessionCookie } from "@praxy/nextjs";
import { Praxy } from "@praxy/core";

export async function signIn(email: string, password: string) {
  const client = new Praxy({ endpoint, projectId });
  const created = await client.account.createEmailSession({ email, password });
  await setSessionCookie({ projectId, token: created.token, expiresAt: created.session.expiresAt });
}
```

`setSessionCookie()`/`clearSessionCookie()` are the two cookie-writing helpers this package exports
directly (beyond the OAuth handler, which calls the equivalent logic itself) — use them from any
Server Action that just exchanged credentials for a session and needs to persist it.

### Sign out (a Server Action)

```ts
"use server";
import { clearSessionCookie, createServerClient } from "@praxy/nextjs";

export async function signOut() {
  const client = await createServerClient({ endpoint, projectId });
  await client.account.deleteSession(); // revokes the session server-side
  await clearSessionCookie(projectId);   // then drops the cookie
}
```

### Google OAuth — one Route Handler

The server already runs the whole PKCE + state-cookie dance
(`GET /v1/account/sessions/oauth2/{provider}` → redirect to Google → `GET .../oauth2/callback/...` →
redirect to your `success` URL with `?userId=&secret=`). This package's job is exactly that one
callback:

```ts
// app/auth/callback/route.ts
import { createOAuthCallbackHandler } from "@praxy/nextjs";

export const { GET } = createOAuthCallbackHandler({ endpoint, projectId, redirectTo: "/dashboard" });
```

Start the flow with a plain link/redirect to
`${endpoint}/v1/account/sessions/oauth2/google?project=${projectId}&success=${origin}/auth/callback&failure=${origin}/sign-in`
— nothing else to wire up. The callback redirect carries `?userId=&secret=` in the query string, not
a ready session; the handler exchanges it at `POST /v1/account/sessions/token`
(`account.createOAuth2Session`) before setting the cookie.

### `praxyMiddleware()` — route protection

```ts
// middleware.ts
import { praxyMiddleware } from "@praxy/nextjs/middleware"; // note: /middleware, not the main entry

export default praxyMiddleware({ projectId, protectedPaths: ["/dashboard"], signInUrl: "/sign-in" });

export const config = { matcher: ["/dashboard/:path*"] };
```

**Import this from `@praxy/nextjs/middleware`, not the main `@praxy/nextjs` entry point.** The main
entry re-exports all of `@praxy/react` (for the one-install convenience documented above), and
`<PraxyProvider>` uses `createContext`, a client-only React API — importing it into `middleware.ts`
is a hard Next.js build error ("You're importing a module that depends on `createContext` into ...
Edge Middleware"), not a warning. The `/middleware` subpath only ever pulls in `next/server` and a
zero-dependency string helper, never `@praxy/react` or `@praxy/core` — nothing here could pull a
Node-only API into the edge bundle even transitively. (`@praxy/core` itself is statically verified
edge-safe too, independent of this — see its own test suite's `edge-safety.test.ts`.)

Runs on **Edge Runtime by default**. Checks cookie *presence* only, not validity — verifying the
session secret against the database would mean a network round-trip on every matched request. A stale
cookie still gets past this gate and is caught downstream by `createServerClient()` + a real `account`
call.

### The Client Component bridge — a JWT, never the real session

```tsx
// A Server Component:
import { createServerClient } from "@praxy/nextjs";
import { PraxyProvider } from "@praxy/nextjs"; // re-exported from @praxy/react

export default async function DashboardLayout({ children }: { children: React.ReactNode }) {
  const client = await createServerClient({ endpoint, projectId });
  const { jwt } = await client.account.createJwt();
  return (
    <PraxyProvider config={{ endpoint, projectId }} initialJwt={jwt}>
      {children}
    </PraxyProvider>
  );
}
```

If you find yourself passing the httpOnly cookie's value to a Client Component in any form, stop —
that defeats the entire point of `httpOnly`. Mint a JWT server-side instead; see `@praxy/react`'s
README for exactly what a JWT can and can't do.

## Everything else — full `@praxy/core` surface, server-side

A session-backed client from `createServerClient()` is a full `Praxy` client — every method in
`@praxy/core`'s README (update name/password/prefs, list/revoke sessions, email verification,
password recovery, all of `teams.*`, row CRUD, function invocation) works from it in a Server
Component or Server Action, no separate wrapper needed:

```ts
"use server";
import { createServerClient } from "@praxy/nextjs";

export async function updateDisplayName(name: string) {
  const client = await createServerClient({ endpoint, projectId });
  await client.account.updateName(name);
}

export async function changePassword(oldPassword: string, newPassword: string) {
  const client = await createServerClient({ endpoint, projectId });
  await client.account.updatePassword({ password: newPassword, oldPassword });
}

export async function inviteTeammate(teamId: string, email: string) {
  const client = await createServerClient({ endpoint, projectId });
  await client.teams.createMembership(teamId, { email, roles: ["member"], url: "https://app.example.com/accept-invite" });
}
```

## Development

From `sdk/js/`:

```bash
npm run test -w packages/nextjs        # vitest — handler functions invoked directly with mock
                                        # Request/cookies() objects, no running Next.js server needed
npm run typecheck -w packages/nextjs   # tsc --noEmit
npm run build -w packages/nextjs       # tsc -p tsconfig.build.json → dist/
```
