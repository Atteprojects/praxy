# Geo, Phase 3 (`$distance` + `withinBox`) — report

**Status: complete, with one plan change from the design doc** (see below — `orderNear`'s ordering now
uses `ST_Distance` instead of the GiST-index-accelerated `<->` KNN operator, a real performance
tradeoff, not a caveat). `dotnet test` green: **431 unit, 265 integration** (real PostGIS-enabled
Postgres via Testcontainers), including 20 new unit test cases and 8 new integration tests for this
phase. Console
`tsc -b && vite build` clean. Flutter (`dart analyze .`, `dart test`) and JS/Next.js SDK
(`typecheck`/`test` on `@praxy/core`, `@praxy/react`, `@praxy/nextjs`) all clean. Owner-tested
end-to-end against the shared canonical local dev instance (see
[Owner-test checklist](#owner-test-checklist)).

## Plan change: `orderNear` now orders by `ST_Distance`, not `<->`

The design doc's own instruction: verify first that `<->` (used for the KNN `ORDER BY`) and
`ST_Distance` (used for the returned `$distance`) agree closely enough that "a row shown as nearer
must never sort after one shown as farther"; if they can disagree, use `ST_Distance` for both and
treat it as a plan change, not a caveat. **They disagree, measurably.** Verified against a real
`postgis/postgis:17-3.6-alpine` container (50,000 points spread over ~1°×1°, plus 500 points clustered
within ~50m of a query point, matching the design doc's own dense-cluster concern):

- `<->`'s returned value matches `ST_Distance(a, b, use_spheroid=false)` (the **sphere** distance) —
  confirmed by comparing `<->`'s output against both spheroid and sphere `ST_Distance` variants for the
  same rows; it matches sphere to float precision, spheroid diverges by a consistent, non-trivial margin.
- `ST_Distance`'s **default** (no third argument, what a caller writing `ST_Distance(a, b)` gets) is the
  **spheroid** distance — the accurate, documented one the design doc specifically wanted for the
  displayed value.
- Ordering the nearest 2000 rows by `<->` and checking whether `ST_Distance` is monotonically
  non-decreasing across that same order: **584 of 2000 adjacent pairs inverted**, up to **~23.5m** apart
  in `ST_Distance`, ~0.24% max relative difference. Not floating-point noise — a real algorithmic
  difference (sphere vs. spheroid) that shows up whenever two candidates are close enough that the two
  models' different bearing-dependent error terms flip their relative order.

Per the design doc's own contingency, `orderNear`'s `ORDER BY` (and the cursor subselect/tuple-compare
that reuse the same expression) now use `ST_Distance`, identically to `$distance` — in fact the same
literal SQL expression is reused for both (`QueryCompiler.cs`'s `SortKeyExpr` local function), so the
sort order and the displayed number can never disagree even at the float level, and the "two distance
computations per row" the design doc anticipated collapsed into one.

**The real cost: `orderNear` alone (no `near()`/`withinBox()` filter) loses GiST index-acceleration for
the sort itself.** Verified via `EXPLAIN ANALYZE` against 200,000 seeded rows with a GiST spatial index:

- Bare `orderNear`, no radius filter: **`Parallel Seq Scan`**, ~2.6s (JIT-compilation-inflated; ~700ms
  of actual scan per worker). The GiST index plays no role — Postgres has no way to use it to accelerate
  an `ORDER BY` on an arbitrary function's output, only the specific `<->` KNN operator class gets that
  treatment, and geography has no spheroid-distance KNN operator to substitute.
- `orderNear` combined with `near()`'s radius filter: **`Bitmap Index Scan on ix_places_location`**
  still bounds the candidate set via `ST_DWithin` (200,000 rows → ~2,746 candidates), then sorts just
  that bounded set — ~192ms, close to Phase 2's original `<->`-only figure (~87ms) and nowhere near the
  unbounded case. `withinBox()`'s `ST_Intersects` bounds the same way.

**Guidance worth carrying forward, not implemented here (out of scope for a bug-fix-shaped finding)**: a
bare, unbounded `orderNear` over a large table is now a real performance cliff. Pairing `orderNear` with
`near()` or `withinBox()` keeps it fast; nothing in this phase enforces that pairing or warns about it.
Whether to address this (a size-based warning, a required-radius mode, a hybrid over-fetch-then-rerank
strategy) is a product decision for a future phase, not something to improvise here.

## What shipped

**`Praxy.Tables/QueryDsl.cs`**: `"withinBox"` added to `AllMethods`; `ValidateArity` gained
`"withinBox" => count == 4` (minLat, minLng, maxLat, maxLng). Not added to `NoAttributeMethods` — it
takes the geo column as its attribute.

**`Praxy.Tables/QueryCompiler.cs`**:
- `CompiledListQuery` gained a `HasDistance` field, threaded through from `CompileList` to
  `RowsService.BuildRowJson` — mirrors how `SelectedKeys`/`Reversed` already travel for the same reason.
- `SortKeyExpr`'s `orderNear` branch compiles to `ST_Distance({alias}{col}, {nearPointExpr})` (see the
  plan-change section above). The trailing `$distance` select-list column, appended only when
  `hasDistance` is true, reuses `SortKeyExpr("t.")` verbatim rather than building a second expression —
  appended strictly after `BuildSelectList`'s own columns, never in the middle, exactly matching the
  ordinal-safety rule `RowsService.BuildRowJson` depends on. `select(...)` narrowing never suppresses it
  (same "system field" status as `$id`/`$createdAt`).
- New `CompileWithinBox` (mirrors `CompileNear`'s structure): same column-type and spatial-index gating,
  reorders the wire's lat-first `(minLat, minLng, maxLat, maxLng)` into `ST_MakeEnvelope`'s x/y
  (`minLng, minLat, maxLng, maxLat`) argument order, casts to `::geography`, and compiles to
  `ST_Intersects(col, ST_MakeEnvelope(...)::geography)` — no `&&` alongside it (confirmed via `EXPLAIN`
  that `ST_Intersects` alone uses the GiST index; `&&` is a geometry operator, not a geography one).
  Rejects `minLat >= maxLat` and `minLng > maxLng` (antimeridian) with distinct, actionable messages —
  the antimeridian case names the limitation explicitly rather than silently returning an empty set from
  an inverted envelope.
- New shared `RequireLat`/`RequireLng` helpers (wrapping the existing `RequireNearValue`) add
  `[-90,90]`/`[-180,180]` range validation, applied consistently to `near`, `orderNear`, and `withinBox`
  — previously `near`/`orderNear` validated numeric-ness only, never range; adding it to `withinBox`
  alone would have left the three inconsistent, which the design doc called out as worse than either
  extreme.
- `RequireNearValue` itself (shared by `near`'s `radiusMeters` too) is untouched — still plain
  numeric-only validation, since a radius or a raw KNN value doesn't have a lat/lng range to check.

**`Praxy.Tables/RowsService.cs`**: `BuildRowJson` gained a `hasDistance` parameter (default `false`,
explicit `true` only from `ListAsync`'s call site — `Create`/`Get`/expansion's internal query never carry
`orderNear`, so they never pass it). After the existing per-column loop, `ordinal` is already exactly the
count of consumed columns; when `hasDistance`, one more `reader.GetFieldValue<double>(ordinal)` reads the
trailing `$distance` column straight onto `obj["$distance"]`.

**API DTOs / OpenAPI**: confirmed, not assumed — no changes. `RowDtos.cs`'s `RowListResponse` wraps
`IReadOnlyList<JsonObject>`, and single-row endpoints already `.Produces<JsonObject>()` — a dynamic
schema with no per-field enumeration, so `$distance` flows through with zero DTO/OpenAPI surface change
(`OpenApiDocumentTests` passing unchanged confirms the committed snapshot didn't drift).

**Console** (`console/src/screens/RowsPage.tsx`):
- `OPERATORS_BY_TYPE` gained a `geo` entry — `withinBox` is the *first* geo operator with a
  `FilterPicker` entry at all (confirmed before assuming: `near()` itself still has none, unchanged from
  Phase 1, out of this phase's scope). `FilterPicker`'s arity type widened from `0 | 1 | 2` (the `2`
  already unused) to `0 | 1 | 4`, with a new 4-numeric-input grid (`minLat`/`minLng`/`maxLat`/`maxLng`)
  rendered when the selected operator's arity is 4.
- A `$distance` grid column, shown only when the active sort is `orderNear` (`sort?.direction ===
  "near"`), placed right after `$id` — read-only, formatted via `formatDistance`: whole meters under
  1000m, kilometres to 2 decimal places above that (a judgement call — plausible at both the
  "which corner store" and "which city" ends of a result set; stated here since the prompt left the
  exact formatting to the implementing session).

**SDKs** — `$distance` threaded through wherever `$id`/`$createdAt` are modelled, checked per-SDK rather
than assumed uniform:
- `console/src/api/types.ts`: `Row` interface gains `$distance?: number`.
- `sdk/js/packages/core/src/models.ts`: `RowMeta` interface gains `$distance?: number`. The JS SDK does
  no manual field extraction (`client.ts` is a pure `JSON.parse(...) as T` cast), so this one-line,
  one-file change is the entire fix — the runtime already passes an extra `$distance` key through
  untouched; only the type was missing.
- `sdk/flutter/praxy_core/lib/src/row_codec.dart` + `.../services/tables_service.dart`: Flutter's
  `RowMeta` is a closed Dart record type (not a map), decoded field-by-field in `_decodeRow` — needed a
  new `double? distance` field on the record *and* a corresponding
  `distance: (json[r'$distance'] as num?)?.toDouble()` extraction line, or the value would be silently
  dropped (it's `$`-prefixed, so it's excluded from the loose `data` map too, and un-captured fields
  aren't just ignored — they're genuinely lost). Checked for other `RowMeta` construction sites
  (test mocks, the example app) — none found; `_decodeRow` is the only place one is built.

## Tests

`tests/Praxy.Tests.Unit/QueryDslTests.cs`: `withinBox` arity (3 and 5 rejected, 4 parses, missing
attribute rejected).

`tests/Praxy.Tests.Unit/QueryCompilerTests.cs` (20 new cases): `orderNear` now asserted to compile to
`ST_Distance`, not `<->` (existing tests updated, not just added to, since the plan change invalidated
their prior assertions); `HasDistance` true only for `orderNear`, false for a bare `near()` filter and
for a plain unsorted list; `$distance` survives `select(...)` narrowing; `withinBox` rejected on a
non-geo column and with no spatial index; **the lng/lat reordering asserted against the actual bound
parameter values in call order** (`[minLng, minLat, maxLng, maxLat]`), not just "the SQL contains
`ST_MakeEnvelope`" — the reordering is exactly the part that breaks silently if swapped; `minLat >=
maxLat` and antimeridian-crossing both rejected; `withinBox` composes with `orderNear`; out-of-range
lat/lng rejected for all three of `near`/`orderNear`/`withinBox` (8 theory cases).

`tests/Praxy.Tests.Integration/GeoEngineTests.cs` (8 new tests, real PostGIS-enabled Postgres, exact
`ST_Distance` figures measured directly against a real container rather than reusing Phase 1's rounded
"~3217m"/"~7201m" comments — 3217.59194446m and 7201.19456517m):
`WithinBox_includes_and_excludes_the_right_real_world_landmarks`, `WithinBox_composes_with_orderNear`,
`WithinBox_without_a_spatial_index_is_rejected_with_a_clear_error`,
`WithinBox_crossing_the_antimeridian_is_a_clean_error_not_an_empty_result`,
`Distance_is_present_and_numerically_correct_for_an_orderNear_query` (asserted to 2 decimal places
against the measured figures), `Distance_is_absent_for_a_bare_near_query_and_for_a_plain_unsorted_list`,
`Distance_survives_select_narrowing`, and — the ordinal landmine, the one the design doc most wanted
covered — `Distance_is_correct_when_the_geo_column_is_followed_by_other_columns`: a table with
`name`(string) → `location`(geo) → `views`(integer) → `featured`(boolean), confirming every column after
`location` lands in its correct property and `$distance` is still correct, unlike every other test in
this file (which happens to put `location` last, the one placement that can't expose an ordinal bug).

## Owner-test checklist

Done by me this session, against the shared canonical local dev instance (`owner@test.local`), reusing
Phase 2's `Places` table (`name`, `location` geo, spatial index, City Hall/Golden Gate Bridge/Ferry
Building seed rows).

- Applied "Sort nearest to…" centered on City Hall: a new **`$DISTANCE`** column appeared right after
  `$id`, showing City Hall **0 m**, Ferry Building **3.22 km**, Golden Gate Bridge **7.20 km** — correct
  values (matching the measured 3217.59m/7201.19m figures) in the correct nearest-to-farthest order.
- Added a `withinBox` filter (`37.77, -122.45, 37.80, -122.38`) via the new `FilterPicker` entry:
  correctly included City Hall and Ferry Building, excluded Golden Gate Bridge — confirmed via the grid
  and the filter chip showing `location within box 37.77, -122.45, 37.8, -122.38`.
- Attempted an antimeridian-crossing box (`minLng=170, maxLng=-170`): **found the pre-existing,
  already-flagged console landmine from the Phase 2 report recurring** — the failed request left the
  grid showing "No rows match your filters" instead of surfacing the router's error boundary, the same
  `retry: 1`/`useInfiniteQuery`-stuck-in-`paused` interaction documented in
  `docs/handoff/geo-nearby-phase-2-report.md` and already spun off as its own background investigation
  task (not re-diagnosed here). Confirmed the backend and the error-throw path are both correct by
  calling the same endpoint directly: a clean 400,
  **`'withinBox' can't cross the antimeridian.`**, with the full actionable field message. This is not a
  new defect from this phase — it's the same cross-cutting console gap, now hit a second time because
  `withinBox` is (after `orderNear`) the second console-triggerable action that can produce a
  compiler-rejected list query.
- `git status` clean, `.claude/launch.json` reverted to its committed state (the same session-local port
  workaround as Phase 2, needed only because this environment shares one working directory across
  sessions).

## Next

Per this phase's own prompt: **no `docs/handoff/geo-nearby-phase-4-prompt.md` was written.** Phase 4
(`polygon`/`line` column types, `within`/`intersects`/`contains`) is agreed in scope but deliberately
undesigned — it needs its own design pass first, the same way Phase 2's and Phase 3's designs each
preceded their own implementation.

Two things worth the owner's attention before or during that design pass, neither addressed here:
1. **The bare-`orderNear`-is-a-full-scan performance cliff** (see the plan-change section above) — worth
   deciding whether Phase 4 or a dedicated follow-up should add guidance, a warning, or a different
   strategy for large tables.
2. **The console's `retry: 1` error-surfacing gap** — hit twice now (Phase 2's missing-spatial-index
   case, this phase's antimeridian case), tracked as its own background task already.
