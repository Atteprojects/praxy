import 'dart:async';

import 'package:praxy_core/praxy_core.dart';

import 'realtime/realtime.dart';

/// Forwards the 5 REST row methods to `praxy_core`'s [TablesService] and adds
/// `liveList<T>` — the 6th, realtime-shaped one — which needs [PraxyRealtime] to
/// exist at all, so it can't live in the WebSocket-free core package.
final class PraxyFlutterTables {
  const PraxyFlutterTables(this._tables, this._realtime);

  final TablesService _tables;
  final PraxyRealtime _realtime;

  Future<RowList<T>> list<T>(TableRef<T> table, {List<Query> queries = const [], bool total = true}) =>
      _tables.list(table, queries: queries, total: total);

  Future<T> get<T>(TableRef<T> table, String rowId) => _tables.get(table, rowId);

  Future<T> create<T>(TableRef<T> table, {String? rowId, required T data, List<String> permissions = const []}) =>
      _tables.create(table, rowId: rowId, data: data, permissions: permissions);

  Future<T> update<T>(TableRef<T> table, String rowId, {Map<String, dynamic>? data, List<String>? permissions}) =>
      _tables.update(table, rowId, data: data, permissions: permissions);

  Future<void> delete<T>(TableRef<T> table, String rowId) => _tables.delete(table, rowId);

  /// A REST snapshot followed by realtime patches — every subscriber gets an
  /// updated [RowList] each time a matching row is created, updated, or deleted,
  /// without re-running the whole query.
  ///
  /// Two honest simplifications, documented rather than hidden: row order is
  /// snapshot order with newly-created rows appended, never re-sorted against
  /// `orderAsc`/`orderDesc` as events arrive; and [RowList.total] becomes `null`
  /// after the first patch, since keeping an exact count live would mean
  /// re-running the count query on every event. Call [list] again if either
  /// matters more than staying live for a given screen.
  Stream<RowList<T>> liveList<T>(TableRef<T> table, {List<Query> queries = const []}) {
    late final StreamController<RowList<T>> controller;
    StreamSubscription<RowEvent<T>>? subscription;

    Future<void> start() async {
      final order = <String>[];
      final byId = <String, T>{};
      int? total;

      try {
        final snapshot = await _tables.listWithIds(table, queries: queries);
        total = snapshot.total;
        for (final row in snapshot.rows) {
          order.add(row.id);
          byId[row.id] = row.value;
        }
      } catch (error, stackTrace) {
        if (!controller.isClosed) controller.addError(error, stackTrace);
        return;
      }
      if (controller.isClosed) return;
      controller.add(RowList<T>(total: total, rows: [for (final id in order) byId[id] as T]));

      subscription = _realtime.rows<T>(table, queries: queries).listen(
        (event) {
          switch (event) {
            case RowCreated<T>():
              if (!byId.containsKey(event.rowId)) order.add(event.rowId);
              byId[event.rowId] = event.row;
            case RowUpdated<T>():
              if (!byId.containsKey(event.rowId)) order.add(event.rowId);
              byId[event.rowId] = event.row;
            case RowDeleted<T>():
              order.remove(event.rowId);
              byId.remove(event.rowId);
          }
          if (!controller.isClosed) {
            controller.add(RowList<T>(total: null, rows: [for (final id in order) byId[id] as T]));
          }
        },
        onError: (Object error, StackTrace stackTrace) {
          if (!controller.isClosed) controller.addError(error, stackTrace);
        },
      );
    }

    controller = StreamController<RowList<T>>.broadcast(
      onListen: () => unawaited(start()),
      onCancel: () => unawaited(subscription?.cancel()),
    );
    return controller.stream;
  }
}
