# Praxy Next.js SDK

The second Praxy SDK, after Flutter — groundwork for an eventual Sites-style hosting feature (itself
not yet planned; see `docs/research/nextjs-sdk.md`). **App Router only** — no Pages Router support.

## Packages

| Package | Description |
| --- | --- |
| [`@praxy/core`](packages/core) | Isomorphic, `fetch`-only TypeScript client. Transport, typed errors, `Query`/`Col<T>`, and five services (Account, Tables, Teams, Functions, Realtime). No React, no Next.js dependency — usable from a plain Node script, a Vite SPA, or a worker. |
| [`@praxy/react`](packages/react) | `<PraxyProvider>` + hooks, built on `@tanstack/react-query`. |
| [`@praxy/nextjs`](packages/nextjs) | `createServerClient()`/`createApiKeyClient()`, the OAuth callback Route Handler, `praxyMiddleware()`. Depends on `@praxy/react` for its re-exported client-side half. |
| [`@praxy/codegen`](packages/codegen) | `npx praxy-codegen` — typed row interfaces + column-key constants from a project's live schema. |
| [`examples/nextjs`](examples/nextjs) | A real App Router app: email sign-in, a Server Component read, a Server Action write, a realtime Client Component. |

## Quick start

```ts
import { createServerClient } from "@praxy/nextjs";

// Any Server Component, Server Action, or Route Handler — call fresh every time, never hoisted.
const client = await createServerClient({ endpoint, projectId });
const user = await client.account.get();
```

```tsx
// A Server Component wraps its client subtree, handing down a JWT — never the real session:
const { jwt } = await client.account.createJwt();
<PraxyProvider config={{ endpoint, projectId }} initialJwt={jwt}>
  <TodoList />
</PraxyProvider>;

// A Client Component:
("use client");
const { rows, connectionState } = useLiveList(todosTable);
```

See `@praxy/nextjs`'s README for the full session-bridge story (the cookie shape, the OAuth callback,
why `praxyMiddleware()` has its own import path) and `@praxy/react`'s README for exactly which
operations work with a client-side JWT versus which need a real session done server-side.

## Realtime & the example app

`examples/nextjs` is the full proof: `npm run dev` there against a local Praxy instance, sign in, add
a row via a Server Action, watch it appear in a Client Component's realtime view — and watch a row
inserted from an entirely different client (the console) arrive there too, live. See its own README
for setup (you'll need a database/table and a registered `localhost` platform for CORS).

## Development

From `sdk/js/`:

```bash
npm install
npm run build       # tsc, in dependency order: core → react → nextjs → codegen
npm run test         # vitest, per package
npm run typecheck    # tsc --noEmit, per package
```

TypeScript pinned to `5.9.3` (matching `console/`'s own pin, for one consistent version across the
repo's JS tooling — not a `@praxy/*`-specific requirement). `@praxy/core`/`@praxy/react`/`@praxy/nextjs`
ship ESM (`"type": "module"`); `@praxy/codegen` ships CommonJS (it's a standalone CLI run directly by
`node`, never bundled — see that package's `tsconfig.build.json` for why the split exists).
