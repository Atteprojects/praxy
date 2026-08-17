# Praxy Flutter SDK

A native Dart/Flutter client for [Praxy](../../README.md), a self-hosted backend-as-a-service. Typed
rows, real-time subscriptions, and Google sign-in against your own Praxy instance — no generated
code required to get started, with an optional codegen step once your schema settles.

This is a [pub workspace](https://dart.dev/tools/pub/workspaces) (no melos): `dart pub get` at this
directory resolves `praxy_core`, `praxy_flutter`, `praxy_codegen`, and `example` together from one
lockfile.

## Packages

| Package | What it is |
|---|---|
| [`praxy_core`](praxy_core/) | Pure-Dart client — `package:http` only, no Flutter dependency. Account/session methods, the typed table CRUD surface, query/permission builders. Works on the Dart VM as well as Flutter (server-side scripts, CLI tools). |
| [`praxy_flutter`](praxy_flutter/) | The Flutter layer most apps actually import: secure-storage sessions, the realtime WebSocket client (`Stream`-based, including `liveList`), and Google sign-in via `flutter_web_auth_2`. Re-exports everything from `praxy_core`. |
| [`praxy_codegen`](praxy_codegen/) | An on-demand CLI (not a `build_runner` builder) that emits typed `Col<T>` column constants for one table into a file you commit. Optional — every method works against `TableRef`/`Col` you write by hand. |
| [`example`](example/) | A runnable Flutter app exercising the full surface: sign-up, Google OAuth, row CRUD, and a live realtime subscription against a local Praxy instance. |

## Quick start

Point at a running Praxy instance (`cd deploy && ./up.sh`, or `dotnet run --project src/Praxy.Api`
for local dev — see the [root README](../../README.md)), then from a Flutter app:

Not published to pub.dev yet — depend on it via git or a local path:

```yaml
# pubspec.yaml
dependencies:
  praxy_flutter:
    git:
      url: https://github.com/<your-fork-or-org>/praxy
      path: sdk/flutter/praxy_flutter
```

```dart
import 'package:praxy_flutter/praxy_flutter.dart';

final px = PraxyFlutter(endpoint: 'http://localhost:5090', projectId: 'your-project-id');

// Auth
final session = await px.account.create(email: 'a@b.com', password: 'correct-horse-battery');

// Typed rows — no codegen needed, TableRef<T> works with any RowCodec<T> you write (see below).
final page = await px.tables.list(todos);
final created = await px.tables.create(todos, data: const Todo(title: 'Ship it', done: false));

// Realtime — a live Stream, reconnects with backoff automatically.
px.realtime.rows(todos).listen((event) {
  switch (event) {
    case RowCreated(:final row): print('created: $row');
    case RowUpdated(:final row): print('updated: $row');
    case RowDeleted(:final rowId): print('deleted: $rowId');
  }
});

// Google sign-in (needs a project with Google OAuth configured in the console).
final oauthSession = await px.oauth.signInWithGoogle(callbackUrlScheme: 'com.yourapp');
```

`praxy_core` alone (no Flutter dependency) covers everything except realtime and OAuth, for use in
plain Dart tools and scripts:

```dart
import 'package:praxy_core/praxy_core.dart';

final px = Praxy(endpoint: 'http://localhost:5090', projectId: 'your-project-id');
final user = await px.account.get();
```

## Typed rows without codegen

`TableRef<T>` + `RowCodec<T>` is the whole mechanism — write a small encode/decode pair once per
table (`RowCodec.decode` also receives the row's system fields — id, timestamps, permissions — as a
`RowMeta`, in case your type wants to carry them):

```dart
final class Todo {
  const Todo({this.id, required this.title, required this.done}); // id: null until created
  final String? id;
  final String title;
  final bool done;
}

final todoCodec = RowCodec<Todo>(
  encode: (todo) => {'title': todo.title, 'done': todo.done},
  decode: (data, meta) =>
      Todo(id: meta.id, title: data['title'] as String, done: data['done'] as bool),
);

final todos = TableRef<Todo>('db1', 'todos', codec: todoCodec);
```

`praxy_codegen` exists for the common case of wanting typed `Col<T>` constants (`Db.todos.title`,
usable in `Query` builders) generated straight from your live schema instead of hand-typed:

```bash
dart run praxy_codegen \
  --endpoint http://localhost:5090 --project <id> --api-key <key> \
  --database <key> --table <key> --output lib/db/todos_columns.dart
```

Re-run it whenever the table's columns change; the output is a plain committed `.dart` file, not a
build-time generator, so there's no `build_runner` step in your app's build.

## Realtime & the example app

The realtime `Stream`s (`px.realtime.rows(table)`, `px.tables.liveList(table)` — a REST snapshot
plus realtime patches in one `Stream<RowList<T>>`, `px.realtime.connection`) reconnect with
exponential backoff + jitter automatically — no manual reconnect handling needed. `px.realtime.rows(table,
rowId: id)` scopes to one row's channel when you only care about a single record.

See [`example/`](example/) for a full runnable app: sign-up, Google OAuth, row CRUD, and watching a
realtime update arrive live — the exact flow validated as this SDK's owner test in
[docs/handoff/phase-5-report.md](../../docs/handoff/phase-5-report.md).

## Development

```bash
dart pub get                                       # resolves the whole workspace
dart test praxy_core praxy_codegen                  # pure-Dart package tests
flutter test praxy_flutter example                  # Flutter package + example tests
dart analyze .                                      # whole workspace
```

Running the example app against a local instance needs real ids (create the database/table via the
console first, not just any string):

```bash
flutter run --dart-define=PRAXY_ENDPOINT=http://localhost:5090 \
  --dart-define=PRAXY_PROJECT_ID=<id> --dart-define=PRAXY_DATABASE_ID=<id> --dart-define=PRAXY_TABLE_ID=<id>
```

Full spec and design rationale: [docs/research/flutter-sdk.md](../../docs/research/flutter-sdk.md).
