# Geo columns and `near` queries, Phase 2 (distance-sorting, `orderNear`) — report

**Status: complete.** Every item in `docs/handoff/geo-nearby-phase-2-prompt.md`'s scope shipped, including
full keyset-cursor pagination (the prompt's own correction to the research doc's earlier, more cautious
framing). `dotnet test` green: **409 unit, 257 integration** (real PostGIS-enabled Postgres via
Testcontainers), including 15 new unit test cases and 7 new integration tests for this feature. Console
`tsc -b && vite build` clean. Owner-tested end-to-end against the shared canonical local dev instance (see
[Owner-test checklist](#owner-test-checklist)).

Two defects found in the owner's pre-merge review were fixed on this branch before it merged — see
[Fixed in review, before merge](#fixed-in-review-before-merge). The unit count above includes their
regression coverage.

## What shipped

**`Praxy.Tables/QueryDsl.cs`**: `"orderNear"` added to `AllMethods`; `ValidateArity` gained
`"orderNear" => count == 2` (lat, lng). Not added to `NoAttributeMethods` — it takes the geo column key as
its attribute, same as `orderAsc`/`orderDesc`.

**`Praxy.Tables/QueryCompiler.cs`** — the generalization the design doc laid out, applied exactly as
specified:
- `CompileList`'s order-method switch arm now matches `"orderAsc" or "orderDesc" or "orderNear"`. On
  `orderNear` it resolves the attribute to a `ColumnDef`, validates `column.Type == ColumnTypes.Geo` and a
  declared, available spatial index via `entry.SpatialIndexFor` — same error shape `near()`'s own
  `CompileNear` already produces (`general_query_invalid`, naming the missing index) — then parses the two
  values as plain doubles and stashes them as `sortNearPoint`. "First order method wins" is unchanged, one
  rule across all three methods.
- `RequireNearValue` (previously private to `Builder`, used only by `CompileNear`) moved to a shared
  private static helper on `QueryCompiler` itself — both `CompileNear` and the new `orderNear` handling in
  `CompileList` call it, no duplication. (Nested-class/enclosing-class private access in C# made this a
  clean lift, not a visibility change.)
- Immediately after `select = new Builder(entry)`, `AddParam` is called **exactly once each** for `lng`
  then `lat` when the sort is `orderNear`, building
  `nearPointExpr = "ST_MakePoint({lngParam}, {latParam})::geography"` from the two returned `"@pN"`
  strings.
- The three `sortColQuoted`/`t.{sortColQuoted}` interpolation sites (cursor subselect, cursor
  tuple-compare, final `ORDER BY`) were replaced by one `SortKeyExpr(string alias)` local function: for a
  plain column sort it's bit-for-bit what today's code already emits; for `orderNear` it emits
  `"{alias}{quoted geo column} <-> {nearPointExpr}"`. `alias` is `""` in the unaliased subselect and `"t."`
  in the two aliased sites, matching the existing split exactly. The near-point params are reused verbatim
  across all three sites (never re-added), so keyset pagination "just works" the same way column-based
  cursors already do.
- The `count` query (used only for `includeTotal`) needed no changes — confirmed, not assumed: it has no
  `ORDER BY` or cursor, so `orderNear`'s params never touch its independent `Builder`.
- `near()`'s own compilation (`CompileNear`) was **not touched** beyond the `RequireNearValue` extraction.

**Console** (`console/src/api/rows.ts`, `console/src/screens/RowsPage.tsx`):
- `SortState` became a discriminated union: `{attribute, direction: "asc"|"desc"}` (unchanged) or
  `{attribute, direction: "near", lat, lng}` (new). `serializeQueries` emits
  `{"method":"orderNear","attribute":...,"values":[lat,lng]}` for the near case.
- A `geo` column's header no longer uses the plain toggle-asc/desc button — a near-point sort needs two
  numeric inputs, not a toggle, so it didn't fit that affordance. New `GeoSortHeader` component: clicking
  the column key opens a small popover with `lat`/`lng` number inputs and Cancel/Apply; an active near sort
  shows a 📍 indicator plus a dedicated "✕" clear button next to the header. Reusable `sortIndicator`
  helper replaced the inline asc/desc-arrow ternary at both call sites ($id column and regular columns) so
  it correctly renders nothing for a `"near"` sort on a differently-keyed column (structurally can't
  collide in practice, but kept type-correct rather than assuming). Reachable and clearable, confirmed in
  the owner-test below. (The panel itself is a portal — see
  [Fixed in review, before merge](#fixed-in-review-before-merge) for why the original in-grid popover had
  to change.)

**API DTOs / OpenAPI**: confirmed, not assumed — no changes needed. `queries[]` is already a free-form
string array on the wire (`OpenApiDocumentTests` passing unchanged confirms the committed snapshot didn't
drift), and a repo-wide grep found no other hardcoded list of query method names outside
`QueryDsl.AllMethods` and this same console file.

## EXPLAIN ANALYZE finding: the KNN index-assisted plan survives the keyset cursor

The prompt's open question, checked empirically rather than assumed: **yes, the GiST KNN index-assisted
scan (`Index Scan using ix_..._location ... Order By: (location <-> ...)`) survives combination with the
keyset `WHERE` clause.** Verified against a throwaway `postgis/postgis:17-3.6-alpine` container (matching
the pinned tag) seeded with 200,000 random points and a GiST spatial index:

- Plain `orderNear ... LIMIT 25`: `Index Scan using ix_places_location`, `Order By: (location <-> ...)`,
  ~87ms.
- The same query combined with a keyset cursor (`(location <-> point, _id) > (subselect, cursorId)`):
  **still** `Index Scan using ix_places_location` with the identical `Order By`; the cursor tuple-compare
  becomes a post-scan `Filter` (rows already fetched via the index-assisted KNN order, then filtered past
  the cursor) rather than blocking the index-assisted plan — ~76ms, comparable cost.

This is better news than the design doc's cautious framing anticipated — no fallback-to-offset caveat is
needed for `orderNear` + `cursorAfter`/`cursorBefore` together; both paths use the index. Plain `offset`
pagination remains available unconditionally regardless (unchanged, pre-existing).

> **Resolved 2026-09-02** (in the Phase 3 PR, alongside its own review fixes). The root cause was
> simpler than the diagnosis below and *not* harness-specific: a 4xx was being retried at all.
> The shared QueryClient now refuses to retry 4xx — those fail identically every time, so the
> retry only delayed the message, and under `networkMode: "online"` it could be paused instead
> of run, which is what parked the query at `pending`/`paused`. 5xx and transport errors keep
> their retry. Verified live: the antimeridian case now surfaces its real message.

## Landmine found: console rows-list error surfacing can get stuck behind `retry: 1` in a
degraded-online environment

Not part of this phase's scope to fix — flagged here and as a follow-up task, not silently patched.
While driving the owner-test in the Claude Browser pane, a fresh `orderNear` request against a column
with no spatial index (a genuine 400 from the compiler, confirmed correct via direct `fetch`) left
`RowsPage`'s `useInfiniteQuery` stuck indefinitely at `status: "pending"`, `fetchStatus: "paused"` —
`rows.isError` never flipped true, so the page's `if (rows.isError) throw rows.error;` never fired, and the
UI silently showed the generic "No rows yet." empty state instead of the router's error boundary.

Isolated with a temporary `retry: 0` diagnostic (reverted before finishing this phase — `main.tsx`'s
`git diff` is empty): with retries disabled, the exact same request surfaced the router's error boundary
immediately with the correct message, **`'location' has no spatial index.`** — proving both the backend
error message and the frontend `isError`-throw mechanism are correct. The stuck-forever "paused" state is
specifically `@tanstack/react-query`'s retry-pause-on-offline mechanism (`networkMode: "online"`, the
default) interacting with this automation pane's online/offline event semantics — `navigator.onLine`
reported `true` throughout, and dispatching a synthetic `window` `"online"` event only produced another
retry-then-pause cycle rather than resolving to `"error"`. This is plausibly specific to the sandboxed
browser tool's online-detection rather than a defect a real user's browser would hit (a real browser's
`onlineManager` state should never disagree with reality on a normal page load), but it's a real,
cross-cutting behavior of the shared `retry: 1` QueryClient default (`console/src/main.tsx`) that predates
this phase and applies to *any* rows-list query error, not just `orderNear` — no prior console feature
happened to trigger a compiler-rejected rows-list query before this phase's UI existed (the `near()` filter
itself was never wired into `FilterPicker` in Phase 1). Flagged as a background task rather than expanded
into this phase's scope.

## Fixed in review, before merge

Two defects the owner's pre-merge review caught, both fixed on this same branch rather than deferred:

**1. The sort popover was unreachable on a one-row table.** `GeoSortHeader`'s panel was
`position: absolute` inside `DataGrid`'s `overflow-hidden` → `overflow-auto` wrappers, so it was clipped
whenever it extended past the grid's own box — and because the panel lives inside a `position: sticky`
thead that scrolling never moves, the clipped part was unrecoverable rather than merely off-view.
Measured live on the shared dev instance: at three rows (the fixture this phase's own owner-test used) it
fits with 77px to spare, which is why the original owner-test passed; at one row it was clipped by 52px,
leaving **2px of the 40px Apply button** visible and the scroll container refusing to scroll at all.

Fixed the way `RolePicker`/`RelationshipPicker` already solve the identical problem — a `createPortal`
panel positioned with the shared `usePanelPosition` hook, which also brought outside-click and Escape
dismissal the original popover never had. `usePanelPosition` gained an optional `panelWidth` parameter
(defaulting to its existing 320px) because its left-edge clamp is computed against the panel's real
width, and the geo panel is 224px; both existing call sites pass nothing and are unchanged. Re-measured
after the fix on the same one-row case: no clipping ancestor at all, Apply 40px of 40px visible.

**2. `orderNear` validation errors named the wrong method.** `RequireNearValue` is shared by both
methods but hardcoded `'near'` in its message, so a bad `orderNear` value returned
`'near' requires numeric values.` / `'near' lat must be a number.` — pointing the caller at a query they
never sent. The error `type` (`general_query_invalid`) was correct throughout, so no public-API string
changed; only the human-readable message was wrong. `RequireNearValue` now takes the method name, and a
new `[Theory]` covers all four combinations (`orderNear` lat/lng, `near` lat/radiusMeters).

## Tests

`tests/Praxy.Tests.Unit/QueryDslTests.cs`: `orderNear` arity (1 and 3 values rejected, missing attribute
rejected, exactly 2 parses).

`tests/Praxy.Tests.Unit/QueryCompilerTests.cs` (new `loc` geo column added to the shared `BuildEntry`
helper): `orderNear` on a non-geo column rejected; on a geo column with no spatial index rejected; compiles
to the `<->`/`ST_MakePoint`/`::geography` shape with an available index; adds exactly one param each for
lat/lng (the landmine's own regression test); composes with a `near()` radius filter (`ST_DWithin` *and*
`<->` both present); works standalone with no `near()` filter; first order method wins when `orderAsc`
precedes `orderNear`.

`tests/Praxy.Tests.Integration/GeoEngineTests.cs` (real PostGIS-enabled Postgres, real San Francisco
coordinates from Phase 1's verified City Hall/Golden Gate Bridge/Ferry Building set):
`OrderNear_returns_rows_nearest_to_farthest`, composes with `near()`'s radius filter (bounding *and*
sorting both apply), works standalone (pure K-nearest, no radius filter), rejected cleanly with no spatial
index, rejected on a non-geo column, `orderAsc` sent first beats `orderNear` sent second, and
`OrderNear_paginates_via_keyset_cursor_without_duplicates_or_gaps` (5 rows placed at monotonically
increasing distance from the query point, paginated 2-at-a-time across multiple `cursorAfter` pages,
asserting the exact nearest-to-farthest sequence with no duplicates or gaps — same discipline
`RowEngineTests.Cursor_pagination_covers_every_row_exactly_once_in_order` already established for
column-based sorts).

## Owner-test checklist

Done by me this session, against the shared canonical local dev instance (`owner@test.local`, per
[[praxy-local-dev-instance]]) — its Postgres already carries the PostGIS extension from Phase 1, confirmed
by every step below succeeding without any container/image changes.

- Created database table `Places` (in the existing `Blog` database) with a `name` string column and a
  `location` geo column, a `spatial` index on `location`, and three rows at the same real coordinates
  Phase 1 verified (City Hall, Golden Gate Bridge, Ferry Building). **Left in place** for convenience —
  it's exactly the fixture the owner's own click-test needs, and adding it was purely additive (no
  existing data touched).
- Applied "Sort nearest to…" centered on City Hall via the new popover: rows reordered to City Hall →
  Ferry Building (~3217m) → Golden Gate Bridge (~7201m) — correct nearest-to-farthest order, confirmed via
  the live network request (`orderNear` with the right lat/lng) and the re-ordered grid.
  - Confirmed pagination past the first page is covered by the integration test above (harder to drive
    manually through only 3 seed rows in the console) — the underlying mechanism (index-assisted KNN scan
    surviving the keyset `WHERE`) was separately verified via `EXPLAIN ANALYZE` against 200k seeded rows.
- Cleared the sort via the header's "✕" button: reverted cleanly to default (`$id`) order, confirmed via
  screenshot.
- Attempted the sort against `location` with its spatial index temporarily dropped: confirmed a clean,
  actionable error — **`'location' has no spatial index.`** (see the retry/online-pause landmine above for
  why this needed a temporary `retry: 0` diagnostic to observe cleanly in this specific automation
  environment; the error message and throw path are both correct). Recreated the index afterward,
  confirmed `available` again before finishing.
- `git status` clean, `.claude/launch.json` reverted to its committed state (a session-local port
  workaround for running my own console dev server alongside another session's, needed only because this
  environment shares one working directory across sessions).

## Next

Whether a Phase 3 (a `$distance` return value, array-valued geo columns, more geo types/operators) is
warranted is the owner's call — this report doesn't design one, and no
`docs/handoff/geo-nearby-phase-3-prompt.md` was written, per this phase's own prompt.

Separately: the `retry: 1` / `useInfiniteQuery` error-surfacing landmine above is worth a look regardless
of geo — it's a general console robustness question (does a real user's browser ever hit the same stuck
state?), not specific to this feature.
