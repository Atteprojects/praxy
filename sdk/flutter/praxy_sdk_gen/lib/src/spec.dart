/// Reading `docs/openapi/v1.json` — just enough of OpenAPI to describe Praxy's own
/// document, deliberately not a general-purpose parser.
library;

/// One operation, reduced to what a Dart method needs.
final class Operation {
  Operation({
    required this.operationId,
    required this.method,
    required this.path,
    required this.pathParameters,
    required this.queryParameters,
    required this.bodyProperties,
    required this.requiredBodyProperties,
    required this.responseSchema,
    required this.summary,
  });

  final String operationId;
  final String method;
  final String path;
  final List<String> pathParameters;
  final List<Parameter> queryParameters;

  /// Request-body properties, flattened into named method parameters rather than a
  /// request object. A caller writes `create(email: 'a@b.com')`, not
  /// `create(ServerCreateUserRequest(email: 'a@b.com'))` — matching every
  /// hand-written service in `praxy_core`, so generated and hand-written methods
  /// are indistinguishable at the call site.
  final Map<String, SchemaRef> bodyProperties;
  final Set<String> requiredBodyProperties;

  /// The success response's schema, or null for a 204.
  final SchemaRef? responseSchema;
  final String? summary;

  /// The method name: `users.updateEmail` → `updateEmail`.
  String get methodName =>
      operationId.contains('.') ? operationId.split('.').last : operationId;
}

final class Parameter {
  Parameter({required this.name, required this.schema, required this.required});
  final String name;
  final SchemaRef schema;
  final bool required;
}

/// A resolved schema: either a reference to a named component or an inline type.
final class SchemaRef {
  SchemaRef({this.componentName, required this.type, this.itemType, this.format});

  /// Set when this is a `$ref` to `#/components/schemas/<name>`.
  final String? componentName;
  final String type;
  final SchemaRef? itemType;
  final String? format;
}

/// Parses the operations of one `operationId` namespace (`users.*`) out of the document.
List<Operation> parseNamespace(Map<String, dynamic> document, String namespace) {
  final paths = document['paths'] as Map<String, dynamic>;
  final operations = <Operation>[];

  for (final entry in paths.entries) {
    final item = entry.value as Map<String, dynamic>;
    for (final verb in const ['get', 'post', 'put', 'patch', 'delete']) {
      final op = item[verb];
      if (op is! Map<String, dynamic>) continue;
      final operationId = op['operationId'] as String?;
      if (operationId == null || !operationId.startsWith('$namespace.')) continue;

      final parameters = (op['parameters'] as List?) ?? const [];
      final pathParameters = <String>[];
      final queryParameters = <Parameter>[];
      for (final raw in parameters) {
        final p = raw as Map<String, dynamic>;
        final name = p['name'] as String;
        if (p['in'] == 'path') {
          pathParameters.add(name);
        } else if (p['in'] == 'query') {
          queryParameters.add(
            Parameter(
              name: name,
              schema: resolveSchema(p['schema'] as Map<String, dynamic>),
              required: p['required'] == true,
            ),
          );
        }
      }

      final bodySchema = _bodySchema(document, op);
      operations.add(
        Operation(
          operationId: operationId,
          method: verb.toUpperCase(),
          path: entry.key,
          pathParameters: pathParameters,
          queryParameters: queryParameters,
          bodyProperties: bodySchema.properties,
          requiredBodyProperties: bodySchema.required,
          responseSchema: _responseSchema(op),
          summary: op['summary'] as String?,
        ),
      );
    }
  }

  operations.sort((a, b) => a.operationId.compareTo(b.operationId));
  return operations;
}

final class _Body {
  _Body(this.properties, this.required);
  final Map<String, SchemaRef> properties;
  final Set<String> required;
}

_Body _bodySchema(Map<String, dynamic> document, Map<String, dynamic> op) {
  final content = (op['requestBody'] as Map<String, dynamic>?)?['content'] as Map<String, dynamic>?;
  final json = content?['application/json'] as Map<String, dynamic>?;
  final schema = json?['schema'] as Map<String, dynamic>?;
  if (schema == null) return _Body(const {}, const {});

  final resolved = _dereference(document, schema);
  final properties = (resolved['properties'] as Map<String, dynamic>?) ?? const {};
  return _Body(
    {
      for (final e in properties.entries)
        e.key: resolveSchema(e.value as Map<String, dynamic>),
    },
    ((resolved['required'] as List?) ?? const []).cast<String>().toSet(),
  );
}

SchemaRef? _responseSchema(Map<String, dynamic> op) {
  final responses = (op['responses'] as Map<String, dynamic>?) ?? const {};
  for (final status in const ['200', '201']) {
    final response = responses[status] as Map<String, dynamic>?;
    final schema = ((response?['content'] as Map<String, dynamic>?)?['application/json']
            as Map<String, dynamic>?)?['schema']
        as Map<String, dynamic>?;
    if (schema != null) return resolveSchema(schema);
  }
  return null;
}

Map<String, dynamic> _dereference(Map<String, dynamic> document, Map<String, dynamic> schema) {
  final ref = schema[r'$ref'] as String?;
  if (ref == null) return schema;
  final name = ref.split('/').last;
  final schemas = (document['components'] as Map<String, dynamic>)['schemas'] as Map<String, dynamic>;
  return schemas[name] as Map<String, dynamic>;
}

/// Turns one schema node into a [SchemaRef].
///
/// The `type` field is where .NET's document needs care: an integer property is
/// emitted as `type: ["integer", "string"]` with an integer `format`, even though the
/// wire only ever carries a number. Taking that union literally would give every
/// `total` in the SDK a `Object` type. `openapi-typescript` resolves the same union to
/// `number` for the console, so this resolves it to `int` — the same answer, reached
/// the same way, rather than a second opinion about the same document.
SchemaRef resolveSchema(Map<String, dynamic> schema) {
  final ref = schema[r'$ref'] as String?;
  if (ref != null) {
    return SchemaRef(componentName: ref.split('/').last, type: 'object');
  }

  final rawType = schema['type'];
  final types = switch (rawType) {
    String s => [s],
    List l => l.cast<String>(),
    _ => <String>['object'],
  };
  final type = types.firstWhere((t) => t != 'null' && t != 'string', orElse: () => types.first);

  return SchemaRef(
    type: type,
    format: schema['format'] as String?,
    itemType: type == 'array' && schema['items'] is Map<String, dynamic>
        ? resolveSchema(schema['items'] as Map<String, dynamic>)
        : null,
  );
}
