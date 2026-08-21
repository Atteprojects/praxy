# @praxy/core

Isomorphic, `fetch`-only TypeScript client for [Praxy](../../../README.md). No React, no Next.js
dependency — works from a plain Node script, a Vite SPA, a Cloudflare Worker, or (via `@praxy/nextjs`)
a Next.js server/edge context.

Five services, matching the wire protocol exactly:

- **`account`** — 15 methods: `get`, `create`, `createEmailSession`, `createOAuth2Session`,
  `deleteSession`, `updatePrefs`, `updateName`, `updatePassword`, `listSessions`, `sendVerification`,
  `confirmVerification`, `sendRecovery`, `confirmRecovery`, `roles`, `createJwt`.
- **`tables`** — 5 methods: `list`, `get`, `create`, `update`, `delete`. No `upsert` — the same real
  API gap the Flutter SDK's own `TablesService` doc comment documents (no server route exists).
- **`teams`** — 10 methods: `create`, `list`, `get`, `update`, `delete`, `createMembership`,
  `listMemberships`, `updateMembershipRoles`, `acceptInvitation`, `deleteMembership`.
- **`functions`** — 2 methods: `createExecution`, `getExecution`. Data-plane invocation only —
  deployment management stays a console/operator concern.
- **`realtime`** — 4 methods: `rows`, `account`, `connection`, `close`. Only meaningful in a browser
  (a Server Component can't hold a WebSocket across a request/response cycle) — see `@praxy/react`'s
  `useLiveList` for the hook most apps actually reach for.

## Install

Workspace-internal only — not published to npm. From `sdk/js/`: `npm install`.

## Usage

```ts
import { Praxy, Query, Col, tableRef } from "@praxy/core";

const px = new Praxy({ endpoint: "http://localhost:5090", projectId: "<project-id>" });

const { session, token } = await px.account.createEmailSession({ email, password });

// A fresh authenticated client — never mutate/reuse the anonymous one in place.
const authed = new Praxy({ endpoint: "http://localhost:5090", projectId: "<project-id>", sessionToken: token });

interface Todo {
  title: string;
  done: boolean;
}
const todos = tableRef<Todo>("<database-id>", "<table-id>");

const title = new Col<string>("title");
const page = await authed.tables.list(todos, { queries: [Query.equal(title, "Buy milk"), Query.limit(10)] });

const created = await authed.tables.create(todos, { data: { title: "Buy milk", done: false } });
await authed.tables.update(todos, created.$id, { data: { done: true } });
```

Errors are a typed hierarchy rooted at `PraxyError`: `PraxyApiError` (any server error response,
carrying `status`/`type`/`requestId`) with subclasses `PraxyAuthError` (401/403), `PraxyNotFoundError`
(404), `PraxyConflictError` (409), `PraxyRateLimitError` (429, carries `retryAfter` parsed from the
`Retry-After` header), and `PraxyValidationError` (400 with a structured `fields` map) — plus
`PraxyNetworkError` (transport-level failure) and `PraxyDecodeError` (a response body that wasn't the
expected shape) as direct `PraxyError` subclasses, not `PraxyApiError` ones. Each carries a `kind`
discriminant for `switch`-based handling without an `instanceof` chain.

`Query`/`Col<T>` are value objects, not pre-encoded strings, so `Query.or([...])` composes child
`Query` values directly instead of re-decoding its own inputs.

## What's not here

- **Session persistence** — this package doesn't own a session store the way `praxy_flutter` does
  (secure-storage on mobile). `@praxy/nextjs`'s `createServerClient()` reads the httpOnly session
  cookie fresh on every call instead; a plain browser/Node consumer of `@praxy/core` manages its own
  token storage.
- **Google OAuth's redirect dance** — `account.createOAuth2Session()` is the token-exchange half only;
  starting the flow and handling the callback redirect is `@praxy/nextjs`'s Route Handler.
- **Typed row codegen** — see `@praxy/codegen`.
- **React bindings** — see `@praxy/react`.

## Development

From `sdk/js/`:

```bash
npm run test -w packages/core        # vitest
npm run typecheck -w packages/core   # tsc --noEmit
npm run build -w packages/core       # tsc -p tsconfig.build.json → dist/
```
