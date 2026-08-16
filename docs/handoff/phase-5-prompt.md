# Phase 5 — session prompt

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 5 (Flutter SDK) of Praxy**, a self-hosted BaaS (.NET 10 API +
PostgreSQL + Vite/React console). Phases 0–4 shipped everything the SDK talks to: instance
claim, full app-user auth (email+password + Google OAuth token flow with PKCE, teams/memberships,
the one `IRoleResolver`), the dynamic schema engine, the full data plane (row CRUD, the 24-method
query DSL, keyset pagination, permission filtering), and realtime (WebSocket endpoint, message-mode
protocol, permission-filtered fan-out, tickets for non-browser auth). This session is different in
kind from the last four: it's a **Dart/Flutter client**, not another slice of the .NET API or
console. The API surface is done and stable — this phase consumes it, and per CLAUDE.md's cross-
phase rules, does not reopen it. The plan is settled — implement, don't re-plan.

Read first, in this order:
1. `docs/handoff/phase-4-report.md` — what realtime actually shipped and, critically, its
   deviations that shape the SDK: the exact message-mode protocol shapes (`connected`/`response`/
   `event`/`pong`/`error`), that a subscribe's `queries?` field is **accepted but never applied as
   a server-side filter** (Appwrite's own actual behavior, not a Praxy gap) — which means
   `liveList<T>`'s realtime half must apply the same query client-side against incoming events to
   stay consistent with its REST snapshot, or it will show rows the original query would have
   excluded; the ticket endpoint (`POST /v1/realtime/ticket`, single-use, 60s) is exactly the
   non-browser auth path this SDK needs, since a Dart `WebSocket.connect` can't attach the
   `praxy_session_<projectId>` cookie a browser would.
2. `docs/research/flutter-sdk.md` — the full spec for this phase, already written from studying
   Appwrite's Flutter/Dart SDKs and their concrete bugs. Everything in it is a decision already
   made: three packages (`praxy_core` pure Dart / `praxy_flutter` / `praxy_codegen`), the
   `Transport`/`SessionStore` injection seams, token-flow sessions (never a cookie jar —
   `flutter_secure_storage` keyed by project id), `RowCodec<T>`/`TableRef<T>` for codegen-free
   typed rows, `Query` as a typed value object (not Appwrite's pre-JSON-encoded string), realtime
   as a real `Stream<RowEvent<T>>` with sealed `RowCreated`/`RowUpdated`/`RowDeleted` events and a
   `liveList<T>` REST-snapshot + realtime-patch helper, the sealed `PraxyException` hierarchy, and
   — read the "Traps found in Appwrite's SDK" section closely — several specific bugs
   (case-sensitive `setJWT` config key, contravariant decode-signature mismatch, `values.firstWhere`
   enum decoding that breaks on any server-added variant, an unawaited cookie write, lockstep
   reconnect backoff, double-socket construction) to not repeat.
3. `docs/roadmap.md`'s Phase 5 scope block and owner-test checklist (your acceptance gate) and
   `docs/architecture.md` for the wire-level shapes the SDK must match (§8 API conventions — request
   id header, error envelope; §6 realtime channel grammar; §4.6 query DSL methods).
4. `docs/research/appwrite-api.md`'s query-DSL and error-envelope sections — the exact JSON shapes
   `Query.equal()` etc. must serialize to, and the `{message, code, type, version, requestId,
   fields?}` error envelope `PraxyApiException` deserializes from.

Build exactly the roadmap's Phase 5 scope — flutter-sdk.md's "v1 SDK surface" section is the literal
method list, don't add to it:

- **`sdk/flutter/praxy_core`** (pure Dart, `package:http` only): `Transport` interface +
  `HttpTransport`, `SessionStore` interface + `MemorySessionStore`, the sealed exception hierarchy
  (`PraxyApiException` with `status`/`type`/`requestId`, `PraxyNetworkException`,
  `PraxyDecodeException`, plus the four typed subclasses), `Query`/`Col<T>` typed query builders
  matching every method in architecture.md §4.6, `Uid.unique()`, `Permission`/`Role` string
  builders matching `research/appwrite-api.md`'s permission grammar (`action("role")`, roles
  `any`/`guests`/`users`/`user:<id>`/`team:<id>`/`team:<id>/<role>`/`member:<id>`/`label:<x>`),
  `RowCodec<T>`/`TableRef<T>`, and the account (8) + tables (7) + realtime (4) methods.
- **`sdk/flutter/praxy_flutter`**: `SecureSessionStore` (`flutter_secure_storage`), the realtime
  WebSocket client — connect via ticket (mint through `POST /v1/realtime/ticket`, pass as
  `?ticket=` on the WS URL per architecture.md §6), one socket per client reference-counted (open
  on first `Stream` listener, close after a grace period when the last cancels), exponential
  backoff **with full jitter** on reconnect (not Appwrite's lockstep step function), transport
  errors surfaced on the separate `connection` stream never injected into data streams; Google
  OAuth via `flutter_web_auth_2` (Android needs a `CallbackActivity` intent filter with
  `android:taskAffinity=""` — flutter-sdk.md has the exact gap in Appwrite's own README; iOS needs
  no `Info.plist` entry, `ASWebAuthenticationSession` takes the callback scheme as an API argument).
- **`sdk/flutter/example`**: a minimal app exercising the full surface — sign up, Google sign-in,
  row CRUD against a table, a live `liveList` view.
- **`sdk/flutter/praxy_codegen`** (optional dev dependency, not a `build_runner` builder): emits
  typed column constants on demand into a committed file.

Constraints that hold: this SDK talks to the API exactly as documented — no server-side changes
this phase. If you find the API surface doesn't actually support something flutter-sdk.md assumes,
stop and flag it rather than quietly working around it; the settled decision is that Phase 0–4 got
the server right, so a mismatch is more likely a misreading of the current code than a real gap —
verify against the actual endpoint (start from `src/Praxy.Api/Endpoints/`) before concluding
otherwise. Dart/Flutter tooling conventions apply where CLAUDE.md's .NET/TS-specific rules don't:
use whatever the current stable Dart/Flutter SDK and `melos`-or-plain-workspace convention is for a
three-package monorepo — there's no pre-verified pin file for this stack the way
`docs/research/dotnet-stack.md` exists for .NET, so verify current package versions (`http`,
`flutter_secure_storage`, `flutter_web_auth_2`, `web_socket_channel` or equivalent) against pub.dev
yourself rather than trusting training-data versions, the same spirit as dotnet-stack.md's own
"machine-verified" pins.

I have full permission for package installs and edits inside this repo. Use subagents where useful,
especially for researching current Dart/Flutter package versions and Android/iOS platform
configuration specifics.

When done: run the roadmap's Phase 5 owner test yourself (run the example app against local Praxy
— sign up, Google sign-in, CRUD rows, watch a realtime update arrive from the console's realtime
inspector you already have from Phase 4, kill/restart the app → still signed in), then follow the
handoff protocol at the bottom of `docs/roadmap.md`: write `docs/handoff/phase-5-report.md` and
`docs/handoff/phase-6-prompt.md`, update CLAUDE.md's Commands section if it changed, and print the
Phase 6 prompt for the owner.
