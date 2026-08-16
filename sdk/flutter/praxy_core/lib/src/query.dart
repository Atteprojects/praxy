import 'dart:convert';

/// A typed column reference. Carries no behaviour beyond its wire [name] — [T] only
/// exists so [Query.equal] and friends reject the wrong value type at compile time.
final class Col<T> {
  const Col(this.name);

  final String name;

  @override
  String toString() => name;
}

/// One query, kept as a value object end to end rather than pre-JSON-encoded —
/// unlike Appwrite's `Query.equal()` (which returns a `String`, forcing `Query.or()`
/// to `jsonDecode` its own inputs), a Praxy [Query] can be inspected, composed, and
/// encoded exactly once, at the point it's sent. Matches every method in
/// architecture.md §4.6 / research/appwrite-api.md's JSON-per-query wire format:
/// `{"method":"equal","attribute":"title","values":["Hello"]}`.
final class Query {
  const Query._(this.method, {this.attribute, this.values = const [], this.children = const []});

  final String method;
  final String? attribute;
  final List<Object?> values;
  final List<Query> children;

  static Query equal<T>(Col<T> col, T value) =>
      Query._('equal', attribute: col.name, values: [_encode(value)]);

  /// `equal` with multiple values is an OR within that one attribute (matches the
  /// server's `count >= 1` arity — a single [equal] call is just the `count == 1` case).
  static Query equalAny<T>(Col<T> col, List<T> values) =>
      Query._('equal', attribute: col.name, values: values.map(_encode).toList());

  static Query notEqual<T>(Col<T> col, T value) =>
      Query._('notEqual', attribute: col.name, values: [_encode(value)]);

  static Query lessThan<T>(Col<T> col, T value) =>
      Query._('lessThan', attribute: col.name, values: [_encode(value)]);

  static Query lessThanEqual<T>(Col<T> col, T value) =>
      Query._('lessThanEqual', attribute: col.name, values: [_encode(value)]);

  static Query greaterThan<T>(Col<T> col, T value) =>
      Query._('greaterThan', attribute: col.name, values: [_encode(value)]);

  static Query greaterThanEqual<T>(Col<T> col, T value) =>
      Query._('greaterThanEqual', attribute: col.name, values: [_encode(value)]);

  static Query between<T>(Col<T> col, T start, T end) =>
      Query._('between', attribute: col.name, values: [_encode(start), _encode(end)]);

  static Query isNull(Col<dynamic> col) => Query._('isNull', attribute: col.name);

  static Query isNotNull(Col<dynamic> col) => Query._('isNotNull', attribute: col.name);

  static Query startsWith(Col<String> col, String value) =>
      Query._('startsWith', attribute: col.name, values: [value]);

  static Query endsWith(Col<String> col, String value) =>
      Query._('endsWith', attribute: col.name, values: [value]);

  static Query contains<T>(Col<T> col, T value) =>
      Query._('contains', attribute: col.name, values: [_encode(value)]);

  /// Requires a declared fulltext index on [col] — the server rejects `search`
  /// without one rather than silently falling back to `ILIKE`.
  static Query search(Col<String> col, String value) =>
      Query._('search', attribute: col.name, values: [value]);

  static Query select(List<Col<dynamic>> columns) =>
      Query._('select', values: columns.map((c) => c.name).toList());

  static Query orderAsc(Col<dynamic> col) => Query._('orderAsc', attribute: col.name);

  static Query orderDesc(Col<dynamic> col) => Query._('orderDesc', attribute: col.name);

  static Query limit(int count) => Query._('limit', values: [count]);

  static Query offset(int count) => Query._('offset', values: [count]);

  static Query cursorAfter(String rowId) => Query._('cursorAfter', values: [rowId]);

  static Query cursorBefore(String rowId) => Query._('cursorBefore', values: [rowId]);

  static Query and(List<Query> queries) => Query._('and', children: queries);

  static Query or(List<Query> queries) => Query._('or', children: queries);

  /// The escape hatch for a method this builder doesn't cover yet — stays a real
  /// [Query] value like every other constructor, never a raw string.
  static Query raw(String method, {String? attribute, List<Object?> values = const []}) =>
      Query._(method, attribute: attribute, values: values);

  Map<String, dynamic> toJson() => {
    'method': method,
    if (attribute != null) 'attribute': attribute,
    if (children.isNotEmpty)
      'values': children.map((c) => c.toJson()).toList()
    else if (values.isNotEmpty)
      'values': values,
  };

  /// The wire form sent as one entry of the repeated `queries[]` param.
  String encode() => jsonEncode(toJson());

  static Object? _encode(Object? value) => switch (value) {
    DateTime d => d.toUtc().toIso8601String(),
    _ => value,
  };
}
