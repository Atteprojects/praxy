# Session task — Teams, Functions and Messaging services for the Flutter SDK

> **Status: shipped.** Verified 2026-09-03 against the code, not assumed — see `sdk/flutter/praxy_core/lib/src/services/{account,teams,functions}_service.dart`. No `MessagingService` — the prompt's own scope item 4 excluded it deliberately (messaging endpoints are console-only), so its absence is correct, not a gap.
> No `flutter-sdk-parity-report.md` was written at the time, so this prompt used to look like
> outstanding work when scanning `docs/handoff/` for prompts without reports.

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
others — it touches Dart, not C# or the console. Items #1–#5 are merged to
`main` as of this prompt's last edit (2026-08-20) — #4 added the
`.github/workflows/ci.yml` this prompt's new scope item 7 extends, and #5
merged the branch-protection rule the "Branch protection" section below
describes.

**One dependency, now closed**: `docs/handoff/function-execution-read-prompt.md`
merged (`50eb38c`) — the data plane now has
`GET /v1/functions/{functionId}/executions/{executionId}`, scoped to the
caller's own execution (`TriggeredBy` match; a signed-in session or an API key
each get their own identity now — `user:<id>` / `key:<id>` — verified live
against a real Docker-built container, including the actual regression case:
one key cannot read a different key's execution). `FunctionsService` below
can bind the full sync/async/poll trio without a fallback branch.

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
3. **`FunctionsService`** (new): the data-plane invoke
   (`POST /v1/functions/{functionId}/executions`, sync and async) plus
   `getExecution` (`GET .../executions/{id}`, now real — see the dependency
   note above). Deployment management is a console/operator concern, not an
   app's, and stays out of scope.
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
7. **Add a CI job for the Flutter SDK to `.github/workflows/ci.yml`** — the
   workflow currently has two jobs (`Build and test API`, `Build console`);
   this task adds a third, `Build and test Flutter SDK` (or similar exact
   name — pick one and use it consistently), covering
   `dart pub get && dart test praxy_core praxy_codegen && flutter test
   praxy_flutter example && dart analyze .`, same invocations as
   `CLAUDE.md`'s Commands section and this prompt's own Done means. This is
   new scope added after the rest of this prompt was written — the SDK work
   above was previously going to ship with no CI coverage at all.

## Branch protection — read before opening a PR

`main` now requires a passing PR with two required status checks (`Build and
test API`, `Build console`) before merge — added this session, `enforce_admins:
true`, so this applies to you too, no direct pushes or local
`git merge && git push` to `main`. Push your branch and open a PR
(`gh pr create`); do not attempt `gh pr merge` or a local merge yourself —
stop once the PR is open and CI is green, and let the owner merge it (same
as every other item in this backlog).

Your new `Build and test Flutter SDK` job is **not** in that required-checks
list yet — it can't be, since a required check has to have actually reported
on this repo at least once before GitHub will let you add it. Once your PR's
run reports the new job's exact name, mention in your final summary that the
owner may want to add it to the required list (`gh api -X PUT
repos/Atteprojects/praxy/branches/main/protection ...`, same shape used to
set up the other two) — but do not make that repo-settings change yourself;
it needs the owner's explicit go-ahead each time, the same way this session's
addition of branch protection itself did.

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

- **`GetDataPlaneExecution` is scoped to the caller's own execution, not
  anyone the function's `execute` role would let invoke it.** Bind
  `getExecution` normally — same `FunctionExecutionResponse` shape the sync
  path already decodes, just fetched by id — but don't be surprised by a
  `404` on someone else's execution id, including one from a *different* API
  key with the identical scope. That's not a bug to route around: two keys
  never share an identity, and a session can only ever fetch what it itself
  triggered. The server enforces this; the SDK doesn't need to replicate it,
  just let the resulting `PraxyException` surface like any other.

- **The CI job needs the Flutter SDK, not just Dart** — `praxy_core`/
  `praxy_codegen` are pure-Dart packages, but `praxy_flutter`/`example` are
  real Flutter packages (`pubspec.yaml` constraints: Dart `^3.12.2`, Flutter
  `>=3.24.0`), and `flutter test` needs the Flutter SDK on the runner, which
  bundles its own pinned Dart — a bare `dart-lang/setup-dart` action is not
  enough. Use `subosito/flutter-action@v2` with `channel: stable` (satisfies
  both lower bounds comfortably; no need to chase an exact patch pin the way
  `docs/research/dotnet-stack.md` does for NuGet — Flutter's stable channel
  is the whole point of a floating channel action). Run `dart pub get` from
  `sdk/flutter/` once (the native pub workspace resolves all four packages
  together, per `CLAUDE.md`) before `dart test`/`flutter test`/`dart analyze`.

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
- **A real, verified-green GitHub Actions run for your new `Build and test
  Flutter SDK` job** — same requirement item #4 had for its own new CI job,
  and for the same reason: "the YAML looks right" is not the same as a green
  run. Push the branch, open the PR, and confirm all three jobs (the two
  existing ones plus yours) pass — paste or describe the real result in your
  final summary.
- State in your final summary: the exact new method count added per service
  (matching the existing doc's "N methods, and nothing more" style), the new
  CI job's exact name as it reported, and a note that the owner may want to
  add it to `main`'s required status checks (see "Branch protection" above).

## Deploying (only if the owner asks)

This task ships no server changes, so there is nothing to deploy on
`praxycore.dev`. If the owner wants the new SDK surface published (pub.dev or
similar), that is a separate decision — do not publish packages without being
asked.
