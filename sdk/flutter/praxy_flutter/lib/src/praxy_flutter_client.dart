import 'package:praxy_core/praxy_core.dart';

import 'oauth.dart';
import 'realtime/realtime.dart';
import 'secure_session_store.dart';
import 'tables.dart';

/// The Flutter entry point: one `Praxy` core client plus secure-storage sessions,
/// the realtime WebSocket layer, and Google sign-in — the full surface
/// research/flutter-sdk.md specifies, assembled from `praxy_core` and this package.
///
/// ```dart
/// final px = PraxyFlutter(endpoint: 'https://api.example.com', projectId: 'proj1');
/// final user = await px.account.get();
/// final page = await px.tables.list(Db.todos);
/// px.realtime.rows(Db.todos).listen((event) { ... });
/// ```
final class PraxyFlutter {
  PraxyFlutter({required String endpoint, required this.projectId, SessionStore? sessionStore, Transport? transport})
    : core = Praxy(
        endpoint: endpoint,
        projectId: projectId,
        transport: transport,
        sessionStore: sessionStore ?? SecureSessionStore(projectId: projectId),
      ) {
    realtime = PraxyRealtime(core);
    tables = PraxyFlutterTables(core.tables, realtime);
    oauth = PraxyOAuth(core);
  }

  final String projectId;

  /// The pure-Dart client underneath, if a lower-level call is ever needed.
  final Praxy core;

  late final PraxyRealtime realtime;
  late final PraxyFlutterTables tables;
  late final PraxyOAuth oauth;

  AccountService get account => core.account;
  TeamsService get teams => core.teams;
  FunctionsService get functions => core.functions;
  StorageService get storage => core.storage;
  SessionStore get sessionStore => core.sessionStore;
  Uri get endpoint => core.endpoint;

  /// Tears down the realtime socket and the HTTP transport. Call on sign-out or
  /// app teardown, not between individual requests.
  void close() {
    realtime.close();
    core.close();
  }
}
