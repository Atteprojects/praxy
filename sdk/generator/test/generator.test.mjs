// @ts-check
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { parseNamespace } from "../src/spec.mjs";
import { services } from "../src/services.mjs";
import { targets } from "../src/targets/index.mjs";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

/**
 * A miniature document with the shapes that actually matter: a path parameter, a body with one
 * required and one optional property, a query parameter, a 204, a `$ref` response, and .NET's
 * integer-or-string union.
 */
const fixture = () =>
  JSON.parse(`{
    "paths": {
      "/v1/things": {
        "get": {"operationId": "things.list",
          "parameters": [{"name": "limit", "in": "query", "schema": {"type": "integer"}}],
          "responses": {"200": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/ThingListResponse"}}}}}},
        "post": {"operationId": "things.create",
          "requestBody": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/CreateThingRequest"}}}},
          "responses": {"201": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/ThingResponse"}}}}}}
      },
      "/v1/things/{thingId}": {
        "delete": {"operationId": "things.delete",
          "parameters": [{"name": "thingId", "in": "path", "required": true, "schema": {"type": "string"}}],
          "responses": {"204": {"description": "No Content"}}}
      }
    },
    "components": {"schemas": {
      "ThingResponse": {"type": "object", "required": ["id"], "properties": {"id": {"type": "string"}}},
      "ThingListResponse": {"type": "object", "required": ["total", "things"],
        "properties": {"total": {"type": ["integer", "string"], "format": "int32"},
                       "things": {"type": "array", "items": {"$ref": "#/components/schemas/ThingResponse"}}}},
      "CreateThingRequest": {"type": "object", "required": ["name"],
        "properties": {"name": {"type": "string"}, "colour": {"type": "string"}}}
    }}
  }`);

const service = {
  namespace: "things",
  description: ["Things."],
  targets: {
    dart: { className: "ThingsService", modelTypes: { ThingResponse: "Thing" }, generateModels: { ThingListResponse: "ThingList" } },
    csharp: { className: "ThingsService", modelTypes: { ThingResponse: "Thing" }, generateModels: { ThingListResponse: "ThingList" } },
  },
};

const render = (targetName, svc = service, doc = fixture()) =>
  targets[targetName].render(svc, parseNamespace(doc, svc.namespace), doc);

// ---- shared, language-neutral parsing -------------------------------------------------------

test("parses operations in a stable order regardless of document order", () => {
  const operations = parseNamespace(fixture(), "things");
  assert.deepEqual(operations.map((o) => o.operationId), ["things.create", "things.delete", "things.list"]);
});

test("resolves .NET's integer-or-string union to a single numeric type", () => {
  // The document types every int32 as ["integer","string"] though the wire only ever sends a
  // number. Taking the union literally would type every `total` in every SDK as an object.
  assert.match(render("dart"), /final int total;/);
  assert.match(render("csharp"), /int Total/);
});

// ---- per-target emission --------------------------------------------------------------------

for (const [name, expectations] of Object.entries({
  dart: {
    list: /Future<ThingList> list\(\{int\? limit\}\)/,
    delete: /Future<void> delete\(String thingId\) async \{/,
    requiredBody: /required String name/,
    optionalOmitted: /'colour': \?colour/,
    reusesHandWritten: /Future<Thing> create\(/,
  },
  csharp: {
    list: /Task<ThingList\?> List\(int\? limit = null/,
    delete: /public async Task Delete\(string thingId/,
    requiredBody: /string name/,
    optionalOmitted: /\["colour"\] = colour/,
    reusesHandWritten: /Task<Thing\?> Create\(/,
  },
})) {
  test(`${name}: emits one method per operation, named from the operationId`, () => {
    const out = render(name);
    assert.match(out, expectations.list);
    assert.match(out, expectations.delete);
  });

  test(`${name}: a required body property is required and an optional one may be omitted`, () => {
    const out = render(name);
    assert.match(out, expectations.requiredBody);
    assert.match(out, expectations.optionalOmitted);
  });

  test(`${name}: reuses a hand-written model instead of generating a second one`, () => {
    // The "generate the mechanical surface, hand-write what has design in it" split depends on
    // this: each SDK already models these, and a generated duplicate would either shadow the good
    // type or silently diverge from it.
    const out = render(name);
    assert.match(out, expectations.reusesHandWritten);
    assert.doesNotMatch(out, /(class|record) Thing[ ({]/);
    assert.match(out, /ThingList/);
  });

  test(`${name}: an unmapped schema fails loudly rather than guessing a type`, () => {
    const incomplete = { ...service, targets: { ...service.targets, [name]: { ...service.targets[name], modelTypes: {} } } };
    assert.throws(() => render(name, incomplete), /ThingResponse/);
  });
}

// ---- the real document ------------------------------------------------------------------------

test("every committed service matches what the real document produces", () => {
  // The drift gate in miniature: CI regenerates and `git diff --exit-code`s, but this fails inside
  // `node --test` too, where the cause is easier to read.
  const document = JSON.parse(readFileSync(join(repoRoot, "docs/openapi/v1.json"), "utf8"));

  for (const svc of services) {
    for (const [targetName, target] of Object.entries(targets)) {
      if (!svc.targets[targetName]) continue;
      const generated = target.render(svc, parseNamespace(document, svc.namespace), document);
      const committed = readFileSync(join(repoRoot, target.outputPath(svc)), "utf8");

      // Formatting is applied after rendering by each language's own formatter, so compare the
      // parts that carry meaning rather than demanding byte equality with a formatted file.
      const squash = (text) => text.replace(/\s+/g, " ").trim();
      assert.ok(
        squash(committed).includes(squash(generated).slice(0, 200)),
        `${target.outputPath(svc)} is stale — run: node sdk/generator/bin/generate.mjs`,
      );
    }
  }
});

test("the users service exposes the paging the document now describes", () => {
  // Both of these exist only because the API declares the query parameters its handler reads and
  // carries a summary on it. Either regressing would silently shrink every generated SDK.
  const document = JSON.parse(readFileSync(join(repoRoot, "docs/openapi/v1.json"), "utf8"));
  const users = services.find((s) => s.namespace === "users");

  const dartOut = targets.dart.render(users, parseNamespace(document, "users"), document);
  assert.match(dartOut, /list\(\{int\? limit, int\? offset, String\? search\}\)/);
  assert.match(dartOut, /\/\/\/ Lists the project's app users, newest first\./);

  const csharpOut = targets.csharp.render(users, parseNamespace(document, "users"), document);
  assert.match(csharpOut, /List\(int\? limit = null, int\? offset = null, string\? search = null/);
  assert.match(csharpOut, /<summary>Lists the project's app users, newest first\.<\/summary>/);
});
