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
 * maps straight to `GuidId` — no guessing involved. The other encoding has no such signal: OpenAPI/JSON
 * Schema has no way to say "this string happens to hold 32 hex chars." So `WireId` is applied by a
 * *naming* heuristic instead — any `format`-less string property named `id`, or ending in `Id`/`Ids` —
 * minus a hand-reviewed deny-list of the few such names that aren't Praxy ids at all (that script's
 * `WIRE_ID_EXCLUDE`). Note the direction: **the default is to brand**, so a newly-added `*Id`-named
 * property that isn't a Praxy id is branded wrongly until someone adds it to that list. The heuristic
 * fails open, not safe — the honest cost of branding an encoding the wire format itself doesn't expose.
 */

declare const wireIdBrand: unique symbol;

/**
 * A Praxy resource id as it appears on the wire. Two forms, one family: 32 lowercase hex characters
 * with no dashes (`Ids.Wire` on the server) for a generated id, or — for the resources that let the
 * operator choose one — a custom id of 1-36 lowercase alphanumerics and hyphens (`Ids.IsValidCustomId`:
 * projects, sites, functions, messaging topics). What both forms have in common, and what this brand
 * exists to enforce, is that neither is ever a dashed `Guid`.
 */
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
