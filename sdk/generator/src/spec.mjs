// @ts-check
/**
 * Reads `docs/openapi/v1.json` into a small, language-neutral intermediate representation.
 *
 * This half of the generator is deliberately the only half that knows about OpenAPI, and it knows
 * nothing about any target language. Everything language-specific lives in `src/targets/`. That is
 * the whole reason this is one generator rather than one per language: adding a target is a new
 * file in `targets/`, not a new program that has to re-learn this document's quirks.
 */

/**
 * @typedef {object} SchemaRef
 * @property {string|null} componentName Set when this is a `$ref` to `#/components/schemas/<name>`.
 * @property {string} type
 * @property {SchemaRef|null} itemType
 * @property {string|null} format
 */

/**
 * @typedef {object} Parameter
 * @property {string} name
 * @property {SchemaRef} schema
 * @property {boolean} required
 * @property {string|null} description
 */

/**
 * @typedef {object} Operation
 * @property {string} operationId
 * @property {string} method
 * @property {string} path
 * @property {string[]} pathParameters
 * @property {Parameter[]} queryParameters
 * @property {Record<string, SchemaRef>} bodyProperties
 * @property {Set<string>} requiredBodyProperties
 * @property {SchemaRef|null} responseSchema Null for a 204.
 * @property {string|null} summary
 */

const HTTP_METHODS = ["get", "post", "put", "patch", "delete"];

/**
 * The operations in one `operationId` namespace (`users.*`), sorted so output is stable regardless
 * of the order paths happen to appear in the document.
 *
 * @param {any} document
 * @param {string} namespace
 * @returns {Operation[]}
 */
export function parseNamespace(document, namespace) {
  /** @type {Operation[]} */
  const operations = [];

  for (const [path, item] of Object.entries(document.paths)) {
    for (const method of HTTP_METHODS) {
      const op = /** @type {any} */ (item)[method];
      if (!op?.operationId?.startsWith(`${namespace}.`)) continue;

      /** @type {string[]} */
      const pathParameters = [];
      /** @type {Parameter[]} */
      const queryParameters = [];
      for (const parameter of op.parameters ?? []) {
        if (parameter.in === "path") pathParameters.push(parameter.name);
        else if (parameter.in === "query") {
          queryParameters.push({
            name: parameter.name,
            schema: resolveSchema(parameter.schema),
            required: parameter.required === true,
            description: parameter.description ?? null,
          });
        }
      }

      const body = bodySchema(document, op);
      operations.push({
        operationId: op.operationId,
        method: method.toUpperCase(),
        path,
        pathParameters,
        queryParameters,
        bodyProperties: body.properties,
        requiredBodyProperties: body.required,
        responseSchema: responseSchema(op),
        summary: op.summary ?? null,
      });
    }
  }

  operations.sort((a, b) => (a.operationId < b.operationId ? -1 : a.operationId > b.operationId ? 1 : 0));
  return operations;
}

/** The method name a target should use: `users.updateEmail` → `updateEmail`. */
/** @param {Operation} operation */
export function methodName(operation) {
  const dot = operation.operationId.lastIndexOf(".");
  return dot < 0 ? operation.operationId : operation.operationId.slice(dot + 1);
}

/**
 * Request-body properties, flattened rather than modelled as a request object, so a caller writes
 * `create(email: …)` instead of `create(CreateUserRequest(email: …))`. Every hand-written service
 * in every Praxy SDK reads that way, and generated methods should be indistinguishable from them.
 *
 * @param {any} document
 * @param {any} op
 */
function bodySchema(document, op) {
  const schema = op.requestBody?.content?.["application/json"]?.schema;
  if (!schema) return { properties: {}, required: new Set() };

  const resolved = dereference(document, schema);
  /** @type {Record<string, SchemaRef>} */
  const properties = {};
  for (const [name, property] of Object.entries(resolved.properties ?? {})) {
    properties[name] = resolveSchema(property);
  }
  return { properties, required: new Set(resolved.required ?? []) };
}

/** @param {any} op @returns {SchemaRef|null} */
function responseSchema(op) {
  for (const status of ["200", "201"]) {
    const schema = op.responses?.[status]?.content?.["application/json"]?.schema;
    if (schema) return resolveSchema(schema);
  }
  return null;
}

/** @param {any} document @param {any} schema */
function dereference(document, schema) {
  if (!schema.$ref) return schema;
  return document.components.schemas[schema.$ref.split("/").pop()];
}

/**
 * One schema node → a {@link SchemaRef}.
 *
 * The `type` field is where .NET's document needs care: an integer property is emitted as
 * `type: ["integer", "string"]` with an integer `format`, even though the wire only ever carries a
 * number. Taking that union literally would give every `total` in every SDK an `object` type.
 * `openapi-typescript` already resolves the same union to `number` for the console, so this
 * resolves it the same way — one answer about this document, not one per generator.
 *
 * @param {any} schema
 * @returns {SchemaRef}
 */
export function resolveSchema(schema) {
  if (schema?.$ref) {
    return { componentName: schema.$ref.split("/").pop(), type: "object", itemType: null, format: null };
  }

  const raw = schema?.type;
  const types = typeof raw === "string" ? [raw] : Array.isArray(raw) ? raw : ["object"];
  const type = types.find((t) => t !== "null" && t !== "string") ?? types[0];

  return {
    componentName: null,
    type,
    format: schema?.format ?? null,
    itemType: type === "array" && schema?.items ? resolveSchema(schema.items) : null,
  };
}
