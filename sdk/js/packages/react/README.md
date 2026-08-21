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

`usePraxyClient()` is the escape hatch — the full `@praxy/core` `Praxy` client (JWT-authenticated),
for anything a dedicated hook doesn't cover.

## Development

From `sdk/js/`:

```bash
npm run test -w packages/react        # vitest, jsdom environment
npm run typecheck -w packages/react   # tsc --noEmit
npm run build -w packages/react       # tsc -p tsconfig.build.json → dist/
```
