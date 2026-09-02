# Geo, Phase 3 (`$distance` + `withinBox`) — report

**Status: complete, with one plan change from the design doc** (see below — the distance model, which
was revised twice: this session moved `orderNear`'s ordering off `<->` to fix a real correctness bug,
and owner review then found a third option that fixes it without losing the index. Final state: sphere
everywhere, `<->` retained for ordering). `dotnet test` green: **435 unit, 265 integration** (real
PostGIS-enabled Postgres via Testcontainers), including 21 new unit test cases and 8 new integration
tests for this phase, plus 3 more from the pre-existing fixes below. Console
`tsc -b && vite build` clean. Flutter (`dart analyze .`, `dart test`) and JS/Next.js SDK
(`typecheck`/`test` on `@praxy/core`, `@praxy/react`, `@praxy/nextjs`) all clean. Owner-tested
end-to-end against the shared canonical local dev instance (see
[Owner-test checklist](#owner-test-checklist)).

## Plan change: the distance model, revised twice

**Final state: sphere everywhere.** `orderNear` orders by `<->` (index-accelerated), `$distance` is
`ST_Distance(col, point, false)`, and `near()` is `ST_DWithin(col, point, radius, false)`. Full
reasoning and measurements: `docs/research/geo-nearby.md`'s "Distance model" section.

How it got there, because the intermediate step is worth not repeating:

**This session's finding (correct, and it stands):** the design doc said to use `ST_Distance`'s default
for `$distance` and `<->` for the sort, and to verify they agree. They do not. `<->` computes
geography's *sphere* distance while `ST_Distance` defaults to the *spheroid*; ordering the nearest 2000
rows by `<->` and checking `ST_Distance` monotonicity across that order inverted **584 of 2000 adjacent
pairs**, up to **~23.5m** apart, ~0.24% max relative difference. Not float noise — a real
bearing-dependent difference between the two models. Mixing them would have violated "a row shown as
nearer must never sort after one shown as farther."

**This session's fix, since superseded:** per the design doc's own contingency, `ORDER BY` was switched
to `ST_Distance` so both sides used one expression. That restored consistency but cost the GiST
index-acceleration: a bare `orderNear` became a `Parallel Seq Scan` (~2.6s over 200k rows vs Phase 2's
~87ms), with the index only still helping when a `near()`/`withinBox()` filter bounded the candidate
set first (~192ms).

**Superseded in owner review, before merge.** The contingency was written without knowing that cost,
and it missed a third option: `<->` is *exactly* `ST_Distance(a, b, false)` — the sphere variant —
agreeing to ~1e-9 m. So the sort can keep `<->` (and its index scan) while `$distance` spells out the
same computation as an explicit sphere `ST_Distance`. Verified against real PostGIS that
sphere-vs-spheroid is not what costs the index — *operator-vs-function* is; even
`ST_Distance(g, p, false)` seq-scans (Index Scan reading 25 rows, cost 8.72, vs Parallel Seq Scan
reading 200,000, cost 1,477,811).

`near()`'s `ST_DWithin` moved to sphere in the same change, since its default is spheroid too and a
radius on a different model than the reported distance contradicts it at the boundary (a point at
sphere-3002.267m / spheroid-2996.797m sits inside a spheroid `near(…,3000)` while displaying as 3002m).

Net cost of the final state: all distances are sphere-model, ~0.1-0.2% (about 6m at 3km) from the true
spheroid — invisible in the UI this field feeds, and it keeps nearest-first index-accelerated. The
integration tests' expected-distance constants moved to the sphere figures accordingly (the spheroid
ones are recorded next to them). `QueryCompilerTests.Near_and_distance_both_pin_the_sphere_model_explicitly`
guards both `false` arguments, since `ST_Distance(a,b)` and `ST_DWithin(a,b,r)` are the natural things
to write and each silently reverts to the spheroid.

## What shipped

**`Praxy.Tables/QueryDsl.cs`**: `"withinBox"` added to `AllMethods`; `ValidateArity` gained
`"withinBox" => count == 4` (minLat, minLng, maxLat, maxLng). Not added to `NoAttributeMethods` — it
takes the geo column as its attribute.

**`Praxy.Tables/QueryCompiler.cs`**:
- `CompiledListQuery` gained a `HasDistance` field, threaded through from `CompileList` to
  `RowsService.BuildRowJson` — mirrors how `SelectedKeys`/`Reversed` already travel for the same reason.
- `SortKeyExpr`'s `orderNear` branch keeps the `<->` KNN operator, and a separate `DistanceExpr`
  compiles the trailing `$distance` select-list column as `ST_Distance({col}, {nearPointExpr}, false)` —
  the explicit sphere variant, numerically identical to `<->` (see the plan-change section above; this
  bullet's first version described the superseded ST_Distance-for-both approach). The `$distance` column
  is appended only when `hasDistance` is true, strictly after `BuildSelectList`'s own columns and never
  in the middle, exactly matching the ordinal-safety rule `RowsService.BuildRowJson` depends on.
  `select(...)` narrowing never suppresses it (same "system field" status as `$id`/`$createdAt`).
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

## Also fixed in this PR (pre-existing, flagged in earlier phases, not Phase 3 scope)

Three defects that earlier phases had flagged-but-not-fixed, cleaned up in owner review before this
merged rather than carried forward again.

**1. The console `retry: 1` error-surfacing landmine** (flagged in the Phase 2 report, hit again by this
phase's antimeridian case). Both sessions suspected it was specific to the browser-automation
environment. It isn't — the root cause is simpler: **a 4xx was being retried at all.** A rejected query
fails identically every time, so the retry only delays the error reaching the UI, and under
react-query's default `networkMode: "online"` that retry can be *paused* rather than run, parking the
query at `status: "pending"` / `fetchStatus: "paused"` so `isError` never flips and the screen renders
its empty state. `console/src/main.tsx`'s shared QueryClient now returns `false` from `retry` for any
`ApiError` with a 4xx code; 5xx and transport failures keep their single retry, since those are the
ones a retry can fix. Verified live in the console: an antimeridian `withinBox` now surfaces
**"'withinBox' can't cross the antimeridian."** in the router's error boundary, immediately.

**2. and 3. Two more derived-identifier budget bugs**, the same class as the `reserveSuffixChars` fix
geo Phase 1 made for `_lng`/`_lat`. That report noted in passing that fulltext indexes "likely" had it
too; they did, and a sweep found a third, wider instance nobody had spotted:

| derived name | suffix chars | worst case | reachable by |
|---|---|---|---|
| geo `_lng`/`_lat` alias | 4 | — | fixed in Phase 1 |
| fulltext `__fts` column | 5 | **68 chars** | a fulltext index keyed >53 chars |
| table `__perms` + its `_action_role_idx` | 23 | **86 chars** | enabling row security on a table keyed >56 chars |

Keys are valid up to `Keys.MaxLength` (64), so both were reachable with ordinary valid input, and both
threw `PhysicalNaming.Quote`'s `InvalidOperationException` ("this is a bug, not user input") — a 500,
not a clean 400. `IndexName` now takes `forFulltext`, and `EntityName` reserves
`PhysicalNaming.RowSecuritySuffixChars` for every table (row security is toggled long after the name is
generated, and the name can never change afterwards). Physical names are stored per-resource in the
catalog, so only newly created tables and indexes are affected — nothing existing is renamed.

Both regression tests assert that the *unreserved* form is genuinely unsafe, so they document the bug
rather than just guarding the fix. A sweep of every `Quote($"...")` and `{PhysicalName}`-concatenation
site confirms these four are the complete set.

**Also in this PR, unrelated to any of the above:** `.github/workflows/ci.yml` now only does each
area's real work when files in that area changed — a docs-only PR no longer spends ~16 minutes
rebuilding the API. Every job still *runs* and gates its steps rather than being skipped, because
`main`'s required checks ("Build and test API", "Build console", "Build and test Flutter SDK") combined
with `enforce_admins` mean a job that never reports leaves a PR unmergeable with no override. The
workflow carries a comment explaining that, and when the tidier job-level `if:` becomes safe.

Verified, with one wrong turn worth recording. The commit that introduced the workflow resolved all
four filters `true` — correct, since every area watches `ci.yml`. A follow-up docs-only *commit* was
then pushed expecting all four to flip `false`; they didn't, and the API job ran its full ~16 minutes
again. The reason is that on `pull_request` events `dorny/paths-filter` diffs against the **base
branch**, not the previous commit, so it sees the whole PR — and this PR genuinely changes `src/`,
`tests/` and `console/`. That is the correct semantic (a PR should test everything it changes), but it
means the skip path cannot be demonstrated from inside a PR that touches those areas at all. It was
confirmed instead on a separate throwaway docs-only PR opened off `main`, where all four filters
resolved `false`, every job ran its "No … changes" step, and all three required contexts still
reported passing — the property that actually matters, since a required check that stops reporting
would leave `main` unmergeable and `enforce_admins` allows no override.

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
1. ~~**The bare-`orderNear`-is-a-full-scan performance cliff**~~ — **resolved in owner review** by
   keeping `<->` for the ordering (see the plan-change section). The index scan is back, so there is no
   cliff. A narrower version remains: pairing `orderNear` with a *non-spatial* filter that excludes most
   rows can still make Postgres walk a long way down the index. Whether that deserves a warning or a
   required-bound mode is a genuine question, just a much smaller one than it was.
2. **The console's `retry: 1` error-surfacing gap** — hit twice now (Phase 2's missing-spatial-index
   case, this phase's antimeridian case), tracked as its own background task already.
