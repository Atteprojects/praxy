/// The system fields every row carries, passed alongside the row's own columns so a
/// [RowDecoder] can use them without them polluting the decoded value's data map.
typedef RowMeta = ({
  String id,
  String tableId,
  String databaseId,
  DateTime createdAt,
  DateTime updatedAt,
  List<String> permissions,
  /// Meters from an `orderNear` query's point — null unless the request carried `orderNear`.
  double? distance,
});

/// One decode signature used everywhere a row is turned into a [T] — Appwrite's own
/// SDK has two incompatible signatures (`Row.convertTo` vs `RowList.convertTo`) that
/// are a static error to satisfy with the same tear-off, because Dart parameters are
/// contravariant. There is exactly one [RowDecoder] shape in Praxy.
typedef RowDecoder<T> = T Function(Map<String, dynamic> data, RowMeta meta);
typedef RowEncoder<T> = Map<String, dynamic> Function(T value);

/// How a table's rows convert to and from [T]. Carried by [TableRef] so every
/// generic service method (`list<T>`, `create<T>`, …) has the codec it needs without
/// the caller ever passing one explicitly or writing a cast.
final class RowCodec<T> {
  const RowCodec({required this.decode, required this.encode});

  final RowDecoder<T> decode;
  final RowEncoder<T> encode;

  /// The untyped escape hatch — rows stay as `Map<String, dynamic>`.
  static const RowCodec<Map<String, dynamic>> raw = RowCodec(
    decode: _rawDecode,
    encode: _rawEncode,
  );

  static Map<String, dynamic> _rawDecode(Map<String, dynamic> data, RowMeta meta) => data;
  static Map<String, dynamic> _rawEncode(Map<String, dynamic> value) => value;
}

/// A table, typed. The user's whole schema is one small hand-written file of these —
/// no build_runner, no generated casts; generics flow through from here.
final class TableRef<T> {
  const TableRef(this.databaseId, this.tableId, {required this.codec});

  final String databaseId;
  final String tableId;
  final RowCodec<T> codec;

  /// A table left untyped on purpose — `TableRef.raw('main', 'logs')`.
  static TableRef<Map<String, dynamic>> raw(String databaseId, String tableId) =>
      TableRef(databaseId, tableId, codec: RowCodec.raw);
}
