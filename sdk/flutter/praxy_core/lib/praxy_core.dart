/// Pure-Dart core of the Praxy SDK: the `Praxy` client, its `Transport`/
/// `SessionStore` platform seams, the typed `Query`/`Permission`/`Role` builders,
/// `RowCodec`/`TableRef` for codegen-free typed rows, and the sealed
/// `PraxyException` hierarchy. `package:http` is its only dependency, so this
/// package works identically on the Dart VM and in Flutter.
///
/// Realtime and OAuth need platform capabilities this package deliberately doesn't
/// depend on (a WebSocket stack, a browser-based auth flow) — see `praxy_flutter`.
library;

export 'src/client.dart' show Praxy;
export 'src/errors.dart';
export 'src/http_transport.dart' show HttpTransport;
export 'src/ids.dart' show Uid;
export 'src/models.dart';
export 'src/permission.dart' show Permission, Role;
export 'src/query.dart' show Col, Query;
export 'src/row_codec.dart' show RowCodec, RowDecoder, RowEncoder, RowMeta, TableRef;
export 'src/services/account_service.dart' show AccountService;
export 'src/services/functions_service.dart' show FunctionsService;
export 'src/services/storage_service.dart' show StorageService;
export 'src/services/tables_service.dart' show TablesService;
export 'src/services/teams_service.dart' show TeamsService;
export 'src/session_store.dart' show MemorySessionStore, Session, SessionStore;
export 'src/transport.dart' show Transport, TransportRequest, TransportResponse;
