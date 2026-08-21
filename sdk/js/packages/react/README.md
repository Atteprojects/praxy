# @praxy/react

`<PraxyProvider>` + hooks, built on `@tanstack/react-query` (already proven out in this repo by
`console/package.json` — reused, not a new cache library). Works in any React tree; `@praxy/nextjs`
depends on this package for its client-side half.

## Install

Workspace-internal only — not published to npm. From `sdk/js/`: `npm install`.

## The auth model this package assumes

`<PraxyProvider initialJwt={jwt}>` authenticates every hook's requests with a **JWT**, never the real
session token (a Client Component can't read the httpOnly session cookie anyway — see
`@praxy/nextjs`'s README for how the JWT gets here). A JWT authenticates as
`RequestPrincipal.JwtUser` server-side, which is enough for **permission/role-checked** endpoints —
row CRUD, function invocation, realtime — but is deliberately **not** accepted by
`AppPrincipalFilter.RequireUser`, which most of `/v1/account` and all of `/v1/teams` gate on. So:

| Works with a JWT (client-side hooks exist here) | Needs a real session (do this server-side instead) |
| --- | --- |
| `tables.*` (`useRows`, `useRow`, `useCreateRow`, `useUpdateRow`, `useDeleteRow`) | `account.get`, `updateName`, `updatePassword`, `listSessions`, `deleteSession`, … |
| `functions.createExecution` (`useCreateExecution`) | all of `teams.*` |
| `realtime.*` (`useLiveList`, `useConnectionState`) | — |
| `account.roles` (`useRoles` — role resolution, not `RequireUser`) | — |

This isn't a gap — it's the server's actual authorization model (see `AccountJwtService`'s own doc
comment: a JWT is "enough... to resolve roles... but deliberately not accepted by
`RequireUser`"). There's no `useAccountProfile()`/`useSessions()`/`useTeams()` hook here because they'd
just 401. Read the user's profile in a Server Component and pass down what the UI needs as props; do
sign-in, profile edits, and team management as Server Actions via `@praxy/nextjs`'s
`createServerClient()`, which holds the real session.

## Usage

```tsx
// A Server Component minted this JWT via createServerClient() + account.createJwt() and passed it down.
<PraxyProvider config={{ endpoint, projectId }} initialJwt={jwt}>
  <TodoList />
</PraxyProvider>;

function TodoList() {
  const { rows, connectionState } = useLiveList(todosTable);
  const createRow = useCreateRow(todosTable);

  return (
    <>
      <p>Realtime: {connectionState}</p>
      {rows.map((row) => (
        <div key={row.$id}>{row.title}</div>
      ))}
      <button onClick={() => createRow.mutate({ data: { title: "New todo", done: false } })}>Add</button>
    </>
  );
}
```

## Contents

- [`<PraxyProvider>`](#praxyprovider)
- [`usePraxyClient()`](#usepraxyclient)
- [`usePraxyJwt()`](#usepraxyjwt)
- [`useRoles()`](#useroles)
- [`useRows()`](#userows)
- [`useRow()`](#userow)
- [`useCreateRow()`](#usecreaterow)
- [`useUpdateRow()`](#useupdaterow)
- [`useDeleteRow()`](#usedeleterow)
- [`useLiveList()`](#uselivelist)
- [`useConnectionState()`](#useconnectionstate)
- [`useCreateExecution()`](#usecreateexecution)

### `<PraxyProvider>`

```tsx
<PraxyProvider
  config={{ endpoint, projectId }}
  initialJwt={jwt}       // optional — omit for a not-yet-signed-in tree; every hook 401s until set
  queryClient={myQueryClient} // optional — bring your own to share a cache with the rest of the app
  transport={myTransport}     // optional escape hatch, mainly for tests
>
  {children}
</PraxyProvider>
```

Every hook below reads its client from this provider's context — nothing works outside one. Wraps
its own `QueryClientProvider` unless you pass `queryClient` yourself.

### `usePraxyClient()`

```ts
const client: Praxy = usePraxyClient();
await client.tables.list(todosTable); // the full @praxy/core surface, JWT-authenticated
```

The escape hatch — for anything a dedicated hook below doesn't cover.

### `usePraxyJwt()`

```ts
const { jwt, setJwt } = usePraxyJwt();
setJwt(freshJwt); // e.g. after a Server Action mints a new one — rebuilds the client on next render
```

### `useRoles()`

```ts
const { data } = useRoles(); // TanStack Query result wrapping account.roles()
data?.roles; // e.g. ["any", "users", "user:<id>"]
```

The one Account hook here — `roles()` is role-resolution, not `RequireUser`-gated, so it works with a
JWT (see the table above). No `useAccountProfile()`/`useSessions()` — those need a real session.

### `useRows()`

```ts
const { data, isLoading, error } = useRows(todosTable, {
  queries: [Query.equal(Done, false), Query.limit(25)], // optional
  total: false,                                          // optional — skip the count query
});
data?.rows; // Row<Todo>[]
```

A plain TanStack `useQuery` wrapping `tables.list()` — no live updates; see `useLiveList()` for that.

### `useRow()`

```ts
const { data: row } = useRow(todosTable, rowId); // rowId: string | null — the query is disabled while null
```

### `useCreateRow()`

```ts
const createRow = useCreateRow(todosTable);
createRow.mutate({ data: { title: "Buy milk", done: false } });
await createRow.mutateAsync({ data: { title: "Buy milk", done: false }, rowId: "custom-id" });
```

A TanStack `useMutation` wrapping `tables.create()`; invalidates every `useRows`/`useRow` query for
this table on success.

### `useUpdateRow()`

```ts
const updateRow = useUpdateRow(todosTable);
updateRow.mutate({ rowId, data: { done: true } }); // genuinely partial — only `done` is sent
```

### `useDeleteRow()`

```ts
const deleteRow = useDeleteRow(todosTable);
deleteRow.mutate(rowId);
```

### `useLiveList()`

```ts
const { rows, total, isLoading, error, connectionState } = useLiveList(todosTable, {
  queries: [Query.equal(Done, false)], // optional
});
```

A REST snapshot (`tables.list()`) patched live by a `realtime.rows()` subscription — mirrors
`praxy_flutter`'s `liveList<T>`. Two of the same documented simplifications: row order is snapshot
order with new rows appended (never re-sorted against `orderAsc`/`orderDesc` on a live patch), and
`total` goes stale to `null` once the first patch lands.

### `useConnectionState()`

```ts
const state = useConnectionState(); // "disconnected" | "connecting" | "connected" | "reconnecting"
```

The shared realtime socket's own connection lifecycle — `useLiveList` already surfaces this as
`connectionState`; use this hook directly if you want it without also subscribing to a table.

### `useCreateExecution()`

```ts
const invoke = useCreateExecution(functionId);
invoke.mutate({ method: "POST", path: "/hello", body: JSON.stringify({ name: "Ada" }) });
invoke.mutate({ async: true }); // 202 receipt; poll functions.getExecution() via usePraxyClient()
```

Works with a JWT-only client — function invocation is authorized by the function's `execute` role
list (permission-based), the same as row access, not `AppPrincipalFilter.RequireUser`.

## Development

From `sdk/js/`:

```bash
npm run test -w packages/react        # vitest, jsdom environment
npm run typecheck -w packages/react   # tsc --noEmit
npm run build -w packages/react       # tsc -p tsconfig.build.json → dist/
```
