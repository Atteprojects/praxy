/**
 * Branded id types, closing the bug class the console/API contract initiative exists for.
 *
 * Organizations Phase 2 shipped a bug where `OrganizationMember.userId` (`Ids.Wire`-encoded, 32 lowercase
 * hex) was compared against `ConsoleAccount.id` (a plain, dashed `Guid`) to answer "is this row me?" —
 * a comparison that could never match, silently. Both sides were `string`, correctly, so nothing in the
 * type system objected. Shape-correctness alone can't catch this: what was wrong was the *encoding*.
 *
 * `WireId` and `GuidId` are nominal wrappers around `string` — real strings at runtime (identical to what
 * `types.ts` modelled before this file existed), but mutually incompatible at the type level via a unique
 * symbol brand neither structurally satisfies by accident. Assigning one where the other is expected, or
 * comparing them, is now a compile error instead of a silent false comparison.
 *
 * `docs/api-reference.md`'s generated document distinguishes exactly one of these two encodings on its
 * own: a plain C# `Guid` property serializes with `format: "uuid"`, which `console/scripts/generate-api-types.mjs`
 * maps straight to `GuidId` — no guessing involved. Every other id in the API is a `string` populated via
 * `Ids.Wire(...)`, and OpenAPI/JSON Schema has no way to say "this string happens to hold 32 hex chars" —
 * that's not a property the schema tracks. So `WireId` is applied by the generator from a curated,
 * hand-reviewed list of property paths (see that script's `WIRE_ID_PATHS`), not inferred from the schema.
 * That list is a judgment call recorded in code, not a fact machine-verified from the document — the
 * honest tradeoff of branding an encoding the wire format itself doesn't expose.
 */

declare const wireIdBrand: unique symbol;

/** A Praxy-generated resource id: 32 lowercase hex characters, no dashes (`Ids.Wire` on the server). */
export type WireId = string & { readonly [wireIdBrand]: true };

declare const guidIdBrand: unique symbol;

/**
 * A plain, dashed `Guid` rendered by `Guid.ToString()` / System.Text.Json's default `Guid` converter.
 * Today exactly two properties use this encoding — `ConsoleAccount.id` and
 * `OrganizationMemberResponse.userId` — deliberately not unified with `WireId` (CLAUDE.md).
 */
export type GuidId = string & { readonly [guidIdBrand]: true };

/** Brands a raw string as a `WireId` at a trust boundary (e.g. a route param) — no runtime check. */
export function wireId(value: string): WireId {
  return value as WireId;
}

/** Brands a raw string as a `GuidId` at a trust boundary (e.g. a route param) — no runtime check. */
export function guidId(value: string): GuidId {
  return value as GuidId;
}
