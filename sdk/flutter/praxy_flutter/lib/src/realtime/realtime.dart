import 'package:praxy_core/praxy_core.dart';

import '../account_event.dart';
import '../connection_state.dart';
import 'realtime_socket.dart';

/// Query methods that filter or shape a single row's visibility — the subset worth
/// re-applying to a realtime event. Pagination/ordering methods (`limit`, `offset`,
/// `cursorAfter`, `cursorBefore`, `orderAsc`, `orderDesc`) describe how a *list* is
/// shaped, not whether one row belongs in it, so they're dropped rather than sent
/// to the single-row existence check in [PraxyRealtime.rows].
const _filterMethods = {
  'equal', 'notEqual', 'lessThan', 'lessThanEqual', 'greaterThan', 'greaterThanEqual',
  'between', 'isNull', 'isNotNull', 'startsWith', 'endsWith', 'contains', 'search',
  'and', 'or', 'select',
};

const Col<String> _idCol = Col<String>(r'$id');

/// Realtime access: `rows<T>`, `account`, `connection`, `close` — the 4-method
/// surface research/flutter-sdk.md specifies. One [RealtimeSocket] backs every
/// stream this returns, lazily created on first use.
///
/// The server's row-change event carries only `{databaseId, tableId, rowId, roles}`
/// — never the row's actual column data (`RowsService.BuildEvent`, Phase 4) — so
/// `create`/`update` events here re-fetch the row over REST before emitting. That
/// fetch also re-applies [queries] server-side (`Query.equal($id, rowId)` ANDed in),
/// which is how this stays consistent with a `list()` snapshot taken with the same
/// queries: the phase-4 report is explicit that a subscribe's `queries` field is
/// accepted but never applied as a server-side realtime filter (matching Appwrite's
/// own behaviour), so doing nothing here would leak rows the original query
/// would have excluded.
final class PraxyRealtime {
  PraxyRealtime(this._client);

  final Praxy _client;
  RealtimeSocket? _socket;

  RealtimeSocket _ensureSocket() => _socket ??= RealtimeSocket(
    endpoint: _client.endpoint,
    projectId: _client.projectId,
    ticketProvider: () async {
      final session = await _client.sessionStore.read();
      if (session == null) return null;
      try {
        return (await _client.mintRealtimeTicket()).value;
      } on PraxyException {
        return null; // fall back to an unauthenticated (guest-role) connection
      }
    },
  );

  /// A stream of [RowEvent]s for [table] — the whole table, or just [rowId] if
  /// given. [queries] narrows which creates/updates are emitted, matching what a
  /// `tables.list(table, queries: queries)` snapshot would return.
  Stream<RowEvent<T>> rows<T>(TableRef<T> table, {String? rowId, List<Query> queries = const []}) async* {
    final channel = rowId == null
        ? 'databases.${table.databaseId}.tables.${table.tableId}.rows'
        : 'databases.${table.databaseId}.tables.${table.tableId}.rows.$rowId';
    final filterQueries = [for (final q in queries) if (_filterMethods.contains(q.method)) q];

    await for (final data in _ensureSocket().subscribe([channel])) {
      final events = ((data['events'] as List?) ?? const []).cast<String>();
      final payload = (data['payload'] as Map?)?.cast<String, dynamic>() ?? const {};
      final eventRowId = payload['rowId'] as String?;
      if (eventRowId == null) continue;
      final timestamp = DateTime.tryParse(data['timestamp'] as String? ?? '') ?? DateTime.now().toUtc();
      final action = _actionOf(events);

      if (action == 'delete') {
        yield RowDeleted<T>(tableId: table.tableId, rowId: eventRowId, events: events, timestamp: timestamp);
        continue;
      }

      final row = await _fetchIfMatching(table, eventRowId, filterQueries);
      if (row == null) continue; // filtered out, no longer readable, or already gone

      yield action == 'create'
          ? RowCreated<T>(row: row, tableId: table.tableId, rowId: eventRowId, events: events, timestamp: timestamp)
          : RowUpdated<T>(row: row, tableId: table.tableId, rowId: eventRowId, events: events, timestamp: timestamp);
    }
  }

  /// The caller's own `account` channel — session/profile lifecycle events.
  Stream<AccountEvent> get account => _ensureSocket().subscribe(['account']).map(
    (data) => AccountEvent(
      events: ((data['events'] as List?) ?? const []).cast<String>(),
      channels: ((data['channels'] as List?) ?? const []).cast<String>(),
      payload: (data['payload'] as Map?)?.cast<String, dynamic>() ?? const {},
      timestamp: DateTime.tryParse(data['timestamp'] as String? ?? '') ?? DateTime.now().toUtc(),
    ),
  );

  /// Transport health — reconnects and their backoff never surface as row events.
  Stream<PraxyConnectionState> get connection => _ensureSocket().connectionStates;

  /// Tears down the shared socket and every active subscription. A later `rows`/
  /// `account` call lazily opens a fresh one.
  void close() {
    _socket?.dispose();
    _socket = null;
  }

  Future<T?> _fetchIfMatching<T>(TableRef<T> table, String rowId, List<Query> filterQueries) async {
    try {
      final page = await _client.tables.list(
        table,
        queries: [...filterQueries, Query.equal(_idCol, rowId), Query.limit(1)],
        total: false,
      );
      return page.rows.isEmpty ? null : page.rows.first;
    } on PraxyApiException {
      return null; // e.g. the table or a permission check went away mid-flight
    }
  }

  static String _actionOf(List<String> events) {
    for (final e in events) {
      if (e.endsWith('.create')) return 'create';
      if (e.endsWith('.delete')) return 'delete';
      if (e.endsWith('.update')) return 'update';
    }
    return 'update';
  }
}
