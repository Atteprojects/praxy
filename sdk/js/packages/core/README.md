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

## Contents

- [Configuration](#configuration)
- [Errors](#errors)
- [Account](#account)
  - [Sign up](#sign-up)
  - [Sign in with email](#sign-in-with-email)
  - [Sign in with Google](#sign-in-with-google)
  - [Get the current user](#get-the-current-user)
  - [Update the current user's name](#update-the-current-users-name)
  - [Update the current user's password](#update-the-current-users-password)
  - [Update preferences](#update-preferences)
  - [List sessions](#list-sessions)
  - [Sign out / revoke a session](#sign-out--revoke-a-session)
  - [Email verification](#email-verification)
  - [Password recovery](#password-recovery)
  - [Resolved roles](#resolved-roles)
  - [Mint a JWT](#mint-a-jwt)
- [Tables (rows)](#tables-rows)
  - [`Query` and `Col<T>`](#query-and-colt)
  - [List rows](#list-rows)
  - [Get a row](#get-a-row)
  - [Create a row](#create-a-row)
  - [Update a row](#update-a-row)
  - [Delete a row](#delete-a-row)
- [Teams](#teams)
  - [Create a team](#create-a-team)
  - [List teams](#list-teams)
  - [Get a team](#get-a-team)
  - [Update a team](#update-a-team)
  - [Delete a team](#delete-a-team)
  - [Invite or add a member](#invite-or-add-a-member)
  - [List memberships](#list-memberships)
  - [Update a member's roles](#update-a-members-roles)
  - [Accept an invitation](#accept-an-invitation)
  - [Remove a member](#remove-a-member)
- [Functions](#functions)
  - [Invoke synchronously](#invoke-synchronously)
  - [Invoke asynchronously](#invoke-asynchronously)
- [Realtime](#realtime)
  - [Subscribe to row changes](#subscribe-to-row-changes)
  - [Subscribe to account events](#subscribe-to-account-events)
  - [Track connection state](#track-connection-state)
  - [Close the socket](#close-the-socket)
- [What's not here](#whats-not-here)
- [Development](#development)

## Configuration

```ts
import { Praxy } from "@praxy/core";

const px = new Praxy({
  endpoint: "http://localhost:5090", // or your self-hosted/production origin
  projectId: "<project-id>",
});
```

`Praxy` is a plain constructor, not a builder or a singleton — construct a fresh instance per
credential. It never mutates in place; signing in gets you a session/JWT/key to pass into a *new*
instance:

```ts
export interface PraxyConfig {
  endpoint: string;
  projectId: string;
  sessionToken?: string; // a full session secret, or a JWT — the server tells them apart by dot count
  apiKey?: string;       // server-only; never ship this to a browser bundle
  transport?: Transport; // escape hatch, mainly for tests — swap the default fetch-based transport
}
```

- **Anonymous** (`new Praxy({ endpoint, projectId })`) — enough to sign up, sign in, or hit any
  endpoint a `guests`/`any` role permits.
- **Session-authenticated** (`sessionToken: <the token from create()/createEmailSession()/...>`) —
  a full session; can do everything a signed-in user can, including `account.get()`,
  `updatePassword()`, `listSessions()`, and all of `teams.*`.
- **JWT-authenticated** (`sessionToken: <a createJwt() result>`) — a short-lived, narrowly-scoped
  stand-in for a session. Works for `tables.*`, `functions.createExecution`, `realtime.*`, and
  `account.roles()` (all permission/role-checked), but **401s** on `account.get()`,
  `updateName()`, `updatePassword()`, `listSessions()`, `deleteSession()`, and every `teams.*`
  method — those require a real session (`RequestPrincipal.AppUser`), not a JWT
  (`RequestPrincipal.JwtUser`). This is the entire reason `@praxy/nextjs`/`@praxy/react` exist: do
  session-only operations server-side, hand a JWT to the browser for everything else.
- **API-key-authenticated** (`apiKey: <a project API key>`) — server-only, scoped by the key's
  granted scopes; bypasses row permissions if the key has that flag set. Never construct this in
  code that ships to a browser.

In a Next.js app, you won't usually call `new Praxy(...)` directly for a signed-in user — see
`@praxy/nextjs`'s `createServerClient()`/`createApiKeyClient()`, which build the config's
`sessionToken`/`apiKey` for you from the current request.

## Errors

```ts
import { PraxyApiError, PraxyAuthError, PraxyValidationError } from "@praxy/core";

try {
  await px.account.createEmailSession({ email, password });
} catch (error) {
  if (error instanceof PraxyValidationError) {
    console.log(error.fields); // { password: ["must be at least 8 characters"] }
  } else if (error instanceof PraxyAuthError) {
    console.log("invalid credentials");
  } else if (error instanceof PraxyApiError) {
    console.log(error.status, error.type, error.requestId);
  }
}
```

A typed hierarchy rooted at `PraxyError`: `PraxyApiError` (any server error response, carrying
`status`/`type`/`requestId`) with subclasses `PraxyAuthError` (401/403), `PraxyNotFoundError` (404),
`PraxyConflictError` (409), `PraxyRateLimitError` (429, carries `retryAfter` parsed from the
`Retry-After` header), and `PraxyValidationError` (400 with a structured `fields` map) — plus
`PraxyNetworkError` (transport-level failure) and `PraxyDecodeError` (a response body that wasn't the
expected shape) as direct `PraxyError` subclasses, not `PraxyApiError` ones. Each carries a `kind`
discriminant (`"auth"`, `"not_found"`, `"validation"`, ...) for `switch`-based handling without an
`instanceof` chain.

## Account

### Sign up

```ts
const { user, session, token } = await px.account.create({
  email: "ada@example.com",
  password: "hunter2hunter2",
  name: "Ada Lovelace", // optional
});
// token is the real session secret — build a new authenticated client with it.
const authed = new Praxy({ endpoint, projectId, sessionToken: token });
```

### Sign in with email

```ts
const { user, session, token } = await px.account.createEmailSession({
  email: "ada@example.com",
  password: "hunter2hunter2",
});
```

### Sign in with Google

This is only the token-exchange half — starting the OAuth redirect and handling the callback is a
browser-navigation flow, not something `@praxy/core` alone can drive. See `@praxy/nextjs`'s OAuth
callback Route Handler, which calls this for you:

```ts
// After the server's OAuth callback redirects to your app with ?userId=&secret=:
const { user, session, token } = await px.account.createOAuth2Session({ userId, secret });
```

### Get the current user

```ts
const user = await authed.account.get(); // 401s on a JWT-only client — needs a real session
```

### Update the current user's name

```ts
const user = await authed.account.updateName("Ada Byron");
```

### Update the current user's password

```ts
// Changing your own password (you know the current one):
const user = await authed.account.updatePassword({ password: "newHunter3", oldPassword: "hunter2hunter2" });

// An operator-reset password, or completing recovery, has no old password to prove:
const user = await authed.account.updatePassword({ password: "newHunter3" });
```

### Update preferences

```ts
const user = await authed.account.updatePrefs({ theme: "dark", locale: "en-US" });
```

### List sessions

```ts
const { total, sessions } = await authed.account.listSessions();
const current = sessions.find((s) => s.current);
```

### Sign out / revoke a session

```ts
await authed.account.deleteSession();       // the current session (default)
await authed.account.deleteSession("current"); // same as above, explicit
await authed.account.deleteSession(sessionId); // revoke a specific session, e.g. "log out other devices"
```

### Email verification

```ts
await authed.account.sendVerification("https://app.example.com/verify"); // emails a link carrying ?userId=&secret=
const user = await px.account.confirmVerification({ userId, secret }); // from that link's query string
```

### Password recovery

```ts
await px.account.sendRecovery({ email: "ada@example.com", url: "https://app.example.com/reset-password" });
// From the emailed link's query string:
await px.account.confirmRecovery({ userId, secret, password: "newHunter3" });
```

### Resolved roles

Works with a JWT — useful for client-side, permission-aware UI (`@praxy/react`'s `useRoles()` wraps
this).

```ts
const { roles, principal, scopes } = await authed.account.roles();
// roles: e.g. ["any", "users", "user:<id>"] — what the query compiler/realtime fan-out sees for this caller
// principal: "user" | "key" | "guest"
// scopes: an API key's granted scopes, or null for a user/guest
```

### Mint a JWT

Requires a real session — you cannot mint a new JWT from an existing one (prevents infinite JWT
chaining from a leaked short-lived credential).

```ts
const { jwt } = await authed.account.createJwt();        // default lifetime (server-configured, currently 15 min)
const { jwt } = await authed.account.createJwt(60);       // custom lifetime in seconds, clamped server-side
```

## Tables (rows)

### `Query` and `Col<T>`

Value objects, not pre-encoded strings — `Query.or([...])` composes child `Query` values directly
instead of re-decoding its own inputs.

```ts
import { Col, Query } from "@praxy/core";

interface Todo {
  title: string;
  done: boolean;
  priority: number;
}

const Title = new Col<string>("title");
const Done = new Col<boolean>("done");
const Priority = new Col<number>("priority");

Query.equal(Title, "Buy milk");
Query.equalAny(Priority, [1, 2, 3]);
Query.notEqual(Done, true);
Query.lessThan(Priority, 5);
Query.lessThanEqual(Priority, 5);
Query.greaterThan(Priority, 1);
Query.greaterThanEqual(Priority, 1);
Query.between(Priority, 1, 5);
Query.isNull(Done);
Query.isNotNull(Done);
Query.startsWith(Title, "Buy");
Query.endsWith(Title, "milk");
Query.contains(Title, "milk");
Query.search(Title, "milk");             // requires a fulltext index on the column server-side
Query.select([Title, Done]);              // only fetch these columns
Query.orderAsc(Priority);
Query.orderDesc(Priority);
Query.limit(25);
Query.offset(50);
Query.cursorAfter(lastRowId);
Query.cursorBefore(firstRowId);
Query.and([Query.equal(Done, false), Query.greaterThan(Priority, 2)]);
Query.or([Query.equal(Title, "Buy milk"), Query.equal(Title, "Walk the dog")]);
Query.raw("someFutureMethod", { attribute: "col", values: [1] }); // escape hatch
```

### List rows

```ts
import { tableRef } from "@praxy/core";

const todos = tableRef<Todo>("<database-id>", "<table-id>"); // ids, not keys — from the console or your own config

const { total, rows } = await authed.tables.list(todos, {
  queries: [Query.equal(Done, false), Query.orderDesc(Priority), Query.limit(25)],
});

// Skip the count query on a hot path — total comes back null:
const { rows: page2 } = await authed.tables.list(todos, { total: false });
```

### Get a row

```ts
const row = await authed.tables.get(todos, rowId); // row.$id, row.$createdAt, ...row's own columns
```

### Create a row

```ts
const row = await authed.tables.create(todos, {
  data: { title: "Buy milk", done: false, priority: 3 },
  rowId: "custom-id",              // optional — omit to let the server generate one
  permissions: ['read("any")'],    // optional row-level permissions (only meaningful with row security enabled)
});
```

### Update a row

Genuinely partial — only the keys present in `data` are sent, matching the server's partial-PATCH
contract (CLAUDE.md's "PATCH sends only changed fields" rule).

```ts
const row = await authed.tables.update(todos, rowId, { data: { done: true } }); // title/priority untouched
```

### Delete a row

```ts
await authed.tables.delete(todos, rowId);
```

No `upsert` — the same real API gap `TablesService`'s own doc comment documents on the Flutter SDK
(no server route exists); do a `get` + `create`/`update` if you need upsert semantics client-side.

## Teams

Every method here needs a real session or a key with the matching scope — a JWT-only client 401s on
all of `teams.*` (see [Configuration](#configuration)).

### Create a team

```ts
const team = await authed.teams.create({ name: "Engineering", roles: ["owner"] }); // roles: the creator's own roles on the team
```

### List teams

```ts
const { total, teams } = await authed.teams.list(); // teams the caller belongs to (or all, for a scoped key)
```

### Get a team

```ts
const team = await authed.teams.get(teamId);
```

### Update a team

```ts
const team = await authed.teams.update(teamId, "Platform Engineering"); // owner-only
```

### Delete a team

```ts
await authed.teams.delete(teamId); // owner-only
```

### Invite or add a member

A session invites by email (needs `url`, the invitation-acceptance redirect — an email goes out); a
key adds a member directly by `userId`, no email involved.

```ts
// From a signed-in session — sends an invitation email:
const membership = await authed.teams.createMembership(teamId, {
  email: "grace@example.com",
  roles: ["member"],
  url: "https://app.example.com/accept-invite",
});

// From a server-side API key — adds the member immediately, no email:
const membership = await keyClient.teams.createMembership(teamId, { userId, roles: ["member"] });
```

### List memberships

```ts
const { total, memberships } = await authed.teams.listMemberships(teamId);
```

### Update a member's roles

```ts
const membership = await authed.teams.updateMembershipRoles(teamId, membershipId, ["owner", "member"]); // owner-only
```

### Accept an invitation

Authenticated by the emailed secret, not by a session — also signs the user in (the response embeds
a `CreatedSession`, same as `account.create()`).

```ts
const { membership, session } = await authed.teams.acceptInvitation(teamId, membershipId, { userId, secret });
// From that link's query string. session.token is a real session secret.
```

### Remove a member

Self-removal is always allowed; removing someone else needs the team's `owner` role (or a scoped key).

```ts
await authed.teams.deleteMembership(teamId, membershipId);
```

## Functions

### Invoke synchronously

```ts
const execution = await authed.functions.createExecution(functionId, {
  method: "POST",
  path: "/hello",
  body: JSON.stringify({ name: "Ada" }),
});
console.log(execution.status, execution.statusCode, execution.responseBody, execution.logs);
```

### Invoke asynchronously

```ts
const receipt = await authed.functions.createExecution(functionId, { async: true }); // 202, status "waiting"/"processing"
// ...later, poll:
const completed = await authed.functions.getExecution(functionId, receipt.id);
```

`getExecution` is scoped to the caller's own execution — reading back someone else's execution id
404s (a deliberate cross-caller privacy boundary, not a bug).

## Realtime

Client-side only — a Server Component can't hold a WebSocket across a request/response cycle. Most
apps want `@praxy/react`'s `useLiveList`/`useConnectionState` instead of calling these directly; this
is the layer those hooks are built on.

### Subscribe to row changes

Delivers only `{action, rowId}` — the server's row-change event never carries column data, so
re-fetch with `tables.get`/`tables.list` on each event (`useLiveList` does this for you).

```ts
const unsubscribe = authed.realtime.rows(todos, (event) => {
  console.log(event.action, event.rowId); // "create" | "update" | "delete"
});

// Scoped to one row instead of the whole table:
const unsubscribeOne = authed.realtime.rows(todos, (event) => { /* ... */ }, { rowId });

unsubscribe.unsubscribe();
```

### Subscribe to account events

```ts
const unsubscribe = authed.realtime.account((event) => {
  console.log(event.event, event.payload); // e.g. "account.<userId>.session.create"
});
```

### Track connection state

Replays the current state to a new listener immediately, then forwards transitions.

```ts
const unsubscribe = authed.realtime.connection((state) => {
  console.log(state); // "disconnected" | "connecting" | "connected" | "reconnecting"
});
```

### Close the socket

Disposes the shared WebSocket. A later `rows`/`account`/`connection` call lazily reopens one.

```ts
authed.realtime.close();
```

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
