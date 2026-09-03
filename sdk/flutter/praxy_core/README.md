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
final stored = await px.storage.createFile(
  'bucket-id',
  name: 'avatar.png',
  bytes: await File('avatar.png').readAsBytes(),
  mimeType: 'image/png',
);
print('${stored.sizeBytes} bytes, sha256 ${stored.checksum}'); // checksum computed as it streamed
final page2 = await px.storage.listFiles('bucket-id', limit: 25);
final bytes = await px.storage.getFileDownload('bucket-id', stored.id); // buffered whole
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

## What's not here

- **Realtime.** `praxy_core` has no WebSocket dependency; `Praxy.mintRealtimeTicket()` exists so a
  higher layer (like `praxy_flutter`'s `PraxyRealtime`) can authenticate a socket, but this package
  never opens one itself.
- **OAuth.** Google sign-in needs a browser-based redirect flow that's inherently platform-specific;
  `praxy_flutter`'s `PraxyOAuth` builds on this package's session/account primitives to provide it.
- **Bucket management.** Creating buckets and editing their permission matrix is a console/operator
  concern, the same line this package draws against schema management. Renaming a stored file is an
  API route this package deliberately doesn't wrap yet; HTTP `Range` requests and image transforms
  don't exist server-side (Storage Phase 2/3).
- **Codegen.** `TableRef`/`RowCodec` work with hand-written codecs; see
  [`praxy_codegen`](https://github.com/<your-fork-or-org>/praxy/tree/main/sdk/flutter/praxy_codegen)
  if you'd rather generate typed `Col<T>` column constants from your live schema.

## Development

From the workspace root (`sdk/flutter/`): `dart pub get`, then `dart test praxy_core`.
