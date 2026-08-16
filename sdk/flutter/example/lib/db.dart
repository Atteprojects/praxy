import 'package:praxy_flutter/praxy_flutter.dart';

/// The example's one table. Database/table ids are real Praxy ids (32-hex uuids,
/// not the human `key`) — every row endpoint requires them
/// (`RowEndpoints.ResolveAsync`/`SchemaLookup` both reject a key). Create a
/// database "main" and a table "todos" (columns `title: string`, `done: boolean`)
/// through the console, then paste their ids here — exactly what a real app does,
/// same as hardcoding a collection id in an Appwrite app.
class PraxyIds {
  static const databaseId = String.fromEnvironment('PRAXY_DATABASE_ID');
  static const tableId = String.fromEnvironment('PRAXY_TABLE_ID');
}

final class Todo {
  /// [id] is `null` for a not-yet-created todo — [RowCodec.decode] always fills it
  /// in from [RowMeta], since it never runs before a row exists on the server.
  const Todo({this.id, required this.title, required this.done});

  final String? id;
  final String title;
  final bool done;

  Todo copyWith({String? title, bool? done}) =>
      Todo(id: id, title: title ?? this.title, done: done ?? this.done);

  static Todo _decode(Map<String, dynamic> data, RowMeta meta) =>
      Todo(id: meta.id, title: data['title'] as String? ?? '', done: data['done'] as bool? ?? false);

  // id is metadata, not row data -- deliberately not sent back on write.
  static Map<String, dynamic> _encode(Todo value) => {'title': value.title, 'done': value.done};

  static const codec = RowCodec<Todo>(decode: _decode, encode: _encode);
}

abstract final class TodoColumns {
  static const title = Col<String>('title');
  static const done = Col<bool>('done');
}

abstract final class Db {
  static const todos = TableRef<Todo>(PraxyIds.databaseId, PraxyIds.tableId, codec: Todo.codec);
}
