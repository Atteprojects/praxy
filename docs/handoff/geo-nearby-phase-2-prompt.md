# Session task — Geo columns and `near` queries, Phase 2 (distance-sorting, `orderNear`)

## Why this exists

Phase 1 shipped the `geo` column type and `near(lat, lng, radiusMeters)` as a pure radius filter —
deliberately with no automatic nearest-first ordering (see `docs/handoff/geo-nearby-phase-1-report.md`
and the "Non-goals" section of its own prompt). The owner asked for this next slice explicitly:
distance-sorting / nearest-first. Read `docs/research/geo-nearby.md` in full before writing any code —
specifically its **"Phase 2 — distance-sorting (`orderNear`)"** section, which is the complete design
for this phase: the DSL, the exact `QueryCompiler.CompileList` generalization (with line-number
references into the current code), the PostGIS KNN operator this compiles to, and — importantly — a
correction to this doc's own earlier, more cautious framing: **full keyset-cursor pagination is in
scope for this phase**, not deferred to a later one. This prompt assumes you've read that section and
doesn't re-derive it. Work on a new branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **No `orderNearDesc` / farthest-first.** `orderNear` is nearest-first only, by design — see the doc's
  reasoning. Don't add a second variant.
- **No returned `$distance` value.** `orderNear` changes ordering, not the row payload shape. Adding a
  distance field to results is a natural next idea but a distinct feature with its own design questions
  (what units? what key name? does it apply to `near()` alone too?) — explicitly deferred, not part of
  this phase.
- **No array-valued geo columns, no `line`/`polygon` types, no `within`/bounding-box operators.** Still
  out of scope, unchanged from Phase 1.
- **No change to `near()`'s existing contract.** It stays a pure radius filter with no implicit sort.
  `orderNear` composes alongside it when a caller sends both, but neither implies the other.
- **Don't force `orderNear` to require a co-occurring `near()`.** It must work standalone (unbounded
  "K nearest" queries) — this is explicit in the design, not an oversight to "fix."

## Scope

1. **`QueryDsl.cs`**: add `"orderNear"` to `AllMethods`; add an arity rule to `ValidateArity`:
   `"orderNear" => count == 2 // lat, lng`. It takes an `attribute` (the geo column key), so it must
   **not** be added to `NoAttributeMethods`.
2. **`QueryCompiler.CompileList`** (`src/Praxy.Tables/QueryCompiler.cs`): generalize the sort-key
   handling exactly as the design doc lays out —
   - Extend the order-method arm of the loop's `switch` (currently `case "orderAsc" or "orderDesc":`)
     to also match `"orderNear"`. On `orderNear`, resolve the attribute to a `ColumnDef` and validate
     it's `ColumnTypes.Geo` (mirror `CompileNear`'s own `column.Type != ColumnTypes.Geo` check — same
     error shape) and that it has a declared spatial index (mirror `CompileNear`'s
     `entry.SpatialIndexFor(column.Key)` check — same error shape, "create a spatial index before using
     `orderNear`"). Parse the two values as plain doubles the same way `CompileNear`'s
     `RequireNearValue` already does for `near`'s lat/lng/radius — reuse that helper rather than
     duplicating it if its accessibility allows, or lift it to somewhere both call sites can share.
   - "First order method wins" stays a single rule across all three methods
     (`orderAsc`/`orderDesc`/`orderNear`), unchanged.
   - After `select = new Builder(entry)` is constructed, when the winning sort is `orderNear`, call
     `select.AddParam(lng)` and `select.AddParam(lat)` **once each** and build
     `ST_MakePoint({lngParam}, {latParam})::geography` from the two returned `"@pN"` strings.
   - Replace the three current `sortColQuoted`/`t.{sortColQuoted}` interpolation sites (the cursor
     subselect, the cursor tuple-compare, and the final `ORDER BY`) with a single small helper that
     returns either the existing quoted-column form or `{quoted geo column} <-> {near point expr}`,
     parameterized by whether the alias needed is `""` (subselect) or `"t."` (the other two) — same
     split as today.
   - The `count` query Builder (used only when `includeTotal`) needs no changes — it has no `ORDER BY`
     or cursor, so never touches the near-point params.
3. **Console** (`console/src/api/rows.ts` + `console/src/screens/RowsPage.tsx`): `SortState` is
   currently `{ attribute: string; direction: "asc" | "desc" }`. Extend it to also represent a
   near-point sort — e.g. a discriminated union adding
   `{ attribute: string; direction: "near"; lat: number; lng: number }` — and extend
   `serializeQueries` to emit `{"method":"orderNear","attribute":...,"values":[lat,lng]}` for that case
   instead of `orderAsc`/`orderDesc`. Add a UI entry point on a `geo` column to set this sort: the
   existing per-column header click-to-toggle-asc/desc affordance doesn't fit (a near-point sort needs
   two numeric inputs, not a toggle), so this needs a small dedicated control — e.g. a "Sort nearest
   to…" action on a `geo` column's header opening a tiny popover with lat/lng inputs. Exact shape (icon
   button + popover vs. inline form, etc.) is your call — state what you built and why in the report,
   same latitude Phase 1 had for its Flutter codegen shape. Must be reachable and usable by the owner's
   click-test, including clearing it back to no sort / a different sort.
4. **API DTOs / OpenAPI**: if `orderNear` needs any wire-shape documentation beyond what the generic
   query-method mechanism already covers (check `docs/api-reference.md` and the OpenAPI generation
   path), thread it through. Likely minimal since `queries[]` is already a free-form string array on
   the wire — confirm rather than assume.

## Landmines — read before writing code

- **`AddParam` must be called exactly once each for `lat`/`lng` when the sort is `orderNear`, then the
  returned `"@pN"` strings reused verbatim everywhere the near-point expression is needed.** Do not call
  `AddParam` again at each of the three sites — that would add duplicate parameters with different
  names for the same logical value, which still works but silently bloats the parameter list and breaks
  the "one query point per request" mental model the design doc lays out. Reuse the name.
- **The cursor subselect is unaliased, the tuple-compare and `ORDER BY` are `t.`-aliased.** Get this
  backwards and the SQL either fails to bind (`t` not in scope in the subselect) or silently means
  something else. Check exactly how `sortColQuoted` vs `t.{sortColQuoted}` are used today before
  changing either.
- **Verify the query plan, don't assume it.** Run `EXPLAIN ANALYZE` on a seeded table against both a
  plain `orderNear` query and one combined with a keyset cursor. If the GiST KNN index-assisted plan
  doesn't survive the keyset `WHERE` clause, that's not a bug to chase down in this phase (results are
  still correct) — just note it plainly in the report so a future phase knows the cursor path's
  performance characteristics, and confirm plain `offset` pagination still works as the fallback.
- **`orderNear`'s column-type and spatial-index checks must produce the same clear, actionable error
  shape `near()` already does** — don't let a missing index or wrong column type surface as a raw
  Postgres error or a generic 500.
- **Don't touch `near()`'s own compilation at all.** This phase is additive; if you find yourself
  editing `CompileNear` for anything other than extracting a shared helper (like `RequireNearValue`),
  stop and reconsider.

## Tests

`tests/Praxy.Tests.Unit/` and `tests/Praxy.Tests.Integration/` (real PostGIS-enabled Postgres via
Testcontainers, already wired since Phase 1):

- Unit: `QueryDsl` arity validation for `orderNear` (exactly 2 values, not 1 or 3); a case rejecting
  `orderNear` on a non-`geo` column; a case rejecting `orderNear` on a `geo` column with no spatial
  index.
- Integration: create several rows at known, real-world coordinates with known relative distances;
  `orderNear` from a known point returns them in the correct nearest-to-farthest order; combine
  `orderNear` with `near()`'s radius filter and confirm both apply (bounded *and* sorted); page through
  `orderNear` results past a keyset cursor (`cursorAfter`) across multiple pages and confirm no
  duplicate or skipped rows, same discipline existing cursor-pagination tests already use for
  column-based sorts; confirm `orderNear` composes correctly with `orderAsc`/`orderDesc` sent in the
  same request (first order method wins — whichever the client actually put first).

## Done means

- `dotnet test` green (unit + integration, real PostGIS-enabled Postgres).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run**: in the console, on a table with a `geo` column and existing rows at
  known coordinates plus a spatial index (from Phase 1's setup), apply a "sort nearest to" a chosen
  point and confirm the rows reorder correctly; page past the first page and confirm no
  duplicates/gaps; clear the sort and confirm it reverts cleanly; attempt it against a `geo` column
  with no spatial index and confirm a clean, actionable error.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/geo-nearby-phase-2-report.md`, including the `EXPLAIN ANALYZE` finding on
  index-plan survival under keyset pagination. Whether a Phase 3 is warranted (a `$distance` return
  value, array-valued geo, more geo types) is the owner's call, not something to prompt for
  automatically.
