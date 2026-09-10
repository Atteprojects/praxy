import 'spec.dart';

/// What to generate, and what to reuse.
///
/// [modelTypes] is the important half. `praxy_core` already hand-writes the models
/// that carry design — `AppUser`, `AppSession`, `SessionList` — and regenerating
/// them would either duplicate those types or replace them with worse ones. So the
/// generator is told which schemas already have a Dart equivalent and emits classes
/// only for the ones that don't. This is the "generate the mechanical surface,
/// hand-write what has design in it" split, made explicit and reviewable rather
/// than inferred.
final class ServiceSpec {
  const ServiceSpec({
    required this.namespace,
    required this.className,
    required this.description,
    required this.modelTypes,
    this.generateModels = const {},
    this.documentGaps = const {},
  });

  final String namespace;
  final String className;
  final String description;

  /// OpenAPI schema name → the Dart type that already models it.
  final Map<String, String> modelTypes;

  /// OpenAPI schema name → the Dart class name to emit for it.
  final Map<String, String> generateModels;

  /// operationId → what the document fails to describe about it.
  ///
  /// The generator can only emit what the document says, and the document does not
  /// describe query parameters that a handler reads straight out of `HttpContext`
  /// (37 such reads across 16 endpoint files, `limit`/`offset`/`search` among them).
  /// Emitting the resulting method silently would ship an SDK that looks complete
  /// and quietly cannot paginate. Listing the gap here puts it in the generated
  /// method's own doc comment instead, where the person calling it will read it —
  /// and it disappears on its own once the document describes the parameters,
  /// because the note then has nothing to attach to.
  final Map<String, String> documentGaps;
}

/// Thrown when the document cannot support a faithful method — never worked around
/// silently, because a generator that guesses produces an SDK whose bugs look like
/// the server's.
final class GeneratorException implements Exception {
  GeneratorException(this.message);
  final String message;
  @override
  String toString() => 'GeneratorException: $message';
}

const _header = '''
// GENERATED — do not edit by hand.
//
// Produced by `dart run praxy_sdk_gen` from docs/openapi/v1.json. Edit the API's
// endpoint definitions (or the generator) and regenerate; `check:sdk-gen` fails the
// build if this file and the document disagree.
''';

String generateService(Map<String, dynamic> document, ServiceSpec spec) {
  final operations = parseNamespace(document, spec.namespace);
  if (operations.isEmpty) {
    throw GeneratorException(
      'No operations found for namespace "${spec.namespace}". The document uses '
      'operationIds of the form "<namespace>.<method>" — check the namespace spelling.',
    );
  }

  final unknown = spec.documentGaps.keys
      .where((id) => !operations.any((op) => op.operationId == id))
      .toList();
  if (unknown.isNotEmpty) {
    // A gap note that no longer matches an operation is worse than no note: it means
    // either the operation was renamed or the gap was fixed, and in both cases the
    // remaining entry is a lie about the current document.
    throw GeneratorException(
      'documentGaps names operations that do not exist in namespace '
      '"${spec.namespace}": ${unknown.join(', ')}. Remove them if the document now '
      'describes those parameters.',
    );
  }

  final buffer = StringBuffer()
    ..writeln(_header)
    ..writeln("import '../../client.dart';")
    ..writeln("import '../../json_utils.dart';")
    ..writeln("import '../../models.dart';")
    ..writeln()
    ..writeln('/// ${spec.description}')
    ..writeln('final class ${spec.className} {')
    ..writeln('  const ${spec.className}(this._client);')
    ..writeln()
    ..writeln('  final Praxy _client;');

  for (final op in operations) {
    buffer.writeln();
    buffer.write(_method(op, spec));
  }
  buffer.writeln('}');

  for (final entry in spec.generateModels.entries) {
    buffer.writeln();
    buffer.write(_model(document, entry.key, entry.value, spec));
  }

  return buffer.toString();
}

String _method(Operation op, ServiceSpec spec) {
  final returnType = op.responseSchema == null ? 'void' : _dartType(op.responseSchema!, spec);

  final positional = op.pathParameters.map((p) => 'String ${_camel(p)}').join(', ');
  final named = <String>[
    for (final name in op.bodyProperties.keys)
      op.requiredBodyProperties.contains(name)
          ? 'required ${_dartType(op.bodyProperties[name]!, spec)} ${_camel(name)}'
          : '${_dartType(op.bodyProperties[name]!, spec)}? ${_camel(name)}',
    for (final q in op.queryParameters) '${_dartType(q.schema, spec)}? ${_camel(q.name)}',
  ];

  final signature = StringBuffer('  Future<$returnType> ${op.methodName}(');
  signature.write(positional);
  if (named.isNotEmpty) {
    if (positional.isNotEmpty) signature.write(', ');
    signature.write('{${named.join(', ')}}');
  }
  signature.write(') async');

  final path = op.path.replaceAllMapped(
    RegExp(r'\{(\w+)\}'),
    (m) => '\$${_camel(m.group(1)!)}',
  );

  final args = StringBuffer("method: '${op.method}', path: '$path'");
  if (op.bodyProperties.isNotEmpty) {
    final fields = op.bodyProperties.keys
        .map(
          (name) => op.requiredBodyProperties.contains(name)
              ? "'$name': ${_camel(name)}"
              : "'$name': ?${_camel(name)}",
        )
        .join(', ');
    args.write(', body: {$fields}');
  }
  if (op.queryParameters.isNotEmpty) {
    final entries = op.queryParameters
        .map((q) {
          final name = _camel(q.name);
          // A String needs no interpolation at all, and a non-String needs no braces around a
          // bare identifier. Both are analyzer lints, and generated code that trips them is
          // noise every reader has to learn to ignore.
          final value = _dartType(q.schema, spec) == 'String' ? name : "'\$$name'";
          return "if ($name != null) '${q.name}': [$value]";
        })
        .join(', ');
    args.write(', query: {$entries}');
  }

  final notes = <String>[
    ?op.summary,
    if (spec.documentGaps[op.operationId] case final gap?)
      'Known gap in the OpenAPI document this is generated from: $gap',
  ];
  final doc = notes.map((n) => '  /// $n\n').join('  ///\n');

  if (op.responseSchema == null) {
    return '$doc$signature {\n'
        '    await _client.request($args);\n'
        '  }\n';
  }
  return '$doc$signature =>\n'
      '      $returnType.fromJson(requireJson(await _client.request($args), \'${op.path}\'));\n';
}

/// Emits a model class for a schema with no hand-written equivalent.
String _model(Map<String, dynamic> document, String schemaName, String className, ServiceSpec spec) {
  final schemas = (document['components'] as Map<String, dynamic>)['schemas'] as Map<String, dynamic>;
  final schema = schemas[schemaName] as Map<String, dynamic>?;
  if (schema == null) throw GeneratorException('Schema "$schemaName" is not in the document.');

  final properties = (schema['properties'] as Map<String, dynamic>?) ?? const {};
  final required = ((schema['required'] as List?) ?? const []).cast<String>().toSet();

  final fields = <String, SchemaRef>{
    for (final e in properties.entries) e.key: resolveSchema(e.value as Map<String, dynamic>),
  };

  final buffer = StringBuffer()
    ..writeln('/// ${schema['description'] ?? 'Wire shape of `$schemaName`.'}')
    ..writeln('final class $className {')
    ..writeln('  const $className({')
    ..writeAll([
      for (final name in fields.keys)
        '    ${required.contains(name) ? 'required ' : ''}this.${_camel(name)},\n',
    ])
    ..writeln('  });')
    ..writeln();

  for (final e in fields.entries) {
    final type = _dartType(e.value, spec);
    buffer.writeln('  final $type${required.contains(e.key) ? '' : '?'} ${_camel(e.key)};');
  }

  buffer
    ..writeln()
    ..writeln('  factory $className.fromJson(Map<String, dynamic> json) => $className(');
  for (final e in fields.entries) {
    buffer.writeln('    ${_camel(e.key)}: ${_decode(e.value, "json['${e.key}']", spec)},');
  }
  buffer
    ..writeln('  );')
    ..writeln('}');
  return buffer.toString();
}

String _dartType(SchemaRef schema, ServiceSpec spec) {
  if (schema.componentName case final name?) {
    if (name == 'JsonNode') return 'Map<String, dynamic>';
    final mapped = spec.modelTypes[name] ?? spec.generateModels[name];
    if (mapped == null) {
      throw GeneratorException(
        'Schema "$name" has no Dart type. Add it to ServiceSpec.modelTypes if '
        'praxy_core already models it, or to generateModels to emit a class for it.',
      );
    }
    return mapped;
  }
  return switch (schema.type) {
    'string' => schema.format == 'date-time' ? 'DateTime' : 'String',
    'integer' => 'int',
    'number' => 'double',
    'boolean' => 'bool',
    'array' => 'List<${_dartType(schema.itemType!, spec)}>',
    _ => 'Map<String, dynamic>',
  };
}

/// The decode expression for one field.
///
/// Every field is read defensively against absence rather than null. `Program.cs`
/// serializes with `WhenWritingNull`, so an unset property is *missing from the JSON
/// object*, never present-and-null — the bug that shipped in PR #55 on the console
/// side. `as X?` on a missing key yields null rather than throwing, which is the
/// behaviour a generated codec needs in every language.
String _decode(SchemaRef schema, String access, ServiceSpec spec) {
  if (schema.componentName case final name?) {
    if (name == 'JsonNode') return "($access as Map?)?.cast<String, dynamic>() ?? const {}";
    final type = _dartType(schema, spec);
    return '$type.fromJson($access as Map<String, dynamic>)';
  }
  return switch (schema.type) {
    'string' when schema.format == 'date-time' => 'DateTime.parse($access as String)',
    'string' => '$access as String',
    // Read leniently: the document types an integer as ["integer","string"], and while
    // the wire only ever sends a number, a codec that accepts both cannot be wrong.
    'integer' => '($access is String ? int.parse($access as String) : $access as int)',
    'number' => '($access as num).toDouble()',
    'boolean' => '$access as bool',
    'array' => _decodeList(schema, access, spec),
    _ => '($access as Map?)?.cast<String, dynamic>() ?? const {}',
  };
}

String _decodeList(SchemaRef schema, String access, ServiceSpec spec) {
  final item = schema.itemType!;
  if (item.componentName != null && item.componentName != 'JsonNode') {
    final type = _dartType(item, spec);
    return '[for (final e in ($access as List).cast<Map<String, dynamic>>()) $type.fromJson(e)]';
  }
  return '(($access as List?) ?? const []).cast<${_dartType(item, spec)}>()';
}

String _camel(String name) => name.isEmpty ? name : name[0].toLowerCase() + name.substring(1);
