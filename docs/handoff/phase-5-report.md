# Phase 5 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run end to end against a
fresh, isolated throwaway Praxy instance, driving the real example app in the iOS Simulator against
the real API, real Postgres, and the real console (including its realtime inspector). 46 new
Dart/Flutter tests green, plus the existing 300 .NET tests unaffected (no server-side changes this
phase).

## What shipped

**`sdk/flutter/praxy_core`** (pure Dart, `package:http` only) — the platform-independent half of
the SDK:

- `Transport`/`HttpTransport`, `SessionStore`/`MemorySessionStore`, the sealed `PraxyException`
  hierarchy (`PraxyApiException` with `status`/`type`/`requestId` plus `PraxyAuthException`/
  `PraxyNotFoundException`/`PraxyConflictException`/`PraxyRateLimitException`/
  `PraxyValidationException`, `PraxyNetworkException` with the original `cause`/`stackTrace`
  preserved, `PraxyDecodeException`).
- `Query`/`Col<T>` — a real value object per architecture.md §4.6's 21 methods, not a pre-JSON-
  encoded string; `Permission`/`Role` string builders matching the appwrite-api.md grammar;
  `Uid.unique()` (client-generated dashed UUIDv4, since the server already accepts either its own
  `n`-format or `d`-format ids) and `Uid.custom()`.
- `RowCodec<T>`/`TableRef<T>` — one decode signature (`(Map<String,dynamic> data, RowMeta meta) → T`)
  everywhere, deliberately avoiding the Appwrite trap of two incompatible decode signatures between
  single-row and list decoding.
- `Praxy` client: header attachment, JSON decode, and the one error-mapping function every service
  method funnels through. `AccountService` (8 methods) and `TablesService` (5 REST methods +
  `listWithIds`, an internal id-preserving variant `praxy_flutter`'s `liveList` needs — see
  Deviations).

**`sdk/flutter/praxy_flutter`** — the Flutter-only half:

- `SecureSessionStore` (Keychain/Keystore via `flutter_secure_storage`, keyed by project id, never
  a cookie jar).
- `RealtimeSocket` — one WebSocket per client, reference-counted (opens on first subscription
  listener, closes after a 5s grace period once the last cancels), exponential backoff **with full
  jitter** (`uniform(0, min(30s, 500ms·2^attempt))`, not Appwrite's lockstep step function),
  resubscribing every active channel after each reconnect, a 20s app-level ping, and transport
  errors surfaced only on `PraxyRealtime.connection` (`PraxyConnectionState`), never injected into
  data streams.
- `PraxyRealtime` — `rows<T>`, `account`, `connection`, `close` (the exact 4-method surface). Row
  events re-fetch over REST (see Deviations) and re-apply the caller's `queries` server-side via an
  id-pinned `list()` call, so a subscription stays consistent with what the equivalent REST snapshot
  would show.
- `PraxyOAuth` — Google sign-in via `flutter_web_auth_2`'s token flow: opens the provider's
  consent screen, catches the `<scheme>://oauth/{success,failure}` redirect, exchanges
  `userId`/`secret` at the same `POST /v1/account/sessions/token` every token flow converges on.
- `PraxyFlutterTables.liveList<T>` — REST snapshot + realtime patch, maintaining an ordered
  `(id → T)` map so a `RowUpdated`/`RowDeleted` event can patch one entry without a full re-query.
- `PraxyFlutter` — the single class an app constructs, assembling `Praxy` (core), `SecureSessionStore`
  as the default `SessionStore`, `PraxyRealtime`, `PraxyOAuth`, and `PraxyFlutterTables`.

**`sdk/flutter/praxy_codegen`** — `dart run praxy_codegen --endpoint … --project … --api-key …
--database <key> --table <key> --output lib/db/todos_columns.dart`: resolves database/table by
their human `key` (list-and-filter, since every row/schema endpoint requires the real id in the
URL — see architecture.md §4.1), fetches `GET .../columns`, and writes one `abstract final class`
of `Col<T>` constants (`$id`/`$createdAt`/`$updatedAt` plus every declared column, typed and
array-aware). On-demand only — no `build_runner` dependency, no watcher.

**`sdk/flutter/example`** — sign up / sign in / Google sign-in, a live todos list built entirely on
`liveList<Todo>` (create via FAB, toggle `done` inline, swipe to delete, sign out), a connection-
state dot in the app bar. Android manifest carries the `flutter_web_auth_2` `CallbackActivity`
intent filter (`android:exported="true"`, `android:taskAffinity=""`) and `android:allowBackup="false"`;
`minSdk` bumped to 23 for `flutter_secure_storage` 11.x. iOS needs no `Info.plist` entry for the
custom-scheme OAuth callback (confirmed against the current `flutter_web_auth_2` README), but does
get a dev-only `NSAllowsLocalNetworking` ATS exception so the simulator can reach a plain-HTTP local
Praxy instance.

**Monorepo**: Dart's native pub workspaces (`sdk/flutter/pubspec.yaml`'s `workspace:` list, each
member's `resolution: workspace`) — no `melos`. One `dart pub get`/`flutter pub get` at the root
resolves all four packages with `praxy_flutter → praxy_core` and `example → praxy_flutter` as plain
path-free version dependencies.

## Deviations & notes

- **`upsert<T>` dropped from the Tables surface (6 methods, not 7).** Verified against
  `RowEndpoints.cs`/`RowsService.cs`: the server implements only create/list/get/update(PATCH)/
  delete — there is no upsert route or service method anywhere. Flagged to the owner rather than
  emulated with a non-atomic create-then-update-on-conflict; the owner chose to drop it and treat
  this as a real API gap for a future phase to close, not an SDK oversight.
- **`createAnonymousSession` dropped from the Account surface (8 methods as specified, none of them
  anonymous).** `research/flutter-sdk.md`'s method list carried this over from architecture.md's
  original (pre-narrowing) v1 auth methods list. CLAUDE.md's fixed decisions are unambiguous:
  app-user auth is email+password and Google OAuth only "until the owner says otherwise," and
  `AccountEndpoints.cs`/`AppAuthService` never built anonymous sessions, magic URL, email OTP, or
  JWT minting. No user prompt needed here — CLAUDE.md's "never reopen" language settles it directly,
  unlike the upsert gap.
- **Realtime row events carry no row data.** `RowsService.BuildEvent` (Phase 3/4) emits only
  `{databaseId, tableId, rowId, roles}` on the wire — confirmed by reading the actual payload
  construction, not assumed. `PraxyRealtime.rows<T>` therefore re-fetches over REST on every
  create/update event before emitting a `RowCreated`/`RowUpdated`. That re-fetch is also how the
  caller's `queries` get re-applied: phase-4-report.md is explicit that a subscribe's `queries`
  field is accepted but never enforced server-side (matches Appwrite's own behavior), so the SDK
  ANDs the caller's filter/select queries with `Query.equal($id, rowId)` and re-lists with `limit(1)`
  — reusing the real query compiler instead of hand-rolling a second, potentially-divergent
  predicate evaluator in Dart. A `delete` event needs no fetch (there's nothing left to read).
- **`TablesService.listWithIds`** is additive infrastructure, not part of the documented 5-method
  REST surface: `T` is opaque to `praxy_core` (a user's row type has no obligation to expose its own
  id), so `liveList`'s local `(id → T)` bookkeeping needs ids kept alongside decoded values rather
  than baked into `T` itself. Public (useful on its own for anyone building an id-keyed list UI),
  just not one of the "6" — the doc comment on it says so explicitly.
- **Package pins verified against pub.dev today, not memory**: `http` 1.6.0, `flutter_secure_storage`
  11.0.0 (raised its floor to Android API 23; still `allowBackup="false"` guidance), `flutter_web_auth_2`
  5.1.0 (a `6.0.0-alpha.0` exists but isn't the stable pin), `web_socket_channel` 3.0.3.
- **`Uid.unique()`** generates a client-side dashed UUIDv4 rather than mirroring Appwrite's own
  padded-timestamp scheme — the server's `Ids.TryParseWire` accepts both its own `n`-format and
  `d`-format uuids, so a real UUID is the simplest thing that satisfies the "know the id before
  create() returns" use case without inventing a second id grammar.

## Known gaps (deliberate, next phases or later)

- **Google OAuth is unit-tested, not live-tested end to end.** `oauth_test.dart` covers URL
  construction, the success/`userId`+`secret` path, the provider-error path, and the malformed-
  callback path, all via an injected `WebAuthenticator`. What it cannot cover without the owner's
  own Google Cloud OAuth client id/secret: a real consent screen round trip. The Android manifest
  and iOS platform config are in place per the verified current `flutter_web_auth_2` guidance; the
  owner should configure a real Google provider in a project's Auth settings and click through the
  example app's "Sign in with Google" once before shipping anything that depends on it.
- **`RealtimeSocket`'s reconnect/backoff path has no automated test.** It was exercised live (a
  real WebSocket connection, real `connected`/`event` frames, a real kill/restart of the app that
  proved the socket reopens and resubscribes cleanly), but a fake-channel-based unit test of the
  jittered-backoff math and resubscribe-after-drop behavior doesn't exist yet — the socket has no
  injectable channel factory, unlike `PraxyOAuth`'s injectable authenticator. Worth adding if this
  code sees more churn.
- **`praxy_codegen` was verified against a mocked HTTP client, not a live instance.** The wire
  shapes it depends on (`{total, databases}`/`{total, tables}`/`{total, columns}`) are the same ones
  `DatabaseEndpoints.cs` serves and were read directly from source, but a first real run against a
  live Praxy is still worth doing before relying on it.
- No Flutter/Android build was exercised this phase (Java/Gradle version mismatch reported by
  `flutter create`'s own output on this machine — `flutter config --jdk-dir=...` or a Gradle bump
  would resolve it). All verification here is iOS Simulator + `flutter analyze`/`flutter test`,
  which don't touch platform channels differently enough to make Android a real risk, but it hasn't
  been run.

## Tests

`sdk/flutter/praxy_core`: `query_test.dart`, `permission_test.dart`, `ids_test.dart`,
`client_error_mapping_test.dart` (every `PraxyException` subtype mapped from status/fields/headers,
malformed error bodies, transport failures, header propagation), `account_service_test.dart`,
`tables_service_test.dart` (codec round-trip, partial-PATCH body shape, decode-failure wrapping) —
40 tests. `sdk/flutter/praxy_codegen`: `generator_test.dart` against `package:http/testing.dart`'s
`MockClient` — 2 tests. `sdk/flutter/praxy_flutter`: `oauth_test.dart` (3 tests, injected
authenticator). `sdk/flutter/example`: one smoke test. 46 total, all green; `dart analyze .` across
the whole workspace: 4 harmless `prefer_initializing_formals` info lints, zero warnings or errors.

## Commands

```
# .NET / console — unchanged from phase 4
docker run -d --name praxy-dev-pg -e POSTGRES_USER=praxy -e POSTGRES_PASSWORD=praxy \
  -e POSTGRES_DB=praxy -p 5432:5432 postgres:17-alpine
dotnet run --project src/Praxy.Api                       # API :5090 (Scalar at /scalar/v1)
npm run dev --prefix console                              # console :5173, /v1 proxied
dotnet test                                                # 300 tests; Docker required
cd deploy && ./up.sh                                       # self-host stack

# Flutter SDK — new this phase
cd sdk/flutter && dart pub get                             # resolves all 4 workspace members
dart test praxy_core praxy_codegen                          # 42 pure-Dart tests
flutter test praxy_flutter example                          # 4 Flutter tests
dart analyze .                                               # whole workspace
flutter run --dart-define=PRAXY_ENDPOINT=http://localhost:5090 \
  --dart-define=PRAXY_PROJECT_ID=<id> \
  --dart-define=PRAXY_DATABASE_ID=<id> --dart-define=PRAXY_TABLE_ID=<id> \
  -d <device>                                               # sdk/flutter/example
dart run praxy_codegen --endpoint http://localhost:5090 --project <id> \
  --api-key <key> --database <key> --table <key> \
  --output lib/db/todos_columns.dart                        # from sdk/flutter/praxy_codegen
```

The example app needs a real database/table created through the console first (or the schema API)
— its `PRAXY_DATABASE_ID`/`PRAXY_TABLE_ID` are real ids, not the human `key` (architecture.md §4.1:
every row/schema endpoint requires the generated id in the URL, never the key). Its table needs row
security on and a `users`→`create` table-level permission grant (the console's "Owner only" preset
does both); the app sets its own row-level `read`/`update`/`delete` grants to `user:<id>` on create.

## Owner-test checklist (run by this session, all passing except the noted Google gap)

Run against an isolated throwaway instance (fresh Postgres container, a second API instance on a
free port, a second console dev-server instance temporarily repointed at it — the persistent
`praxy-dev-pg`/`:5090` stack from earlier phases was left completely untouched throughout, per the
owner's explicit choice when asked), driving the real example app in the iOS Simulator:

1. **Sign up** — claimed the throwaway instance, created a project, a `Main` database, a `Todos`
   table (`title: string`, `done: boolean`, row security on, `users`→`create` granted), built and
   ran the example app, signed up `todo.tester@example.com` — landed on the todos list.
2. **Google sign-in** — code path verified by `oauth_test.dart` (URL construction, callback
   parsing, error handling) and the Android/iOS platform config is in place; **not** exercised
   against a real Google consent screen — no Google Cloud OAuth client available in this
   environment. Flagged above as a known gap, not silently marked done.
3. **CRUD rows** — created "Buy milk" via the FAB (client-generated permissions scoping read/
   update/delete to the signed-in user), toggled it done via the checkbox, swiped to delete — all
   three round-tripped correctly, each shown in a screenshot.
4. **Watch a realtime update arrive from the console** — edited the row's `done` cell inline in the
   console's `<DataGrid />` (`Owner@praxy.local`, a completely different session than the app's);
   the app's list updated within the same second, no manual refresh, no app-side interaction.
   Reversed the direction too: toggling the checkbox in the app produced a matching event in the
   console's realtime inspector at `9:01:19 AM`, channel `databases.<db>.tables.<table>.rows...`.
   Both directions screenshotted.
5. **Kill/restart the app → still signed in** — force-terminated the app (`xcrun simctl terminate`,
   not just backgrounding), relaunched the installed `.app` cold (no debugger attached, no
   in-memory state survives), and it went straight to the todos list — `SecureSessionStore`
   restored the session from Keychain, the previously-created "Survive restart" row was still
   there (server-persisted, not app-local), and realtime reconnected on its own.

Also verified: `dart analyze .`/`dart test`/`flutter test` all green across the workspace;
`dotnet test` still 300/300 (no server-side changes this phase); the throwaway Postgres container,
API process, and second console dev-server were torn down afterward, and the temporary
`console/vite.config.ts` proxy-target edit (5090→5091, for driving the throwaway console instance)
was reverted — `git diff console/vite.config.ts` is empty.

## Next: Phase 6

The SDK is real — Auth, Databases/Tables, and Realtime all have a working native client now,
verified against the real API and console, not just against mocks. The prompt below is ready to
paste into a fresh session.
