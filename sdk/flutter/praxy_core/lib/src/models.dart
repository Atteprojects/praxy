/// The app-user wire shape (`AppUserResponse` server-side). Ids are 32-hex-char
/// uuids — the same form permission roles use (`user:<id>`).
final class AppUser {
  const AppUser({
    required this.id,
    required this.email,
    required this.name,
    required this.emailVerified,
    required this.status,
    required this.labels,
    required this.prefs,
    required this.createdAt,
    required this.updatedAt,
  });

  final String id;
  final String email;
  final String name;
  final bool emailVerified;
  final bool status;
  final List<String> labels;
  final Map<String, dynamic> prefs;
  final DateTime createdAt;
  final DateTime updatedAt;

  factory AppUser.fromJson(Map<String, dynamic> json) => AppUser(
    id: json['id'] as String,
    email: json['email'] as String,
    name: json['name'] as String,
    emailVerified: json['emailVerified'] as bool,
    status: json['status'] as bool,
    labels: ((json['labels'] as List?) ?? const []).cast<String>(),
    prefs: (json['prefs'] as Map?)?.cast<String, dynamic>() ?? const {},
    createdAt: DateTime.parse(json['createdAt'] as String),
    updatedAt: DateTime.parse(json['updatedAt'] as String),
  );
}

final class AppSession {
  const AppSession({
    required this.id,
    required this.userId,
    required this.provider,
    this.ip,
    this.userAgent,
    required this.current,
    required this.expiresAt,
    required this.createdAt,
  });

  final String id;
  final String userId;
  final String provider;
  final String? ip;
  final String? userAgent;
  final bool current;
  final DateTime expiresAt;
  final DateTime createdAt;

  factory AppSession.fromJson(Map<String, dynamic> json) => AppSession(
    id: json['id'] as String,
    userId: json['userId'] as String,
    provider: json['provider'] as String,
    ip: json['ip'] as String?,
    userAgent: json['userAgent'] as String?,
    current: json['current'] as bool,
    expiresAt: DateTime.parse(json['expiresAt'] as String),
    createdAt: DateTime.parse(json['createdAt'] as String),
  );
}

/// What signup/login/token-exchange return: the user, the session record, and the
/// opaque secret — carried exactly once. Persisting it is the [SessionStore]'s job.
final class CreatedSession {
  const CreatedSession({required this.user, required this.session, required this.token});

  final AppUser user;
  final AppSession session;
  final String token;

  factory CreatedSession.fromJson(Map<String, dynamic> json) => CreatedSession(
    user: AppUser.fromJson(json['user'] as Map<String, dynamic>),
    session: AppSession.fromJson(json['session'] as Map<String, dynamic>),
    token: json['token'] as String,
  );
}

/// A `tables.list<T>` page. [total] is `null` when the caller opted out of the
/// count query (`total: false`) for a cheaper list call.
final class RowList<T> {
  const RowList({required this.total, required this.rows});

  final int? total;
  final List<T> rows;
}

/// A single-use, short-lived credential for authenticating the realtime WebSocket —
/// native clients can't attach the session cookie a browser handshake carries
/// automatically, so they mint one of these instead (`POST /v1/realtime/ticket`).
final class RealtimeTicket {
  const RealtimeTicket({required this.value, required this.expiresAt});

  final String value;
  final DateTime expiresAt;

  factory RealtimeTicket.fromJson(Map<String, dynamic> json) => RealtimeTicket(
    value: json['ticket'] as String,
    expiresAt: DateTime.parse(json['expiresAt'] as String),
  );
}

/// A realtime update for a subscribed table or row. Sealed so a `switch` over the
/// three variants is exhaustive instead of string-matching an `action` field.
sealed class RowEvent<T> {
  const RowEvent({
    required this.tableId,
    required this.rowId,
    required this.events,
    required this.timestamp,
  });

  final String tableId;
  final String rowId;

  /// The wildcard-expanded event names this update matched, e.g.
  /// `databases.*.tables.*.rows.*.create`.
  final List<String> events;
  final DateTime timestamp;
}

final class RowCreated<T> extends RowEvent<T> {
  const RowCreated({
    required this.row,
    required super.tableId,
    required super.rowId,
    required super.events,
    required super.timestamp,
  });

  final T row;
}

final class RowUpdated<T> extends RowEvent<T> {
  const RowUpdated({
    required this.row,
    required super.tableId,
    required super.rowId,
    required super.events,
    required super.timestamp,
  });

  final T row;
}

/// Carries no row payload — the server's delete event has no row left to send one.
final class RowDeleted<T> extends RowEvent<T> {
  const RowDeleted({
    required super.tableId,
    required super.rowId,
    required super.events,
    required super.timestamp,
  });
}
