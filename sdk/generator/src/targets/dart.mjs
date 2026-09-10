// @ts-check
/**
 * Dart target: emits `praxy_core`'s generated services.
 *
 * A target owns exactly two things — what the code looks like, and where it goes. Everything about
 * reading the document lives in `../spec.mjs`, so this file is the entire cost of supporting Dart.
 */

import { methodName, resolveSchema } from "../spec.mjs";

/** @typedef {import("../spec.mjs").Operation} Operation */
/** @typedef {import("../spec.mjs").SchemaRef} SchemaRef */

const HEADER = `// GENERATED — do not edit by hand.
//
// Produced by \`node sdk/generator/bin/generate.mjs\` from docs/openapi/v1.json. Edit the API's
// endpoint definitions (or the generator) and regenerate; CI fails the build if this file and the
// document disagree.
`;

export const dart = {
  name: "dart",

  /** @param {any} service */
  outputPath(service) {
    return `sdk/flutter/praxy_core/lib/src/services/generated/${service.namespace}_service.dart`;
  },

  /** Formatted by the real formatter, in the job that has Dart — never by careful string building. */
  formatCommand: (files) => ["dart", ["format", "--page-width=110", ...files], "sdk/flutter"],

  /**
   * @param {any} service
   * @param {Operation[]} operations
   * @param {any} document
   */
  render(service, operations, document) {
    const config = service.targets.dart;
    const lines = [
      HEADER,
      `import '../../client.dart';`,
      `import '../../json_utils.dart';`,
      `import '../../models.dart';`,
      ``,
      ...docComment(service.description),
      `final class ${config.className} {`,
      `  const ${config.className}(this._client);`,
      ``,
      `  final Praxy _client;`,
    ];

    for (const operation of operations) {
      lines.push("", method(operation, service, config));
    }
    lines.push("}");

    for (const [schemaName, className] of Object.entries(config.generateModels ?? {})) {
      lines.push("", model(document, schemaName, /** @type {string} */ (className), config));
    }
    return lines.join("\n") + "\n";
  },
};

/** Dart doc comment from neutral paragraphs — `///` lines, blank `///` between paragraphs. */
/** @param {string[]} paragraphs */
function docComment(paragraphs) {
  return paragraphs.flatMap((paragraph, index) =>
    index === 0 ? [`/// ${paragraph}`] : ["///", `/// ${paragraph}`],
  );
}

/** @param {Operation} operation @param {any} service @param {any} config */
function method(operation, service, config) {
  const returnType = operation.responseSchema === null ? "void" : dartType(operation.responseSchema, config);

  const positional = operation.pathParameters.map((p) => `String ${camel(p)}`).join(", ");
  const named = [
    ...Object.entries(operation.bodyProperties).map(([name, schema]) =>
      operation.requiredBodyProperties.has(name)
        ? `required ${dartType(schema, config)} ${camel(name)}`
        : `${dartType(schema, config)}? ${camel(name)}`,
    ),
    ...operation.queryParameters.map((q) => `${dartType(q.schema, config)}? ${camel(q.name)}`),
  ];

  let signature = `  Future<${returnType}> ${methodName(operation)}(${positional}`;
  if (named.length > 0) signature += `${positional ? ", " : ""}{${named.join(", ")}}`;
  signature += ") async";

  const path = operation.path.replace(/\{(\w+)\}/g, (_, name) => `$${camel(name)}`);
  let args = `method: '${operation.method}', path: '${path}'`;

  if (Object.keys(operation.bodyProperties).length > 0) {
    const fields = Object.keys(operation.bodyProperties)
      .map((name) => (operation.requiredBodyProperties.has(name) ? `'${name}': ${camel(name)}` : `'${name}': ?${camel(name)}`))
      .join(", ");
    args += `, body: {${fields}}`;
  }
  if (operation.queryParameters.length > 0) {
    const entries = operation.queryParameters
      .map((q) => {
        const name = camel(q.name);
        // A String needs no interpolation at all, and a non-String needs no braces around a bare
        // identifier. Both are analyzer lints, and generated code that trips them is noise every
        // reader has to learn to ignore.
        const value = dartType(q.schema, config) === "String" ? name : `'$${name}'`;
        return `if (${name} != null) '${q.name}': [${value}]`;
      })
      .join(", ");
    args += `, query: {${entries}}`;
  }

  const notes = [operation.summary, service.documentGaps?.[operation.operationId] && `Known gap in the OpenAPI document this is generated from: ${service.documentGaps[operation.operationId]}`].filter(Boolean);
  const doc = notes.map((n) => `  /// ${n}\n`).join("  ///\n");

  if (operation.responseSchema === null) {
    return `${doc}${signature} {\n    await _client.request(${args});\n  }`;
  }
  return `${doc}${signature} =>\n      ${returnType}.fromJson(requireJson(await _client.request(${args}), '${operation.path}'));`;
}

/** @param {any} document @param {string} schemaName @param {string} className @param {any} config */
function model(document, schemaName, className, config) {
  const schema = document.components.schemas[schemaName];
  if (!schema) throw new GeneratorError(`Schema "${schemaName}" is not in the document.`);

  const required = new Set(schema.required ?? []);
  const fields = Object.entries(schema.properties ?? {}).map(([name, property]) => ({
    name,
    schema: /** @type {SchemaRef} */ (resolveSchema(property)),
  }));

  const lines = [
    `/// ${schema.description ?? `Wire shape of \`${schemaName}\`.`}`,
    `final class ${className} {`,
    `  const ${className}({`,
    ...fields.map((f) => `    ${required.has(f.name) ? "required " : ""}this.${camel(f.name)},`),
    `  });`,
    ``,
    ...fields.map((f) => `  final ${dartType(f.schema, config)}${required.has(f.name) ? "" : "?"} ${camel(f.name)};`),
    ``,
    `  factory ${className}.fromJson(Map<String, dynamic> json) => ${className}(`,
    ...fields.map((f) => `    ${camel(f.name)}: ${decode(f.schema, `json['${f.name}']`, config)},`),
    `  );`,
    `}`,
  ];
  return lines.join("\n");
}

/** @param {SchemaRef} schema @param {any} config */
function dartType(schema, config) {
  if (schema.componentName) {
    if (schema.componentName === "JsonNode") return "Map<String, dynamic>";
    const mapped = config.modelTypes[schema.componentName] ?? config.generateModels?.[schema.componentName];
    if (!mapped) {
      throw new GeneratorError(
        `Schema "${schema.componentName}" has no Dart type. Add it to this service's dart.modelTypes ` +
          `if praxy_core already models it, or to generateModels to emit a class for it.`,
      );
    }
    return mapped;
  }
  switch (schema.type) {
    case "string":
      return schema.format === "date-time" ? "DateTime" : "String";
    case "integer":
      return "int";
    case "number":
      return "double";
    case "boolean":
      return "bool";
    case "array":
      return `List<${dartType(/** @type {SchemaRef} */ (schema.itemType), config)}>`;
    default:
      return "Map<String, dynamic>";
  }
}

/**
 * Every field is read defensively against *absence*, not null. `Program.cs` serializes with
 * `WhenWritingNull`, so an unset property is missing from the JSON object entirely rather than
 * present-and-null — the bug that shipped on the console side in PR #55. `as X?` on a missing key
 * yields null instead of throwing, which is what a generated codec needs in every language.
 *
 * @param {SchemaRef} schema @param {string} access @param {any} config
 */
function decode(schema, access, config) {
  if (schema.componentName) {
    if (schema.componentName === "JsonNode") return `(${access} as Map?)?.cast<String, dynamic>() ?? const {}`;
    return `${dartType(schema, config)}.fromJson(${access} as Map<String, dynamic>)`;
  }
  switch (schema.type) {
    case "string":
      return schema.format === "date-time" ? `DateTime.parse(${access} as String)` : `${access} as String`;
    case "integer":
      // Read leniently: the document types an integer as ["integer","string"], and while the wire
      // only ever sends a number, a codec that accepts both cannot be wrong.
      return `(${access} is String ? int.parse(${access} as String) : ${access} as int)`;
    case "number":
      return `(${access} as num).toDouble()`;
    case "boolean":
      return `${access} as bool`;
    case "array":
      return decodeList(schema, access, config);
    default:
      return `(${access} as Map?)?.cast<String, dynamic>() ?? const {}`;
  }
}

/** @param {SchemaRef} schema @param {string} access @param {any} config */
function decodeList(schema, access, config) {
  const item = /** @type {SchemaRef} */ (schema.itemType);
  if (item.componentName && item.componentName !== "JsonNode") {
    return `[for (final e in (${access} as List).cast<Map<String, dynamic>>()) ${dartType(item, config)}.fromJson(e)]`;
  }
  return `((${access} as List?) ?? const []).cast<${dartType(item, config)}>()`;
}

/** @param {string} name */
function camel(name) {
  return name.length === 0 ? name : name[0].toLowerCase() + name.slice(1);
}

export class GeneratorError extends Error {}
