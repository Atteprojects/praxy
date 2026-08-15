# Research — Flutter/Dart SDK design

Source: study of `appwrite/sdk-for-flutter` v25.4.0, `appwrite/sdk-for-dart` v27.0.0, and
`appwrite/sdk-generator`. Distilled to the decisions and traps that affect Praxy.

---

## Decisions taken

### Package structure — three packages, not two

Appwrite publishes two packages (`appwrite` for Flutter, `dart_appwrite` for server) that are ~90% identical
code generated from the same templates. They have drifted to different major versions (25.x vs 27.x) against the
same server. That split exists only because the Flutter package needs Flutter-only dependencies — which is a
platform-behaviour problem, solved by injection rather than by forking the SDK.

```
packages/
  praxy_core/      pure Dart, depends on package:http only.
                   Transport, errors, models, services, query/id/permission builders.
                   Works on server and client alike.
  praxy_flutter/   depends on praxy_core. SecureSessionStore, OAuth web-auth,
                   realtime WebSocket, widget glue.
  praxy_codegen/   optional dev dependency. Generates typed column constants.
```

Two injected seams carry all platform difference:

```dart
abstract interface class Transport {
  Future<TransportResponse> send(TransportRequest r);
  void close();
}

abstract interface class SessionStore {
  Future<Session?> read();
  Future<void> write(Session s);
  Future<void> clear();
}
```

One conditional import in the whole SDK, inside the transport factory, for the self-signed-cert override.
Use `export` barrels, not `part`/`part of` — Appwrite's 280-part `models.dart` forces the analyzer to load the
whole library to touch one model.

### Sessions — token flow, not cookies

**Do not build a cookie jar.** Appwrite's mobile session story is a `PersistCookieJar` in the app documents
directory, and it is the single largest source of user-reported breakage. Praxy uses an opaque session secret sent
as `X-Praxy-Session`, stored in `flutter_secure_storage` (Keychain / Keystore), keyed by project id.

This survives endpoint host changes, survives app restart deterministically, and is trivially testable.

Web keeps HttpOnly cookies plus `withCredentials`, because a long-lived secret in `localStorage` is XSS-readable.
The pluggable `SessionStore` is what lets the two platforms differ without forking.

### Typed rows without build_runner

The table reference carries the codec, so generics flow through the whole API and inference does the work.

```dart
typedef RowDecoder<T> = T Function(Map<String, dynamic> data, RowMeta meta);
typedef RowEncoder<T> = Map<String, dynamic> Function(T value);

final class RowCodec<T> {
  const RowCodec({required this.decode, required this.encode});
  final RowDecoder<T> decode;
  final RowEncoder<T> encode;
  static const RowCodec<Map<String, dynamic>> raw = RowCodec(decode: _rawDecode, encode: _rawEncode);
}

final class TableRef<T> {
  const TableRef(this.databaseId, this.tableId, {required this.codec});
  final String databaseId, tableId;
  final RowCodec<T> codec;
  static TableRef<Map<String, dynamic>> raw(String db, String table) =>
      TableRef(db, table, codec: RowCodec.raw);
}
```

The user's schema is one hand-written file, ~10 lines per table:

```dart
abstract final class Db {
  static const todos = TableRef<Todo>('main', 'todos', codec: Todo.codec);
  static final logs  = TableRef.raw('main', 'logs');   // stay untyped where you want
}
```

Service methods are generic; call sites need zero casts and zero codegen:

```dart
final page = await px.tables.list(Db.todos, queries: [Query.equal(Todos.done, false)]);
for (final todo in page.rows) print(todo.title);   // todo is Todo
```

`praxy_codegen` is deliberately **not** a `build_runner` builder — it runs on demand, emits committed files that
show up in diffs, and never imposes a watcher on every save.

### Query is a value object, not a pre-encoded string

Appwrite's `Query.equal()` returns a JSON-encoded `String`, which forces `Query.or()` to `jsonDecode` its own
inputs — the tell that the value type was discarded too early. Praxy keeps `Query` as a value and types columns:

```dart
final class Col<T> { const Col(this.name); final String name; }

static Query equal<T>(Col<T> col, T v) => Query._('equal', attribute: col.name, values: [_enc(v)]);
```

`Query.equal(Todos.done, 'yes')` becomes a compile error. `Query.raw()` stays as the dynamic escape hatch.

### Realtime returns a real Stream

Appwrite's `subscribe` returns a wrapper object with three different teardown verbs (`unsubscribe`, `close`,
`disconnect`). Praxy returns a `Stream`, so lifecycle is `StreamSubscription.cancel()` — the verb every Flutter
developer already knows, and one that `StreamBuilder` manages for free.

```dart
Stream<RowEvent<T>> rows<T>(TableRef<T> table, {String? rowId, List<Query> queries});
Stream<ConnectionState> get connection;   // transport health, separate from data
Future<void> get ready;
```

Sealed events give exhaustive switching instead of string matching:

```dart
sealed class RowEvent<T> { … }
final class RowCreated<T> extends RowEvent<T> { final T row; }
final class RowUpdated<T> extends RowEvent<T> { final T row; }
final class RowDeleted<T> extends RowEvent<T> { }
```

Plus a `liveList<T>()` helper doing REST-snapshot + realtime-patch, because every app hand-rolls it.

Socket policy: one socket per client, multiplexed, reference-counted — opens on first listener, closes after a
grace period when the last cancels. Transport errors go to the `connection` stream, never injected into data
streams.

### Sealed error hierarchy

```dart
sealed class PraxyException implements Exception { final String message; }

class PraxyApiException extends PraxyException {
  final int status;
  final String type;         // stable machine-readable code
  final String? requestId;   // echo the server trace id
}
final class PraxyAuthException       extends PraxyApiException {}
final class PraxyNotFoundException   extends PraxyApiException {}
final class PraxyConflictException   extends PraxyApiException {}
final class PraxyRateLimitException  extends PraxyApiException { final Duration? retryAfter; }
final class PraxyValidationException extends PraxyApiException { final Map<String, List<String>> fields; }

final class PraxyNetworkException extends PraxyException {
  final Object cause;          // never collapsed to a String
  final StackTrace stackTrace;
  final bool isTimeout;
}
final class PraxyDecodeException extends PraxyException { final String field, expected; }
```

This is what lets a caller tell "offline" from "server rejected you" — impossible with Appwrite's single flat
`AppwriteException`, whose `call()` collapses every `SocketException`, TLS failure and timeout to `e.toString()`
and loses the stack.

---

## Server-side requirements this imposes on Praxy

These are SDK findings that constrain the **API**, so they belong in Phase 0–4 work, not Phase 5:

1. **Ship an API version header from day one.** Appwrite's `X-Appwrite-Response-Format` lets the server shape
   responses for older SDKs. Cheap on day one, impossible to retrofit. Praxy: `X-Praxy-Api-Version`.
2. **Return a request id on every response and echo it in errors.** The SDK surfaces it as
   `PraxyApiException.requestId`; without it, support is guesswork.
3. **Errors need a stable machine-readable `type` and structured field errors** for validation failures, so the
   SDK can populate `PraxyValidationException.fields`.
4. **Rate-limit responses must carry `Retry-After`.**
5. **The realtime protocol must have an app-level `connected` frame**, and the server must tolerate — not
   policy-violation-close — a subscribe frame that arrives early. Appwrite closes with 1008 in that case, and
   because its client then reconnects and re-sends, users hit an infinite reconnect loop.
6. **PATCH must be genuinely partial.** A full-object round-trip silently rewrites fields the caller never
   touched.
7. **OAuth should use the token flow** (callback carries `userId` + `secret`, exchanged for a session) rather than
   the cookie flow, and should support **PKCE** so an intercepted callback secret alone is insufficient.

---

## Traps found in Appwrite's SDK, worth not repeating

- `setJWT` writes `config['jWT']` while the realtime code reads `config['jwt']` — the realtime JWT auth path never
  fires. Case-sensitivity bug that a typed config object would have prevented.
- `Row.convertTo` takes `T Function(Map<String, dynamic>)` but `RowList.convertTo` takes `T Function(Map)`. Dart
  parameters are contravariant, so passing the same `fromJson` tear-off to both is a static error. **One decode
  signature across the SDK.**
- Enums deserialize via `values.firstWhere(...)`, which **throws on any value the SDK doesn't know** — so adding a
  server-side enum variant breaks every older client. Use a wrapper type that round-trips unknown values.
- The cookie write in the response interceptor is **not awaited** (`_saveCookies(response).then(...)`), so killing
  the app right after login can lose the session.
- Reconnect backoff is a step function with no jitter (`1s / 5s / 10s / 60s`), putting every client in lockstep.
  Use exponential backoff with full jitter.
- Constructing two `Realtime` instances opens two sockets — the cause of long-standing duplicate-connection
  reports. Reference-count instead.
- `AppwriteException`'s constructor is positional `(message, code, type, response)` while its fields declare
  `(message, type, code, response)`. Reliable footgun.

---

## Corrections to common documentation

- **iOS needs no `Info.plist` entry** for the `flutter_web_auth_2` OAuth flow. `ASWebAuthenticationSession` takes
  the callback scheme as an API argument and intercepts it in-process; the URL never reaches the OS router. The
  only requirement is deployment target ≥ iOS 11. Appwrite's docs imply otherwise. Registering `CFBundleURLTypes`
  anyway is harmless.
- **Android does need** a `CallbackActivity` intent filter, and `android:taskAffinity=""` on it — which Appwrite's
  README omits.
- `ASWebAuthenticationSession` shows a "…wants to use … to Sign In" system dialog unless
  `prefersEphemeralWebBrowserSession` is set. Real conversion cost; make it a conscious choice and document it.

---

## v1 SDK surface (Phase 5 scope)

About 20 public methods, and nothing more.

- **Client/infra:** `Praxy(...)`, `Transport` + `HttpTransport`, `SessionStore` + `MemorySessionStore` /
  `SecureSessionStore`, exception hierarchy, `Query`, `Col<T>`, `Uid.unique()`, `Permission`, `Role`
- **Account (8):** `get`, `create`, `createEmailSession`, `createAnonymousSession`, `createOAuth2Session`,
  `deleteSession`, `updatePrefs`, `createRecovery`/`updateRecovery`
- **Tables (7):** `list<T>`, `get<T>`, `create<T>`, `upsert<T>`, `update<T>`, `delete<T>`, `liveList<T>`
- **Realtime (4):** `rows<T>`, `account`, `connection`, `close`

Out of v1: storage, functions, teams, messaging, avatars, locale, transactions, all server-admin surfaces. Each is
additive and none constrains the shape of the four above. Every service shipped is a compatibility commitment.
