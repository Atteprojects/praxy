# Session task — let a caller read back their own async function execution

## Why this exists

Found while writing the prompt for gap-analysis item #6 (Flutter SDK parity),
not while implementing it — worth fixing on its own rather than papered over
inside an SDK task.

The data-plane function-invoke route maps exactly one operation:
`dataPlane.MapPost("/{functionId}/executions", Invoke)`
(`src/Praxy.Api/Endpoints/FunctionEndpoints.cs:134`). There is no
`GET .../executions/{id}` on the data plane. The read exists only on the
console admin surface (`GetExecution`, same file, `RequireOperatorFilter`-
gated), which an app user's session or an API key cannot reach.

So today: an app or a server integration calls
`POST /v1/functions/{functionId}/executions?async=true`, gets back a `202`
with a `waiting` execution and nowhere to check on it again. Async invocation
is not slow — it is unusable. This is what item #1's
`RequireExecutePermissionAsync` gate protects, and it deserves the same
scrutiny that gate got, not a quiet SDK-side workaround.

Work on a new branch off `main`. Read `CLAUDE.md` first. This is a small,
single post-Phase-9 fix — do not let it grow past what's scoped here.

## Non-goals — do not build these

- **No change to sync invoke.** It already returns the completed result
  directly; this task is async-only.
- **No console changes.** The operator's `GetExecution` already exists and
  already works; this is exclusively the data-plane gap.
- **No execution cancellation, no execution list for the data plane.** Only
  a single-execution read. A data-plane list endpoint is a different,
  larger surface-area decision (pagination, what a caller may enumerate) that
  nothing has asked for.
- **No retry/webhook-style delivery of the result to the caller.** Polling is
  the whole feature; push notification of completion is a different, bigger
  feature.

## Scope

1. **`GET /v1/functions/{functionId}/executions/{executionId}`** on the
   existing data-plane group
   (`api.MapGroup("/v1/functions").AddEndpointFilter<ProjectGuardFilter>().AddEndpointFilter<AppPrincipalFilter>()`,
   same file). Returns the same `FunctionExecutionResponse` the console
   already uses.
2. **Decide, and implement, who may read it.** See the landmine below — this
   is the actual design work in this task, not the routing.
3. **Fix `TriggeredBy`'s ambiguity for API keys.** See the landmine — this is
   what makes (2) resolvable in a way that doesn't leak one key's execution
   results to a different key.

## Landmines — read before writing code

Verified against current `main`, not recalled.

- **`TriggeredBy` cannot distinguish one API key from another today.**
  `Invoke`'s `triggeredBy` computation
  (`FunctionEndpoints.cs:353-359`) sets `user:<id>` for a session, but the
  bare literal string `"key"` for every API key, with no id. If the read
  endpoint's authorization rule is "you may read an execution you triggered,"
  every key satisfies that check against every other key's execution — a
  cross-key leak of another integration's function output and logs.

  **Fix this as part of the same change**: extend the key branch to
  `$"key:{Ids.Wire(apiKey.Id)}"`, matching the existing `user:<id>` shape.
  Checked before writing this prompt — nothing in `src/` or `tests/` matches
  the literal string `"key"` against `TriggeredBy`, and the console only
  renders the field raw (`FunctionExecutionsPage.tsx:57` — `{row.original.triggeredBy ?? ""}`),
  no parsing anywhere. The change is additive and safe. It also happens to be
  exactly the `key:<id>` actor format `docs/handoff/audit-log-read-surface-prompt.md`
  (gap #3) already proposes for the audit log — one vocabulary, not two, if
  that task lands around the same time as this one.

- **Pick the authorization rule deliberately, and write it down.** Two
  defensible options, not the same risk:
  - **(Recommended) Own-execution only**: a session may read an execution
    where `TriggeredBy == "user:<their id>"`; a key may read one where
    `TriggeredBy == "key:<its own id>"`. Tightest option — no caller ever sees
    another caller's function output, even within the same project. Matches
    the general shape "you can see the result of the thing you did," not "you
    can see the result of anything you're permitted to trigger."
  - **Same-role-as-invoke**: anyone who currently holds the function's
    `execute` role (or a key with the `functions.execute` scope) may read
    *any* execution of that function, not just their own. Simpler, but two
    different app users who both hold `execute("users")` on a public function
    would be able to read each other's request bodies, response bodies and
    logs — a real information leak between unrelated end users of the same
    app, and the reason the recommended option above exists.

  Whichever you pick, a caller who fails the check should get the same
  `401`/`404` shape `RowEndpoints`/`FunctionEndpoints` already use elsewhere —
  do not leak whether the execution exists to a caller who isn't allowed to
  see it (prefer `function_execution_not_found` over a distinguishable
  `unauthorized`, the same "don't confirm existence" instinct
  `RowsService`'s permission-filtered queries already follow).

- **Logs and response bodies are returned as-is — confirm that's still right
  once a *caller* (not just an operator) can see them.** `Logs`/`ResponseBody`/
  `Errors` are the function's own stdout/HTTP response, written by that
  project's own code. Nothing here redacts anything today because only
  operators could ever see it. That's probably still fine once the reader is
  restricted to the triggering caller — a user reading their own request's
  logs is not a new exposure — but say so explicitly rather than silently
  assuming; a function author who logs another user's data expecting only
  operators to see it is the edge case worth naming.

## Console

None. This is data-plane only.

## Tests

`tests/Praxy.Tests.Integration/` — Testcontainers, `postgres:17-alpine`, shared
collection fixture. `FunctionExecutePermissionTests.cs` is the closest
neighbour (it's Docker-free by design — reuse that discipline: authorize
before checking whether the execution exists, so these tests don't need a
real container build either).

- The triggering user can read their own async execution; a different signed-
  in user on the same project gets `404`.
- A key that triggered an execution can read it; a *different* key with the
  same `functions.execute` scope on the same function cannot — the actual
  regression test for the `TriggeredBy` fix.
- An unauthenticated caller gets the same denial shape as any other
  unauthorized data-plane call.
- The response shape matches what the console's `GetExecution` already
  returns for the same execution (one `FunctionExecutionResponse`, two routes,
  no drift).

## Done means

- `dotnet test` green (needs Docker).
- OpenAPI: `OpenApiDocumentTests` fails the build if the new operation lacks
  `.Produces<T>()` or the snapshot drifts — regenerate per
  `docs/api-reference.md`.
- `git status` clean, conventional commits, on a new branch off `main`.
- State in your final summary which authorization rule you chose and why, and
  confirm the `TriggeredBy` change doesn't alter what `FunctionExecutionsPage.tsx`
  displays (it renders the raw string either way, so `key:<id>` vs `key`
  should just show a more specific value, not break anything).

## Relationship to item #6 (Flutter SDK parity)

`docs/handoff/flutter-sdk-parity-prompt.md` currently documents this exact gap
as a landmine and tells that session to ship sync-only (or ship async with a
"can never be re-fetched" caveat) if this hasn't landed yet. Once this task
merges, update that prompt's landmine section to describe the real endpoint
instead of the workaround — or, better, do this task first and let #6 bind to
a complete API from the start.

## Deploying (only if the owner asks)

`praxycore.dev`, procedure in `docs/self-host.md`'s Upgrading section — backup
first, `git pull origin main`, then
`docker compose -f deploy/docker-compose.yml --profile https up -d --build`.
No schema migration (no new column — `TriggeredBy` is already a nullable
string; extending what goes into it needs no migration). Do not deploy unless
asked.
