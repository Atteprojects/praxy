# praxy_flutter

Flutter layer of the [Praxy](https://github.com/<your-fork-or-org>/praxy) SDK. Re-exports everything
from [`praxy_core`](https://github.com/<your-fork-or-org>/praxy/tree/main/sdk/flutter/praxy_core)
plus what needs Flutter: secure-storage sessions, the realtime WebSocket client (`Stream<RowEvent<T>>`,
`Stream<RowList<T>>` for `liveList`, connection-state), and Google sign-in via
[`flutter_web_auth_2`](https://pub.dev/packages/flutter_web_auth_2). Most apps only need to import
this package.

## Install

Not published to pub.dev yet:

```yaml
dependencies:
  praxy_flutter:
    git:
      url: https://github.com/<your-fork-or-org>/praxy
      path: sdk/flutter/praxy_flutter
```

## Usage

```dart
import 'package:praxy_flutter/praxy_flutter.dart';

final px = PraxyFlutter(endpoint: 'http://localhost:5090', projectId: 'your-project-id');

// Sessions persist in secure storage (flutter_secure_storage) automatically —
// no manual token handling between app launches.
final session = await px.account.create(email: 'a@b.com', password: 'correct-horse-battery');

// Teams, function invocation and file storage — plain passthroughs to praxy_core,
// same as px.account.
final team = await px.teams.create(name: 'Engineering');
final execution = await px.functions.createExecution('function-id', path: '/hello');
final stored = await px.storage.createFile(
  'bucket-id', name: 'avatar.png', bytes: imageBytes, mimeType: 'image/png');

// The same 5-method row surface as praxy_core (px.tables.list/get/create/update/delete),
// plus liveList<T>: a REST snapshot followed by realtime patches in one Stream.
px.tables.liveList(todos).listen((page) => print('${page.rows.length} rows live'));

// A raw event stream when you want create/update/delete distinguished explicitly.
px.realtime.rows(todos).listen((event) {
  switch (event) {
    case RowCreated(:final row): print('created: $row');
    case RowUpdated(:final row): print('updated: $row');
    case RowDeleted(:final rowId): print('deleted: $rowId');
  }
});

// Connection state, if you want to show a live "reconnecting…" banner.
px.realtime.connection.listen((state) => print(state));
```

### Google sign-in

Needs a project with Google OAuth configured in the console, and the platform-specific setup
`flutter_web_auth_2` itself requires — an Android intent-filter for the callback scheme
(`AndroidManifest.xml`, matching `callbackUrlScheme` below); iOS needs nothing extra. See
[docs/research/flutter-sdk.md](https://github.com/<your-fork-or-org>/praxy/blob/main/docs/research/flutter-sdk.md)
for the verified setup.

```dart
final session = await px.oauth.signInWithGoogle(callbackUrlScheme: 'com.yourapp');
```

### Tearing down

```dart
px.close(); // closes the realtime socket and the HTTP transport — call on sign-out or app teardown
```

## What's here vs. praxy_core

| | `praxy_core` | `praxy_flutter` |
|---|---|---|
| Account/session methods | ✅ | ✅ (re-exported) |
| Row CRUD (`list`/`get`/`create`/`update`/`delete`) | ✅ | ✅ (re-exported) |
| `liveList<T>` (REST snapshot + realtime patches) | — | ✅ |
| Raw realtime `Stream<RowEvent<T>>` | — | ✅ |
| Session storage | in-memory only | secure storage (`flutter_secure_storage`) |
| Google OAuth | — | ✅ |

Use `praxy_core` directly (no Flutter dependency) for CLI tools or server-side scripts that don't
need realtime or OAuth.

## Development

From the workspace root (`sdk/flutter/`): `dart pub get`, then `flutter test praxy_flutter`.
See [`example/`](../example/) for a full runnable app exercising this package end to end.
