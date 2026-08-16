/// Flutter layer of the Praxy SDK. Re-exports everything from `praxy_core` plus
/// what needs Flutter: secure-storage sessions, the realtime WebSocket client, and
/// Google OAuth via `flutter_web_auth_2`. Most apps only need to import this.
library;

export 'package:praxy_core/praxy_core.dart';

export 'src/account_event.dart';
export 'src/connection_state.dart';
export 'src/oauth.dart' show PraxyOAuth;
export 'src/praxy_flutter_client.dart' show PraxyFlutter;
export 'src/realtime/realtime.dart' show PraxyRealtime;
export 'src/secure_session_store.dart' show SecureSessionStore;
export 'src/tables.dart' show PraxyFlutterTables;
