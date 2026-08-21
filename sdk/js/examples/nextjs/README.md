# Praxy Next.js SDK example

A real App Router app exercising the whole SDK end to end: email sign-in (+ a wired-up Google OAuth
button), a Server Component reading rows, a Server Action writing a row, and a Client Component
receiving realtime updates — the session bridge this SDK exists for, proven against a real local
Praxy instance, not just "the types compile."

## Setup

1. Run the Praxy API locally (`dotnet run --project src/Praxy.Api`, see the repo root `CLAUDE.md`).
2. Open the console (`npm run dev --prefix console`), claim the instance, and create a project.
3. In that project: create a database, a `todos` table with columns `title` (string, required) and
   `done` (boolean, default `false`), and grant **All signed-in users** create/read/update/delete on
   the table (Settings tab — tables deny everyone by default).
4. Under **Platforms**, add a **Web** platform with hostname `localhost` — the browser calls the API
   directly (cross-origin from `localhost:3000` to `localhost:5090`), and Praxy's CORS is
   allowlist-driven off registered platforms; skip this and every client-side fetch/WebSocket 403s.
5. Copy `.env.example` to `.env.local` and fill in the project/database/table ids from the console.
6. Optional: regenerate `lib/db.generated.ts` from the live schema instead of trusting it's still
   accurate — `@praxy/codegen`'s CLI is exactly built for this:
   ```bash
   npx praxy-codegen --endpoint http://localhost:5090 --project <id> --api-key <key> \
     --database main --table todos --class-name Todo --output lib/db.generated.ts
   ```
7. `npm run dev` (from this directory, or `npm run dev --prefix sdk/js/examples/nextjs` from the repo root).

## What each piece demonstrates

- **`app/page.tsx`** (Server Component) — checks the httpOnly session cookie via
  `createServerClient()` + `account.get()`; renders the email sign-in form and a Google OAuth link.
- **`app/actions.ts`** (Server Actions) — `signIn`/`signUp` call `@praxy/core` directly (no session
  yet), then `@praxy/nextjs`'s `setSessionCookie()` with the real session token; `signOut` calls
  `account.deleteSession()` then `clearSessionCookie()`.
- **`app/auth/callback/route.ts`** — `@praxy/nextjs`'s `createOAuthCallbackHandler()`, re-exported
  with two lines. Handles the `?userId=&secret=` redirect the server's OAuth flow produces.
- **`middleware.ts`** — `praxyMiddleware()` **from `@praxy/nextjs/middleware`**, not the main
  package export (see that package's README for why: importing it from the main barrel drags
  `@praxy/react`'s client-only code into the Edge Middleware bundle, which Next.js hard-rejects).
- **`app/dashboard/layout.tsx`** — mints a JWT server-side (`account.createJwt()`) and hands only
  that to `<PraxyProvider initialJwt={jwt}>` — the Client Component tree never sees the real session.
- **`app/dashboard/page.tsx`** (Server Component read) — `createServerClient()` +
  `tables.list(todosTable)`, authorized by the real session cookie.
- **`app/dashboard/actions.ts`** (Server Action write) — `createServerClient()` +
  `tables.create(todosTable, ...)`, then `revalidatePath()`.
- **`app/dashboard/live-todos.tsx`** (Client Component realtime) — `"use client"`, `useLiveList()`
  from `@praxy/react`, authenticated with the JWT the layout minted.

## Verified end to end (2026-08-20)

Against a real local Praxy instance (fresh Postgres, fresh claimed instance, real project): signed up
a test user, signed out, signed back in with email+password: dashboard reached both times. Added a
row via the Server Action form — appeared in both the Server Component list (after the action's
`revalidatePath`) and the realtime Client Component view, on the same page load, no reload. Then, in
a **separate browser tab running the console** (a different client entirely), inserted a row directly
into the table — it appeared in the Next.js tab's realtime view within the same second, with zero
interaction on that tab, proving the WebSocket push is genuinely live and not a re-render artifact.
