# Session task — Geo, Phase 3 (`$distance` + `withinBox`)

## Why this exists

Phases 1 and 2 shipped the `geo` point column, the `spatial` (GiST) index type, `near(lat, lng,
radiusMeters)` as a radius filter, and `orderNear(lat, lng)` as nearest-first sorting with keyset
pagination. Both are merged and deployed. Read `docs/research/geo-nearby.md` in full before writing any
code — specifically its **"Phase 3 — `$distance` and `withinBox`"** section, which is the complete
design for this phase and settles several questions that earlier drafts left open (units, field name,
whether bare `near()` gets a distance, why `ST_Distance` rather than reusing `<->`). This prompt assumes
you've read it. Work on a new branch off `main`. Read `CLAUDE.md` first.

This phase is two independent additions that share no code. They're batched because both are small and
both are things Appwrite doesn't have — see the design doc's "Competitive position" section for why
that sequencing was chosen over the larger `polygon`/`line` parity work (Phase 4).

## Non-goals — do not build these

- **No `polygon` or `line` column types, no `within`/`intersects`/`contains`/`crosses`/`touches`/
  `overlaps`.** That's Phase 4-5 and it needs its own design pass first. `withinBox` here operates on
  the existing scalar `geo` point column and introduces no new column type.
- **No `$distance` for a bare `near()` filter.** Only `orderNear` produces it. The design doc explains
  why (ambiguity: `near()` can legitimately appear more than once in a query; `orderNear` cannot).
- **No array-valued geo columns.** Still Phase 5.
- **No change to `near()` or `orderNear()`'s existing contracts.** This phase is additive.
- **Don't rename `withinBox` to `within`.** `within` is reserved for Phase 4's polygon containment,
  which is what developers will expect that word to mean.

## Scope

1. **`$distance` system field** (`QueryCompiler.cs`, `RowsService.cs`):
   - Emitted **only** when the query carries an `orderNear`, measured from that same query point, in
     **meters**, as `$distance` alongside `$id`/`$createdAt`/`$updatedAt`/`$permissions`.
   - Computed with `ST_Distance({col}, {the same near-point expression})` — **not** by reusing the
     `<->` value the `ORDER BY` already computes. Rationale is in the design doc. **Verify first**
     that `ST_Distance` and `<->` agree closely enough that the sort order can never contradict the
     displayed numbers (a row shown as nearer must never sort after one shown as farther). If they
     can disagree, use `ST_Distance` for both and say so in the report — that's a plan change worth
     making, not a caveat worth shipping.
   - **Append the expression to the end of `BuildSelectList`'s select list, never the middle.**
     `RowsService.BuildRowJson` walks ordinals positionally from 3, advancing by **2** for geo columns
     and 1 otherwise; a trailing column is the only placement that can't corrupt that walk. Read it
     with a single `reader.GetFieldValue<double>(ordinal)` after the column loop, where `ordinal` is
     already the exact count of consumed columns.
   - Thread a flag through `CompiledListQuery` (it already carries `SelectedKeys`/`Reversed` for the
     same kind of reason) so `BuildRowJson` knows whether to read that trailing ordinal. `Create`/`Get`
     never set it — only `List` can carry an `orderNear`.
   - `select(...)` must **not** suppress it: `BuildSelectList` already emits the three system columns
     unconditionally regardless of `select`, and this is a system field of the same kind.
2. **`withinBox(minLat, minLng, maxLat, maxLng)` filter**:
   - `QueryDsl.cs`: add to `AllMethods`, and `"withinBox" => count == 4` in `ValidateArity`. Takes an
     `attribute`, so it does **not** go in `NoAttributeMethods`.
   - `QueryCompiler.cs`'s `Builder.CompileFilterNode`: a new case compiling to
     `ST_Intersects({colSql}, ST_MakeEnvelope(@minLng, @minLat, @maxLng, @maxLat, 4326)::geography)`.
     Note the **wire order is lat-first but `ST_MakeEnvelope` takes x/y (lng/lat)** — reorder in the
     compiler exactly as `CompileNear` already does for `ST_MakePoint`. Parse the four values as plain
     doubles via the existing shared `RequireNearValue` helper (pass `"withinBox"` as the method name —
     it takes one since Phase 2's review fix).
   - Gate on `column.Type == ColumnTypes.Geo` **and** a declared spatial index, same two checks and the
     same error shapes `CompileNear`/`orderNear` already produce.
   - Reject `minLat >= maxLat` with a clear error. Reject `minLng > maxLng` (an antimeridian-crossing
     box, which `ST_MakeEnvelope` can't express) with a clear, specific error rather than silently
     emitting an inverted envelope that matches nothing — say so in the message, since a caller hitting
     this needs to know it's a real limitation, not a typo.
3. **Coordinate-range validation, applied to `near`, `orderNear` and `withinBox` together**: latitude
   `[-90, 90]`, longitude `[-180, 180]`. `near` doesn't validate this today; adding it to `withinBox`
   alone would leave the three inconsistent, which is worse than either extreme. Purely additive — it
   only rejects input that is already meaningless.
4. **Console**: surface `withinBox` in `FilterPicker` (`RowsPage.tsx`'s `OPERATORS_BY_TYPE` gains a
   `geo` entry) with four numeric inputs, and render `$distance` in the rows grid when present — a
   read-only column, formatted sensibly (metres vs kilometres is your call; state what you chose in the
   report). Note `near()` itself was never wired into `FilterPicker` in Phase 1, so you may need to add
   the `geo` operator entry from scratch rather than extend one — check before assuming.
5. **SDKs**: `$distance` is a new system field on row payloads — thread it through wherever `$id`/
   `$createdAt` are modelled (`console/src/api/types.ts`, the Flutter and Next.js SDK row types). Check
   rather than assume: some of these may model system fields loosely enough to need no change.

## Landmines — read before writing code

- **The select-list ordinal walk is the sharp edge of this phase.** `BuildRowJson` is positional and geo
  columns already consume two ordinals each. Appending `$distance` anywhere but the very end will
  silently shift every column after it — and it'll look correct on a table whose geo column happens to
  be last. Test on a table with a geo column *followed by* other columns.
- **`ST_MakeEnvelope` returns geometry, not geography** — the `::geography` cast is required, and a
  geography envelope's edges follow great circles rather than parallels. Negligible for a map viewport,
  visible across many degrees of latitude. Verify against real coordinates and document what you find.
- **Don't add `&&` alongside `ST_Intersects`.** PostGIS's named predicates use the GiST index
  automatically when one exists; `&&` is a geometry operator, not a geography one, and writing both is
  cargo-culted from geometry-column examples.
- **Two distance computations per row is the intended design, not an oversight** — `<->` for the
  index-accelerated ordering, `ST_Distance` for the displayed value. Don't "optimize" it into one
  without reading the design doc's reasoning first.

## Tests

- Unit (`QueryDslTests`): `withinBox` arity (3 and 5 rejected, 4 parses, missing attribute rejected).
- Unit (`QueryCompilerTests`): `withinBox` on a non-geo column rejected; with no spatial index rejected;
  compiles to `ST_Intersects`/`ST_MakeEnvelope`/`::geography` with the **lng/lat argument order actually
  asserted** (not just "contains ST_MakeEnvelope" — the reordering is the part that breaks silently);
  `minLat >= maxLat` rejected; antimeridian box rejected; out-of-range lat/lng rejected for all three
  geo methods.
- Integration (`GeoEngineTests`): `withinBox` around a known box includes/excludes the right real-world
  landmarks; `withinBox` composes with `orderNear`; `$distance` is present and numerically correct for
  an `orderNear` query (assert against the real ~3217m/~7201m figures Phases 1-2 already verified, not a
  made-up number); `$distance` is **absent** for a bare `near()` query and for a plain unsorted list;
  `$distance` survives `select(...)` narrowing; and — the ordinal landmine — `$distance` is correct on a
  table where the geo column is followed by at least two other columns of different types.

## Done means

- `dotnet test` green (unit + integration, real PostGIS-enabled Postgres).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run**: in the console, on a table with a `geo` column, a spatial index and rows
  at known coordinates — apply a nearest-to sort and confirm the `$distance` column shows plausible,
  correctly-ordered values; add a `withinBox` filter that should include some rows and exclude others
  and confirm it does; confirm an antimeridian-crossing box gives a clean, readable error rather than
  an empty result set.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/geo-nearby-phase-3-report.md`. **Do not write a Phase 4 prompt** — Phase 4
  (`polygon`/`line` + `within`/`intersects`/`contains`) is agreed in scope but deliberately undesigned,
  and needs its own design pass first, the same way Phase 2's design preceded its implementation.
