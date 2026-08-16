import '../client.dart';
import '../errors.dart';
import '../json_utils.dart';
import '../models.dart';
import '../query.dart';
import '../row_codec.dart';

/// Row CRUD against a [TableRef]'s table. 5 methods here, not the 7
/// research/flutter-sdk.md lists:
///
/// - `upsert<T>` is dropped: `RowEndpoints.cs`/`RowsService.cs` implement only
///   create/list/get/update(PATCH)/delete — there is no upsert route or service
///   method anywhere server-side. A real API gap, not an SDK oversight; documented
///   in the phase-5 report rather than emulated with a non-atomic create-then-
///   update-on-conflict.
/// - `liveList<T>` lives on `praxy_flutter`'s table service instead — it's a REST
///   snapshot plus a realtime patch stream, and this package has no realtime (no
///   WebSocket dependency at all; `praxy_core` is `package:http` only).
final class TablesService {
  const TablesService(this._client);

  final Praxy _client;

  Future<RowList<T>> list<T>(TableRef<T> table, {List<Query> queries = const [], bool total = true}) async {
    final result = await listWithIds(table, queries: queries, total: total);
    return RowList<T>(total: result.total, rows: [for (final r in result.rows) r.value]);
  }

  /// Like [list], but keeps each row's id alongside its decoded [T] — [T] itself is
  /// opaque to this package, so nothing here can assume it exposes its own id.
  /// Not part of research/flutter-sdk.md's documented 5-method surface; it's the
  /// bookkeeping primitive `praxy_flutter`'s `liveList` needs to patch a specific
  /// row in a locally-held list without re-querying the whole table.
  Future<({int? total, List<({String id, T value})> rows})> listWithIds<T>(
    TableRef<T> table, {
    List<Query> queries = const [],
    bool total = true,
  }) async {
    final json = requireJson(
      await _client.request(
        method: 'GET',
        path: _rowsPath(table),
        query: {
          if (queries.isNotEmpty) 'queries[]': [for (final q in queries) q.encode()],
          if (!total) 'total': const ['false'],
        },
      ),
      _rowsPath(table),
    );
    final rawRows = (json['rows'] as List).cast<Map<String, dynamic>>();
    return (
      total: json['total'] as int?,
      rows: [for (final r in rawRows) (id: r[r'$id'] as String, value: _decodeRow(table, r))],
    );
  }

  Future<T> get<T>(TableRef<T> table, String rowId) async {
    final json = requireJson(
      await _client.request(method: 'GET', path: '${_rowsPath(table)}/$rowId'),
      rowId,
    );
    return _decodeRow(table, json);
  }

  /// [rowId] is optional — omit it to let the server generate a UUIDv7, or pass
  /// [Uid.unique()][../ids.dart] to know the id before this call returns.
  Future<T> create<T>(TableRef<T> table, {String? rowId, required T data, List<String> permissions = const []}) async {
    final json = requireJson(
      await _client.request(
        method: 'POST',
        path: _rowsPath(table),
        body: {
          'rowId': ?rowId,
          'data': table.codec.encode(data),
          if (permissions.isNotEmpty) 'permissions': permissions,
        },
      ),
      rowId ?? _rowsPath(table),
    );
    return _decodeRow(table, json);
  }

  /// Genuinely partial: only the keys present in [data] are sent, and the server
  /// only rewrites those columns. Pass a raw field map, not a full [T] — that's the
  /// point of PATCH semantics (CLAUDE.md: "PATCH sends only changed fields").
  Future<T> update<T>(TableRef<T> table, String rowId, {Map<String, dynamic>? data, List<String>? permissions}) async {
    final json = requireJson(
      await _client.request(
        method: 'PATCH',
        path: '${_rowsPath(table)}/$rowId',
        body: {
          'data': ?data,
          'permissions': ?permissions,
        },
      ),
      rowId,
    );
    return _decodeRow(table, json);
  }

  Future<void> delete<T>(TableRef<T> table, String rowId) async {
    await _client.request(method: 'DELETE', path: '${_rowsPath(table)}/$rowId');
  }

  static String _rowsPath(TableRef<dynamic> table) =>
      '/v1/databases/${table.databaseId}/tables/${table.tableId}/rows';

  static T _decodeRow<T>(TableRef<T> table, Map<String, dynamic> json) {
    final meta = (
      id: json[r'$id'] as String,
      tableId: json[r'$tableId'] as String,
      databaseId: json[r'$databaseId'] as String,
      createdAt: DateTime.parse(json[r'$createdAt'] as String),
      updatedAt: DateTime.parse(json[r'$updatedAt'] as String),
      permissions: ((json[r'$permissions'] as List?) ?? const []).cast<String>(),
    );
    final data = Map<String, dynamic>.from(json)..removeWhere((key, _) => key.startsWith(r'$'));
    try {
      return table.codec.decode(data, meta);
    } catch (error, stackTrace) {
      Error.throwWithStackTrace(
        PraxyDecodeException(
          field: table.tableId,
          expected: 'a value decodable by this table\'s RowCodec',
          message: "Failed to decode row '${meta.id}' in table '${table.tableId}': $error",
        ),
        stackTrace,
      );
    }
  }
}
