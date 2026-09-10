// @ts-check
/**
 * C# target: emits `Praxy.Sdk`'s generated services.
 *
 * Same two responsibilities as every target — what the code looks like, and where it goes. Nothing
 * here knows about OpenAPI; that lives in `../spec.mjs` and is shared with Dart.
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

export const csharp = {
  name: "csharp",

  /** @param {any} service */
  outputPath(service) {
    return `sdk/dotnet/Praxy.Sdk/Services/Generated/${pascal(service.targets.csharp.className)}.cs`;
  },

  formatCommand: (files) => ["dotnet", ["format", "whitespace", "Praxy.sln", "--include", ...files], "."],

  /**
   * @param {any} service
   * @param {Operation[]} operations
   * @param {any} document
   */
  render(service, operations, document) {
    const config = service.targets.csharp;
    const lines = [
      HEADER,
      `using System.Text.Json.Serialization;`,
      ``,
      `namespace Praxy.Sdk.Services.Generated;`,
      ``,
      ...xmlDoc(service.description, ""),
      `public sealed class ${config.className}(PraxyClient client)`,
      `{`,
    ];

    for (const [index, operation] of operations.entries()) {
      if (index > 0) lines.push("");
      lines.push(method(operation, service, config));
    }
    lines.push("}");

    for (const [schemaName, className] of Object.entries(config.generateModels ?? {})) {
      lines.push("", model(document, schemaName, /** @type {string} */ (className), config));
    }
    return lines.join("\n") + "\n";
  },
};

/**
 * `<summary>` from neutral paragraphs. Multi-paragraph docs use `<para>`, which is what every other
 * XML doc comment in this repository does.
 *
 * @param {string[]} paragraphs @param {string} indent
 */
function xmlDoc(paragraphs, indent) {
  const escape = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  if (paragraphs.length === 1) return [`${indent}/// <summary>${escape(paragraphs[0])}</summary>`];
  return [
    `${indent}/// <summary>`,
    ...paragraphs.map((p) => `${indent}/// <para>${escape(p)}</para>`),
    `${indent}/// </summary>`,
  ];
}

/** @param {Operation} operation @param {any} service @param {any} config */
function method(operation, service, config) {
  const returnType = operation.responseSchema === null ? "Task" : `Task<${csharpType(operation.responseSchema, config)}?>`;

  /** Required parameters first, then optional — C# has no named-argument-only parameters. */
  const required = [
    ...operation.pathParameters.map((p) => `string ${camel(p)}`),
    ...Object.keys(operation.bodyProperties)
      .filter((name) => operation.requiredBodyProperties.has(name))
      .map((name) => `${csharpType(operation.bodyProperties[name], config)} ${camel(name)}`),
  ];
  const optional = [
    ...Object.keys(operation.bodyProperties)
      .filter((name) => !operation.requiredBodyProperties.has(name))
      .map((name) => `${nullable(csharpType(operation.bodyProperties[name], config))} ${camel(name)} = null`),
    ...operation.queryParameters.map((q) => `${nullable(csharpType(q.schema, config))} ${camel(q.name)} = null`),
  ];
  const parameters = [...required, ...optional, "CancellationToken cancellationToken = default"];

  const path = operation.path.replace(/\{(\w+)\}/g, (_, name) => `{${camel(name)}}`);
  // Only interpolate when there is something to interpolate: `$"/v1/users"` compiles, but it reads
  // as though a placeholder was lost, and generated code should never look like a mistake.
  const pathLiteral = operation.pathParameters.length > 0 ? `$"${path}"` : `"${path}"`;
  const args = [
    `HttpMethod.${httpMethod(operation.method)}`,
    pathLiteral,
  ];

  if (Object.keys(operation.bodyProperties).length > 0) {
    const fields = Object.keys(operation.bodyProperties)
      .map((name) => `[\"${name}\"] = ${camel(name)}`)
      .join(", ");
    args.push(`body: new Dictionary<string, object?> { ${fields} }`);
  } else if (operation.queryParameters.length > 0) {
    args.push("body: null");
  }

  if (operation.queryParameters.length > 0) {
    const entries = operation.queryParameters
      .map((q) => `[\"${q.name}\"] = ${queryValue(q, config)}`)
      .join(", ");
    args.push(`query: new Dictionary<string, string?> { ${entries} }`);
  }
  args.push("cancellationToken: cancellationToken");

  const notes = [
    operation.summary,
    service.documentGaps?.[operation.operationId] &&
      `Known gap in the OpenAPI document this is generated from: ${service.documentGaps[operation.operationId]}`,
  ].filter(Boolean);
  const doc = notes.length > 0 ? xmlDoc(/** @type {string[]} */ (notes), "    ").join("\n") + "\n" : "";

  const generic = operation.responseSchema === null
    ? "<object>"
    : `<${csharpType(operation.responseSchema, config)}>`;
  const call = `client.SendAsync${generic}(${args.join(", ")})`;

  // A 204 still awaits the send but discards its (absent) body, so the public signature is a bare
  // Task rather than Task<object?> — the caller has nothing to inspect.
  const body = operation.responseSchema === null
    ? `    {\n        await ${call}.ConfigureAwait(false);\n    }`
    : `        ${call};`;

  const signature = operation.responseSchema === null
    ? `    public async ${returnType} ${pascal(methodName(operation))}(${parameters.join(", ")})`
    : `    public ${returnType} ${pascal(methodName(operation))}(${parameters.join(", ")}) =>`;

  return `${doc}${signature}\n${body}`;
}

/** @param {any} parameter @param {any} config */
function queryValue(parameter, config) {
  const name = camel(parameter.name);
  // Query values go on the wire as strings. A string parameter is already one; anything else is
  // formatted invariantly, never with the current culture, or a decimal separator follows the
  // machine's locale onto the wire.
  return csharpType(parameter.schema, config) === "string"
    ? name
    : `${name}?.ToString(System.Globalization.CultureInfo.InvariantCulture)`;
}

/** @param {any} document @param {string} schemaName @param {string} className @param {any} config */
function model(document, schemaName, className, config) {
  const schema = document.components.schemas[schemaName];
  if (!schema) throw new Error(`Schema "${schemaName}" is not in the document.`);

  const required = new Set(schema.required ?? []);
  const fields = Object.entries(schema.properties ?? {}).map(([name, property]) => ({
    name,
    schema: /** @type {SchemaRef} */ (resolveSchema(property)),
  }));

  const properties = fields.map((f) => {
    const type = csharpType(f.schema, config);
    return `    [property: JsonPropertyName("${f.name}")] ${required.has(f.name) ? type : nullable(type)} ${pascal(f.name)}`;
  });

  return [
    ...xmlDoc([schema.description ?? `Wire shape of \`${schemaName}\`.`], ""),
    `public sealed record ${className}(`,
    properties.join(",\n") + ");",
  ].join("\n");
}

/** @param {SchemaRef} schema @param {any} config */
function csharpType(schema, config) {
  if (schema.componentName) {
    if (schema.componentName === "JsonNode") return "IReadOnlyDictionary<string, object?>";
    const mapped = config.modelTypes[schema.componentName] ?? config.generateModels?.[schema.componentName];
    if (!mapped) {
      throw new Error(
        `Schema "${schema.componentName}" has no C# type. Add it to this service's csharp.modelTypes ` +
          `if Praxy.Sdk already models it, or to generateModels to emit a record for it.`,
      );
    }
    return mapped;
  }
  switch (schema.type) {
    case "string":
      return schema.format === "date-time" ? "DateTimeOffset" : "string";
    case "integer":
      return "int";
    case "number":
      return "double";
    case "boolean":
      return "bool";
    case "array":
      return `IReadOnlyList<${csharpType(/** @type {SchemaRef} */ (schema.itemType), config)}>`;
    default:
      return "IReadOnlyDictionary<string, object?>";
  }
}

/**
 * `?` on a type that can take it. A value type needs `Nullable<T>`, and both are written `T?` in
 * modern C# — but only under a nullable context, which Directory.Build.props enables repo-wide.
 *
 * @param {string} type
 */
function nullable(type) {
  return type.endsWith("?") ? type : `${type}?`;
}

/** @param {string} method */
function httpMethod(method) {
  return method === "DELETE" ? "Delete" : method.charAt(0) + method.slice(1).toLowerCase();
}

/** @param {string} name */
function camel(name) {
  return name.length === 0 ? name : name[0].toLowerCase() + name.slice(1);
}

/** @param {string} name */
function pascal(name) {
  return name.length === 0 ? name : name[0].toUpperCase() + name.slice(1);
}
