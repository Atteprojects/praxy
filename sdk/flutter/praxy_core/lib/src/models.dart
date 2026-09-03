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

/// A `listSessions` page — every session belonging to the caller, [AppSession.current]
/// marking which one is this client's own.
final class SessionList {
  const SessionList({required this.total, required this.sessions});

  final int total;
  final List<AppSession> sessions;

  factory SessionList.fromJson(Map<String, dynamic> json) => SessionList(
    total: json['total'] as int,
    sessions: [
      for (final s in (json['sessions'] as List).cast<Map<String, dynamic>>()) AppSession.fromJson(s),
    ],
  );
}

/// What the caller resolves to under the permission engine — the debug view behind
/// `GET /v1/account/roles`. [scopes] is only present for an API-key caller.
final class ResolvedRoles {
  const ResolvedRoles({required this.roles, required this.principal, this.scopes});

  final List<String> roles;
  final String principal;
  final List<String>? scopes;

  factory ResolvedRoles.fromJson(Map<String, dynamic> json) => ResolvedRoles(
    roles: (json['roles'] as List).cast<String>(),
    principal: json['principal'] as String,
    scopes: (json['scopes'] as List?)?.cast<String>(),
  );
}

/// A team of users (`/v1/teams`) — [memberCount] only counts confirmed memberships.
final class Team {
  const Team({required this.id, required this.name, required this.memberCount, required this.createdAt});

  final String id;
  final String name;
  final int memberCount;
  final DateTime createdAt;

  factory Team.fromJson(Map<String, dynamic> json) => Team(
    id: json['id'] as String,
    name: json['name'] as String,
    memberCount: json['memberCount'] as int,
    createdAt: DateTime.parse(json['createdAt'] as String),
  );
}

final class TeamList {
  const TeamList({required this.total, required this.teams});

  final int total;
  final List<Team> teams;

  factory TeamList.fromJson(Map<String, dynamic> json) => TeamList(
    total: json['total'] as int,
    teams: [for (final t in (json['teams'] as List).cast<Map<String, dynamic>>()) Team.fromJson(t)],
  );
}

/// One user's membership in a [Team]. [invitedAt]/[joinedAt] are both `null` until the
/// server actually sets them — a key-added member ([confirmed] `true` immediately) skips
/// straight past the invited state, so a caller shouldn't assume [invitedAt] precedes it.
final class Membership {
  const Membership({
    required this.id,
    required this.teamId,
    required this.userId,
    required this.userEmail,
    required this.userName,
    required this.roles,
    required this.confirmed,
    this.invitedAt,
    this.joinedAt,
  });

  final String id;
  final String teamId;
  final String userId;
  final String userEmail;
  final String userName;
  final List<String> roles;
  final bool confirmed;
  final DateTime? invitedAt;
  final DateTime? joinedAt;

  factory Membership.fromJson(Map<String, dynamic> json) => Membership(
    id: json['id'] as String,
    teamId: json['teamId'] as String,
    userId: json['userId'] as String,
    userEmail: json['userEmail'] as String,
    userName: json['userName'] as String,
    roles: (json['roles'] as List).cast<String>(),
    confirmed: json['confirmed'] as bool,
    invitedAt: json['invitedAt'] == null ? null : DateTime.parse(json['invitedAt'] as String),
    joinedAt: json['joinedAt'] == null ? null : DateTime.parse(json['joinedAt'] as String),
  );
}

final class MembershipList {
  const MembershipList({required this.total, required this.memberships});

  final int total;
  final List<Membership> memberships;

  factory MembershipList.fromJson(Map<String, dynamic> json) => MembershipList(
    total: json['total'] as int,
    memberships: [
      for (final m in (json['memberships'] as List).cast<Map<String, dynamic>>()) Membership.fromJson(m),
    ],
  );
}

/// Accepting an invitation both joins the team and signs the user in — the same
/// [CreatedSession] shape `account.create`/`createEmailSession` return.
final class AcceptedMembership {
  const AcceptedMembership({required this.membership, required this.session});

  final Membership membership;
  final CreatedSession session;

  factory AcceptedMembership.fromJson(Map<String, dynamic> json) => AcceptedMembership(
    membership: Membership.fromJson(json['membership'] as Map<String, dynamic>),
    session: CreatedSession.fromJson(json['session'] as Map<String, dynamic>),
  );
}

/// One invocation of a function (`/v1/functions/{id}/executions`). [statusCode]/
/// [responseBody] are `null` until the run completes; [triggeredBy] is `null` for a
/// guest-triggered call, which makes it unrecoverable via `getExecution` by design —
/// see FunctionEndpoints.cs's `GetDataPlaneExecution` remarks.
final class FunctionExecution {
  const FunctionExecution({
    required this.id,
    required this.trigger,
    required this.isAsync,
    required this.status,
    required this.method,
    required this.path,
    this.statusCode,
    this.responseBody,
    required this.logs,
    this.errors,
    this.durationMs,
    required this.coldStart,
    this.triggeredBy,
    required this.createdAt,
    this.completedAt,
  });

  final String id;
  final String trigger;
  final bool isAsync;
  final String status;
  final String method;
  final String path;
  final int? statusCode;
  final String? responseBody;
  final String logs;
  final String? errors;
  final int? durationMs;
  final bool coldStart;
  final String? triggeredBy;
  final DateTime createdAt;
  final DateTime? completedAt;

  factory FunctionExecution.fromJson(Map<String, dynamic> json) => FunctionExecution(
    id: json['id'] as String,
    trigger: json['trigger'] as String,
    isAsync: json['async'] as bool,
    status: json['status'] as String,
    method: json['method'] as String,
    path: json['path'] as String,
    statusCode: json['statusCode'] as int?,
    responseBody: json['responseBody'] as String?,
    logs: json['logs'] as String,
    errors: json['errors'] as String?,
    durationMs: json['durationMs'] as int?,
    coldStart: json['coldStart'] as bool,
    triggeredBy: json['triggeredBy'] as String?,
    createdAt: DateTime.parse(json['createdAt'] as String),
    completedAt: json['completedAt'] == null ? null : DateTime.parse(json['completedAt'] as String),
  );
}

/// A `tables.list<T>` page. [total] is `null` when the caller opted out of the
/// count query (`total: false`) for a cheaper list call.
final class RowList<T> {
  const RowList({required this.total, required this.rows});

  final int? total;
  final List<T> rows;
}

/// One stored file's metadata (`FileResponse` server-side). The bytes themselves
/// come back from `StorageService.getFileDownload`, never inline here.
final class StoredFile {
  const StoredFile({
    required this.id,
    required this.bucketId,
    required this.name,
    required this.mimeType,
    required this.sizeBytes,
    required this.chunkSizeBytes,
    required this.chunkCount,
    required this.checksum,
    required this.createdAt,
    required this.updatedAt,
  });

  final String id;
  final String bucketId;
  final String name;
  final String mimeType;
  final int sizeBytes;

  /// What this file was actually written with — not what the server is currently
  /// configured to use, which can change without touching stored files.
  final int chunkSizeBytes;
  final int chunkCount;

  /// Lowercase hex SHA-256, computed server-side while the upload streamed.
  final String checksum;
  final DateTime createdAt;
  final DateTime updatedAt;

  factory StoredFile.fromJson(Map<String, dynamic> json) => StoredFile(
    id: json['id'] as String,
    bucketId: json['bucketId'] as String,
    name: json['name'] as String,
    mimeType: json['mimeType'] as String,
    sizeBytes: (json['sizeBytes'] as num).toInt(),
    chunkSizeBytes: (json['chunkSizeBytes'] as num).toInt(),
    chunkCount: (json['chunkCount'] as num).toInt(),
    checksum: json['checksum'] as String,
    createdAt: DateTime.parse(json['createdAt'] as String),
    updatedAt: DateTime.parse(json['updatedAt'] as String),
  );
}

final class StoredFileList {
  const StoredFileList({required this.total, required this.files});

  final int total;
  final List<StoredFile> files;

  factory StoredFileList.fromJson(Map<String, dynamic> json) => StoredFileList(
    total: (json['total'] as num).toInt(),
    files: ((json['files'] as List?) ?? const [])
        .map((f) => StoredFile.fromJson((f as Map).cast<String, dynamic>()))
        .toList(growable: false),
  );
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
