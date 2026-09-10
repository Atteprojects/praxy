# praxy_core

Pure-Dart core of the [Praxy](https://github.com/<your-fork-or-org>/praxy) SDK: the `Praxy` client,
its `Transport`/`SessionStore` platform seams, typed `Query`/`Permission`/`Role` builders,
`RowCodec`/`TableRef` for codegen-free typed rows, and the sealed `PraxyException` hierarchy.
`package:http` is its only dependency, so this package works identically on the Dart VM and in
Flutter — use it directly for CLI tools and server-side scripts that talk to a Praxy instance
without pulling in Flutter. Four services: `account` (15 methods — auth, sessions, verification,
JWTs), `tables` (typed row CRUD), `teams` (teams and memberships), and `functions` (data-plane
invocation).

Realtime and Google OAuth need platform capabilities this package deliberately doesn't depend on (a
WebSocket stack, a browser-based auth flow) — see
[`praxy_flutter`](https://github.com/<your-fork-or-org>/praxy/tree/main/sdk/flutter/praxy_flutter)
for those.

## Install

Not published to pub.dev yet:

```yaml
dependencies:
  praxy_core:
    git:
      url: https://github.com/<your-fork-or-org>/praxy
      path: sdk/flutter/praxy_core
```

## Usage

```dart
import 'package:praxy_core/praxy_core.dart';

final px = Praxy(endpoint: 'http://localhost:5090', projectId: 'your-project-id');

// Account
final session = await px.account.create(email: 'a@b.com', password: 'correct-horse-battery');
final user = await px.account.get();
await px.account.updateName(name: 'New Name');
final sessions = await px.account.listSessions(); // sessions.sessions[i].current marks this client's own
final jwt = await px.account.createJwt(); // hand this to another process to act as this user

// Teams
final team = await px.teams.create(name: 'Engineering');
await px.teams.createMembership(team.id, email: 'teammate@example.com', url: 'https://app.example/accept');

// Functions — the data-plane invoke surface only; deployment management is a console concern.
final execution = await px.functions.createExecution('function-id', path: '/hello');
print(execution.responseBody);

// Storage — files live in buckets, and a bucket denies everyone until an operator grants
// a role in the console (a 401 on a fresh bucket is expected, not a bug). The whole file
// goes in one request; the server streams it into storage inside a single transaction, so a
// failed or over-quota upload leaves nothing behind rather than a partial file.
// On a bucket with file security on, `permissions` attaches the file's own grants (the
// uploader's own user id, here) — additive on top of the bucket matrix, and never granted
// automatically: pass them or nobody outside that matrix can reach the file.
final stored = await px.storage.createFile(
  'bucket-id',
  name: 'avatar.png',
  bytes: await File('avatar.png').readAsBytes(),
  mimeType: 'image/png',
  permissions: ['read("user:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4")'],
);
print('${stored.sizeBytes} bytes, sha256 ${stored.checksum}'); // checksum computed as it streamed
final page2 = await px.storage.listFiles('bucket-id', limit: 25);
final bytes = await px.storage.getFileDownload('bucket-id', stored.id); // buffered whole
// Image transforms: a generated, cached derivative instead of the original bytes. Dimensions
// snap up server-side to a fixed ladder (64/128/256/512/1024/2048) — a size above 2048 is a
// clean error, not a silent clamp — and the same permission check as the plain download applies.
final thumbnail = await px.storage.getFileDownload(
  'bucket-id',
  stored.id,
  transform: const FileTransform(width: 200, format: 'webp'),
);
await px.storage.deleteFile('bucket-id', stored.id);

// Typed rows — write a small RowCodec<T> once per table, no build step required.
final class Todo {
  // id is null for a not-yet-created Todo — decode always fills it in from RowMeta,
  // since it never runs before a row exists on the server.
  const Todo({this.id, required this.title, required this.done});
  final String? id;
  final String title;
  final bool done;
}

final todoCodec = RowCodec<Todo>(
  encode: (todo) => {'title': todo.title, 'done': todo.done}, // id is metadata, never sent back
  decode: (data, meta) =>
      Todo(id: meta.id, title: data['title'] as String, done: data['done'] as bool),
);
final todos = TableRef<Todo>('db1', 'todos', codec: todoCodec);

final page = await px.tables.list(todos, queries: [Query.equal(const Col<bool>('done'), false)]);
final created = await px.tables.create(todos, data: const Todo(title: 'Ship it', done: false));
final row = await px.tables.get(todos, created.id!);
await px.tables.update(todos, created.id!, data: {'done': true}); // PATCH: only 'done' is sent
await px.tables.delete(todos, created.id!);
```

Errors are a sealed hierarchy rooted at `PraxyException` — `PraxyAuthException` (401/403),
`PraxyNotFoundException` (404), `PraxyConflictException` (409), `PraxyRateLimitException` (429, carries
`retryAfter` parsed from the `Retry-After` header), `PraxyValidationException` (carries per-field
`fields`), and `PraxyNetworkException`/`PraxyDecodeException` for transport-level failures — so
`catch (PraxyRateLimitException e)` etc. works without string-matching a message.

## Server usage (API keys)

This package is the **Dart server SDK** as well as the core of the Flutter one. Pass an `apiKey`
instead of signing a user in, and every call authenticates as the project rather than as an end
user:

```dart
final px = Praxy(
  endpoint: 'https://api.example.com',
  projectId: 'your-project-id',
  apiKey: '<keyId>.<secret>',   // created in the console, under the project's API keys
);

final rows = await px.tables.list(todos);   // no sign-in, no session
await px.users.create(email: 'new@user.com', password: 'hunter2');
```

`px.users` is the server-side app-user administration surface (`/v1/users`): create, inspect,
update and delete a project's users, and revoke their sessions. It is **generated** from the API's
OpenAPI document by
[`praxy_sdk_gen`](https://github.com/<your-fork-or-org>/praxy/tree/main/sdk/flutter/praxy_sdk_gen) —
the only generated service here; everything else is hand-written because it carries design a
generator would flatten.

It is present on every client rather than only server ones, the same way `teams`' owner-only methods
always are: authorization is the server's answer to give, and a second copy here would be one more
thing to get wrong. Calling it with a session gets a `401`, surfacing as `PraxyAuthException`.

A client constructed with an `apiKey` **never reads or writes the session store at all** — not
"prefers the key over a session". The distinction matters: falling back would make a background
job's identity depend on whatever session happened to be persisted, so a scheduled task could
silently start acting as the last user who signed in. `px.isServerClient` tells the two modes apart.

> **Never ship an `apiKey` inside an app your users install.** A key in a Flutter binary or a
> browser bundle is extractable, and unlike a session it is not scoped to one end user.
> [`praxy_flutter`](https://github.com/<your-fork-or-org>/praxy/tree/main/sdk/flutter/praxy_flutter)
> deliberately offers no way to pass one — a client app should authenticate as its user.

One package serves both audiences, matching `@praxy/core`'s dual-mode client, rather than splitting
into separate client and server packages the way Appwrite's `appwrite`/`node-appwrite` do. That
split stays available later if the warning above proves not to be enough.

## What's not here

- **Realtime.** `praxy_core` has no WebSocket dependency; `Praxy.mintRealtimeTicket()` exists so a
  higher layer (like `praxy_flutter`'s `PraxyRealtime`) can authenticate a socket, but this package
  never opens one itself.
- **OAuth.** Google sign-in needs a browser-based redirect flow that's inherently platform-specific;
  `praxy_flutter`'s `PraxyOAuth` builds on this package's session/account primitives to provide it.
- **Bucket management.** Creating buckets and editing their permission matrix is a console/operator
  concern, the same line this package draws against schema management. Renaming a stored file, and
  changing an existing file's grants (`PATCH /v1/storage/buckets/{id}/files/{id}/permissions`), are
  API routes this package deliberately doesn't wrap yet. HTTP `Range` requests the server does now
  honour — they're a transport concern, so use them through your own HTTP client if you need partial
  reads.
- **Codegen.** `TableRef`/`RowCodec` work with hand-written codecs; see
  [`praxy_codegen`](https://github.com/<your-fork-or-org>/praxy/tree/main/sdk/flutter/praxy_codegen)
  if you'd rather generate typed `Col<T>` column constants from your live schema.

## Development

From the workspace root (`sdk/flutter/`): `dart pub get`, then `dart test praxy_core`.
