# Session task — Teams, Functions and Messaging services for the Flutter SDK

## Why this exists

`praxy_core` exposes exactly two services — `AccountService` and
`TablesService` — plus realtime in `praxy_flutter`. Three of Praxy's six
advertised features (Teams, Functions, Messaging) are unreachable from the
SDK. `AccountService` itself is also missing name/password update,
verification send/confirm, session listing, and JWT minting — all served by
the API, none reachable from Dart.

This was a deliberate v1 decision, not an oversight — worth reading before
changing anything: `docs/research/flutter-sdk.md`'s "v1 SDK surface" section
lists the ~20-method Phase 5 scope and states explicitly: *"Out of v1:
storage, functions, teams, messaging, avatars, locale, transactions, all
server-admin surfaces. Each is additive and none constrains the shape of the
four above. Every service shipped is a compatibility commitment."* This task
is that additive step for three of those five, plus finishing `AccountService`
to match what `AccountEndpoints.cs` actually serves.

This is item #6 of the post-v0.1.0 gap analysis, and the one furthest from the
others — it touches Dart, not C# or the console. Items #1–#3 are merged or
written up; #4 and #5 may or may not have landed by the time this runs.
Nothing here depends on any of them.

Work on a new branch off `main`. Read `CLAUDE.md` first, then
`docs/research/flutter-sdk.md` in full — it is short, and it is the actual
design spec this package follows, including "traps found in Appwrite's SDK,
worth not repeating" and the sealed-exception/typed-row conventions every
existing service already honors.

## Non-goals — do not build these

- **No Storage, avatars, locale, or transactions.** Storage does not exist in
  Praxy yet at all (a separate, larger gap); avatars/locale/transactions
  were never in Appwrite-parity scope for this SDK.
- **No `liveList`-style realtime wrapper for the new services.** Realtime
  today is rows-only (`praxy_flutter`'s `liveList<T>` and `account`/
  `connection` streams) — the roadmap's event grammar covers row events; team
  membership changes, function executions and message sends are not in the
  realtime event surface server-side, so there is nothing to stream. If that
  changes later, the realtime wrapper is additive then too.
- **No codegen changes.** `praxy_codegen` generates typed `TableRef`s from
  table schemas; it has nothing to do with teams/functions/messaging and
  should not be touched.
- **No second SDK (JS/TS, etc.).** Flutter-first is a fixed decision
  (`CLAUDE.md`); a second SDK is a different, larger task.
- **No `upsert` on tables, no anonymous sessions.** Both already documented as
  real API gaps in `TablesService`'s and `AccountService`'s own doc comments —
  not this task's problem to solve by inventing client-side workarounds.

## Scope

1. **`AccountService` gaps**: `updateName`, `updatePassword`, `listSessions`,
   `sendVerification`/`confirmVerification`, `roles` (the debug endpoint), and
   `createJwt`. Exact wire shapes below.
2. **`TeamsService`** (new): teams CRUD, memberships CRUD, matching
   `TeamEndpoints.cs`'s client-facing surface — not the console admin one.
3. **`FunctionsService`** (new): the data-plane invoke only
   (`POST /v1/functions/{functionId}/executions`, sync and async) — not
   deployment management, which is a console/operator concern, not an app's.
   **There is no way to poll an async result — see the landmine below before
   you design this method's return shape.**
4. **No `MessagingService`.** Checked already, not left for you to
   discover: `MessagingEndpoints.cs` maps exactly one route group
   (`/v1/console/projects/{projectId}/messaging`) and every route in it sits
   behind `RequireOperatorFilter`. There is no client-facing messaging
   endpoint anywhere in the API — an app user has no subscribe/unsubscribe
   call to bind to. Building a `MessagingService` here would mean inventing a
   server endpoint first, which is out of scope for an SDK task. If a
   client-facing messaging surface gets added to the API later, the SDK
   binding is a small additive follow-up then, same as this whole task is for
   teams/functions today.
5. **Wire both new services into `client.dart`** the way `account`/`tables`
   already are (`late final` fields, constructed in the `Praxy` constructor).
6. **Update `docs/research/flutter-sdk.md`'s v1 surface section** to describe
   the new v1.1 surface — the doc says the four original services don't
   constrain what's added; document what was actually added (Account's new
   methods, Teams, Functions — and that Messaging was evaluated and has
   nothing to bind to yet), in the same style, including exact method counts
   like the existing section does.

## Landmines — read before writing code

Verified against current `main`, not recalled.

- **`FunctionsService.CanExecute` and the deny-by-default execute-role gate
  apply to this SDK's invoke call exactly as they apply to any other
  caller.** A function with an empty `execute` list will 401 for every app
  user regardless of SDK support. This is expected, not a bug to route
  around — do not add SDK-side logic that tries to detect or explain this
  specially; let the existing `PraxyException` mapping surface the server's
  `general_unauthorized`/`function_disabled`/`function_no_active_deployment`
  errors the same way every other call does.

- **Exact request/response field names, read from source, not assumed**
  (`src/Praxy.Api/Endpoints/AccountEndpoints.cs`,
  `src/Praxy.Api/Endpoints/AuthDtos.cs`):
  - `PATCH /v1/account/name` — body `{"name": "..."}`
    (`UpdateNameRequest(string Name)`), returns `AppUserResponse`.
  - `PATCH /v1/account/password` — body
    `{"password": "...", "oldPassword": "..."}` — `oldPassword` is required
    unless the user has no password yet (OAuth-only account); the server
    enforces this, the SDK just needs to accept a nullable
    `oldPassword` parameter and pass it through.
  - `GET /v1/account/sessions` — returns `SessionListResponse
    {total, sessions: SessionResponse[]}`, each carrying `current: bool` —
    surface that flag; it is how a client tells its own session apart from
    others in the list.
  - `POST /v1/account/verification` — body `{"url": "..."}`
    (`SendVerificationRequest`) — the URL is validated server-side against the
    project's platform allowlist; the SDK does not validate it, just passes it
    through and lets a `400` surface as the existing field-error exception
    shape.
  - `PUT /v1/account/verification` — body `{"userId": "...", "secret": "..."}`,
    returns `AppUserResponse`. Same two-field shape `updateRecovery` already
    uses — mirror that method's structure exactly.
  - `GET /v1/account/roles` — returns
    `ResolvedRolesResponse {roles: string[], principal: string, scopes: string[]?}`.
  - `POST /v1/account/jwts` — body `{"durationSeconds": int?}`
    (`CreateJwtRequest`), returns `JwtResponse {jwt: string}`.

- **`TeamEndpoints.cs`'s membership routes need `ownerOnly` awareness.**
  `UpdateMembershipRoles` and some other membership calls require the caller
  to hold an `owner` role on that team server-side
  (`RequireTeamAccessAsync(..., ownerOnly: true)`). The SDK method itself does
  not need to replicate that check — the server enforces it — but do not
  assume every membership call succeeds for every signed-in user; a `401`
  from a non-owner is expected behavior to leave alone, not a bug in your new
  method.

- **`FunctionsService`'s async invoke returns a `202` with a queued
  execution, and there is nowhere to poll it afterward.** The data plane maps
  exactly one route —
  `dataPlane.MapPost("/{functionId}/executions", Invoke)`
  (`src/Praxy.Api/Endpoints/FunctionEndpoints.cs:134`) — and no
  `GET .../executions/{id}`. That read exists only on the console admin
  surface (`RequireOperatorFilter`-gated), which an app user's session cannot
  reach. This is a real, separate API gap, not something an SDK-only task can
  paper over: an app that calls `async: true` today has no way to ever learn
  what happened.

  Do not invent a poll method that calls a route which doesn't exist — it
  would 404 for every caller. Two honest choices: (a) expose `invoke` for sync
  only in this v1.1 surface and leave async out until the data plane grows a
  read route (name this explicitly as deferred, matching how `upsert` and
  anonymous sessions are already documented as known gaps rather than silently
  worked around); or (b) expose both, with the async variant's doc comment
  stating plainly that the returned execution can never be re-fetched through
  this SDK today. Either is defensible — state which you chose and why. Do
  **not** add the missing data-plane endpoint yourself; that is server scope
  this task doesn't own, and adding one API route as a side effect of an SDK
  task is exactly the kind of undiscussed scope growth CLAUDE.md asks
  sessions to avoid.

- **Match the existing package's error and null-handling conventions
  exactly** — `AccountService`/`TablesService` are short, dense, and
  deliberate about which server fields are optional. Read
  `sdk/flutter/praxy_core/lib/src/errors.dart` and one existing service fully
  before writing the first line of a new one; a service that doesn't match the
  established shape is worse for SDK consumers than one that's simply missing.

## Tests

`sdk/flutter/praxy_core/test/` — `dart test`, no server required;
`support/fake_transport.dart` is the existing test double.
`tables_service_test.dart` and `account_service_test.dart` are the shape to
follow exactly (a `FakeTransport` returning canned JSON, asserting the
service both sends the right request and decodes the right response).

- Each new method: request path/method/body sent correctly, response decoded
  correctly, and at least one error-mapping case (a `4xx` envelope maps to the
  right `PraxyException` subtype).
- The account gaps: same coverage as the existing `account_service_test.dart`
  cases, extended for each new method.
- Run `dart analyze .` from the workspace root — the project's `dart analyze`
  convention covers the whole pub workspace, not just the package you touched.

## Done means

- `dart test praxy_core` green, and `dart analyze .` clean from
  `sdk/flutter/` (the workspace root, not an individual package — see
  `CLAUDE.md`'s Commands section for the exact invocations).
- `flutter test praxy_flutter example` still green — confirms wiring the new
  services into `client.dart` didn't break the realtime/OAuth packages that
  depend on it.
- No change needed to `docs/openapi/v1.json` — this task adds no server
  endpoints, only client bindings to ones that already exist and are already
  documented.
- `git status` clean, conventional commits, on a new branch off `main`.
- Update `sdk/flutter/README.md` and each touched package's own `README.md`
  (real docs since Phase 9, per `CLAUDE.md`) to list the new methods.
- You do not need a running Praxy instance to verify this — the test suite is
  the verification. If you want an end-to-end sanity check, the example app
  (`sdk/flutter/example`) already runs against a real instance per
  `CLAUDE.md`'s Commands section; exercising one new method there is a nice
  extra, not a requirement.
- State in your final summary: the exact new method count added per service
  (matching the existing doc's "N methods, and nothing more" style).

## Deploying (only if the owner asks)

This task ships no server changes, so there is nothing to deploy on
`praxycore.dev`. If the owner wants the new SDK surface published (pub.dev or
similar), that is a separate decision — do not publish packages without being
asked.
