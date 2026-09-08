#!/usr/bin/env node
// Regenerates console/src/api/generated/schema.ts from docs/openapi/v1.json.
//
// Run via `npm run generate:api --prefix console`. `npm run build` never runs this itself — the
// generated file is committed, the same way docs/openapi/v1.json itself is, so the console keeps
// building from a clean checkout with no codegen step, no database, and no network.
//
// `npm run check:api-types` reruns this (overwriting the working-tree copy) and then
// `git diff --exit-code`s it — that's the CI gate (mirrors OpenApiDocumentTests' snapshot check on
// the .NET side). Safe to run locally too: a clean tree means nothing to diff.
//
// See docs/research/console-api-contract.md and docs/handoff/console-api-contract-report.md for why
// this exists and what it closes.

import { fileURLToPath } from "node:url";
import path from "node:path";
import fs from "node:fs";
import ts from "typescript";
import openapiTS, { astToString } from "openapi-typescript";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, "../..");
const schemaPath = path.join(repoRoot, "docs/openapi/v1.json");
const outPath = path.join(scriptDir, "../src/api/generated/schema.ts");

/**
 * Every property below is a `string` with no `format` — indistinguishable, in JSON Schema, from a
 * `Praxy.Core.Ids.Wire`-encoded resource id. Most of them *are* one, so that's the default (see
 * `looksLikeWireId` below); these are the exceptions, found by reading every match against
 * `src/Praxy.Api`'s DTOs by hand. There is no schema-level signal for the distinction — OpenAPI has
 * no "this string happens to be 32 hex chars" format — so this list is a curated judgment call, not
 * something derived mechanically from the document.
 *
 * The bar for an entry is "not a Praxy resource id at all," not "not 32 hex chars": a project, site,
 * function or messaging topic may carry an operator-chosen custom id (`Ids.IsValidCustomId` — 1-36
 * lowercase alphanumerics and hyphens) rather than a generated one, and those are still `WireId`,
 * because what the brand separates is the wire family from a dashed `Guid` — see `../src/api/ids.ts`.
 *
 * **This heuristic fails open.** A newly-added `*Id`-named string property is branded `WireId`
 * automatically; if it isn't a Praxy id, nothing catches that but a human adding it here.
 */
const WIRE_ID_EXCLUDE = new Set([
  // A third-party Google OAuth client id — nothing Ids.Wire ever touches.
  "#/components/schemas/AuthSettingsResponse/googleClientId",
  "#/components/schemas/UpdateAuthSettingsRequest/googleClientId",
  // FunctionRuntimeResponse.id is the runtime's fixed key ("dart"/"node"), built from
  // FunctionRuntimes.All (a static, in-memory list) — never Ids.Wire. The one other spot in the
  // whole API that builds a *Response via .Select over something other than a DB entity
  // (IdentityResponse, checked by hand) does use Ids.Wire, so this is confirmed the only exception.
  "#/components/schemas/FunctionRuntimeResponse/id",
]);

function isPlainString(schemaObject) {
  return schemaObject.type === "string" && schemaObject.format === undefined;
}

function looksLikeWireId(propertyName) {
  return propertyName === "id" || /Id$/.test(propertyName) || /Ids$/.test(propertyName);
}

/**
 * .NET 10's OpenAPI generation types every integer property (int32 and int64 alike — all 96 of
 * them in this document, no exceptions) as `["integer","string"]` with a digit-only `pattern`,
 * modelling that a numeric string would also be an acceptable representation. Nothing in this API
 * sets `JsonNumberHandling.AllowReadingFromString` (checked: `Program.cs` configures no
 * `NumberHandling` at all), so the wire behavior is unconditionally a bare JSON number — the schema
 * is permissive relative to reality here the same way it was for nullability, just not in a way
 * that masks a bug (a real `number` always satisfies `number | string`, so nothing was silently
 * broken by trusting it). Collapsing it to plain `number` matches what every consumer actually
 * receives and what `types.ts` already modelled by hand.
 */
function isIntegerOrStringUnion(schemaObject) {
  return (
    Array.isArray(schemaObject.type) &&
    schemaObject.type.length === 2 &&
    schemaObject.type.includes("integer") &&
    schemaObject.type.includes("string")
  );
}

const nodes = await openapiTS(new URL(`file://${schemaPath}`), {
  transform(schemaObject, options) {
    const nodePath = options.path ?? "";

    // Row column data: an arbitrary, dynamic key->JSON-value bag (System.Text.Json.Nodes.JsonObject
    // on the server). A null value here is a real, present null — JsonNode content is copied
    // verbatim and bypasses Program.cs's WhenWritingNull, unlike every other property in this
    // document — so `unknown` per value, not the generator's default `Record<string, never>` (which
    // would make every column access a type error). See ids.ts's header for the encoding side of
    // this same "don't infer past what the schema actually says" principle.
    if (nodePath === "#/components/schemas/JsonObject") {
      return ts.factory.createTypeReferenceNode("Record", [
        ts.factory.createKeywordTypeNode(ts.SyntaxKind.StringKeyword),
        ts.factory.createKeywordTypeNode(ts.SyntaxKind.UnknownKeyword),
      ]);
    }

    // A plain C# `Guid` property serializes with `format: "uuid"` — today `ConsoleAccount.id` and
    // `OrganizationMemberResponse.userId`, and nothing else. Unlike WireId below, this is a real
    // schema signal, not a naming guess.
    if (schemaObject.format === "uuid") {
      return ts.factory.createTypeReferenceNode("GuidId");
    }

    if (isIntegerOrStringUnion(schemaObject)) {
      return ts.factory.createKeywordTypeNode(ts.SyntaxKind.NumberKeyword);
    }

    if (!nodePath.startsWith("#/components/schemas/")) return undefined;
    if (!isPlainString(schemaObject)) return undefined;
    if (WIRE_ID_EXCLUDE.has(nodePath)) return undefined;

    const propertyName = nodePath.split("/").pop();
    if (looksLikeWireId(propertyName)) {
      return ts.factory.createTypeReferenceNode("WireId");
    }

    return undefined;
  },
});

const header = `/**
 * AUTO-GENERATED by console/scripts/generate-api-types.mjs from docs/openapi/v1.json.
 * Do not hand-edit — run \`npm run generate:api\` to regenerate, and \`npm run check:api-types\` to
 * verify this file is current (also run in CI). See console/src/api/ids.ts for WireId/GuidId.
 */
import type { GuidId, WireId } from "../ids.ts";

`;

const body = astToString(nodes).replace(
  /^\/\*\*\n \* This file was auto-generated by openapi-typescript\.\n \* Do not make direct changes to the file\.\n \*\/\n/,
  "",
);

fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, header + body);
console.log(`Generated ${path.relative(repoRoot, outPath)}`);
