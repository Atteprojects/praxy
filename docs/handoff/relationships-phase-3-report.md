# Table relationships, Phase 3 (read-time expansion + console ergonomics) — report

**Status: complete.** Every item in `docs/handoff/relationships-phase-3-prompt.md`'s scope shipped.
`dotnet test` green — **389 unit, 238 integration** (real Postgres via Testcontainers, 8 new tests for
this phase). Console `tsc -b && vite build` clean. Owner-tested end to end against the live local dev
instance — `Authors` had to be recreated (Phase 2's owner-test session left it force-deleted, per the
prompt's own anticipation), then a fresh scalar and array relationship column, a real linked row, the
row-filter operators, and the search-picker were all exercised live.

## What shipped

**`Praxy.Tables`** (`RowsService.cs`):
- `GetAsync`/`ListAsync` both gained an `IReadOnlyList<string> expandKeys` parameter. `ResolveExpandColumns`
  validates every requested key up front — before any query runs — resolving it to a real column and
  rejecting an unknown key or a non-relationship key with `general_query_invalid` (400), reusing
  `QueryDsl.Invalid` exactly the way `QueryCompiler`'s existing `search`-on-relationship rejection does.
  No new error type, per the prompt's own non-goal.
- New `ExpandAsync`, run after the primary query (and after `AttachPermissionsAsync`) has already produced
  rows: collects every relationship value across all requested expand columns and all result rows, grouped
  by target table id into one `Dictionary<Guid, HashSet<Guid>>` — a column whose `TargetTableId` is `null`
  (Phase 2's orphaning) is skipped entirely, contributing no ids. One batched `SELECT ... WHERE _id =
  ANY(@ids) AND (<permission predicate>)` runs per distinct target table (never per row, never per column),
  using `QueryCompiler.CompilePermissionPredicate` with the *caller's* roles — the same compiled predicate
  the target table's own read endpoints use, not a reimplementation. `AttachPermissionsAsync` runs on the
  expanded rows too, so an expanded object's own `$permissions` field is populated the same way a direct
  fetch of that row would be. The raw id (or each element of an array) is replaced with the expanded row's
  full JSON — built via the existing `BuildRowJson`, no duplicated field-materialization logic — via
  `JsonNode.DeepClone()` per occurrence, since the same expanded row can appear under multiple columns or
  multiple times across a list of rows and `JsonNode` is single-parented.
  Three distinct fallback causes (target table force-deleted, referenced row no longer exists, caller's
  roles can't read it) share one code path and one outcome: the id simply never appears in the batched
  query's result set (or the column is skipped before any query runs), so the original raw id/array element
  is left untouched — no branching by cause, no error, no leaked data.
- `AssertRelationshipTargetsExistAsync`/`AssertNotArrayReferencedAsync` (Phase 1/2) are untouched —
  expansion is purely additive read-time enrichment.

**`Praxy.Api`**: both `RowEndpoints.cs` (data plane) and `ConsoleRowEndpoints.cs` (console) thread a new
`RowEndpoints.ExpandKeys(HttpContext)` helper — a single comma-separated `?expand=a,b` (the more common
REST convention, matching the design doc's own `?expand=<columnKey>[,...]` framing; no existing precedent
forced the `expand[]=a&expand[]=b` shape) — into `RowsService.GetAsync`/`ListAsync`. `UpdateAsync`'s two
internal `GetAsync` fallback calls (the genuinely-partial-update short circuits) pass `[]` — an update
request was never asked to support expansion, and didn't need to change shape to keep compiling.

**Console**:
- [`console/src/screens/RowsPage.tsx`](../../console/src/screens/RowsPage.tsx): `OPERATORS_BY_TYPE` gains a
  `relationship` entry. Because operator *eligibility* for `relationship` depends on the column's own
  `array` flag (not just its type — `contains` only makes sense for the array case, unlike every other
  type in this map), a new `relationshipOps(array: boolean)` function replaces the static lookup at the one
  call site (`FilterPicker`) that needs to be array-aware; the static map entry itself holds the array
  superset, which is all `describeFilter`'s label lookup needs. Verified live: a scalar relationship column
  offers exactly `=`/`≠`/`is NULL`/`is not NULL`; an array one adds `contains`; neither ever offers `starts
  with`/`ends with`.
  `formatCell` gained one targeted fix: a relationship value can now be an object (or array of objects)
  once `?expand=` is wired up, which the grid's cell formatter never had to handle before — an unformatted
  object would have rendered as `[object Object]`. It now shows a short `#<id prefix>` badge for an
  expanded value and falls through to the previous behavior (`String(value)`) for a raw, unexpanded id.
  `RowSheet` needed literally no change — confirmed live: `JSON.stringify(row, null, 2)` on the "Raw JSON"
  tab already shows an expanded value's full nested object with no assumption to break.
  `RowsPage()` now always requests `?expand=<every relationship column key>` on its row list — the only way
  the owner can see a linked row's actual data without a display-field concept (this phase's own explicit
  non-goal), and cheap since it's the same query either way.
  `EditableCell` and `CreateRowSheet` both replace the plain text input for a relationship column
  (`column.targetTableId` set) with the new `RelationshipValueEditor` picker; a relationship column whose
  target table was itself force-deleted (`targetTableId` null) falls through to the original plain text
  input unchanged — verified live against the pre-existing orphaned `authorId` column, which still edits
  and displays as a raw string exactly as it did before this phase.
- [`console/src/components/RelationshipPicker.tsx`](../../console/src/components/RelationshipPicker.tsx)
  (new): `RelationshipPicker` is the portal-popover-with-search itself, modeled on
  `RolePicker.tsx`'s *structure* — positioning (`usePanelPosition`, exported from `RolePicker.tsx` for this
  reuse), open/close (click-outside, Escape), and result-row rendering (`PickerRow`, also exported) — not
  its role-specific (fixed local list) content. It searches the target table's rows by `$id` prefix: **a
  deliberate first cut, fetching one page (limit 100) via the target table's existing console list endpoint
  and filtering by `$id.startsWith()` client-side**, not a server-side prefix query, because
  `QueryCompiler.CompileStringOrArrayOp` explicitly rejects `startsWith`/`contains`/`endsWith` on the `$id`
  (`IdType`) column today ("not supported on row ids") — loosening that check is a separate, broader change
  this phase's "console ergonomics" scope doesn't call for, and the prompt's own text explicitly sanctioned
  either choice. `RelationshipValueEditor` is the shared chip-list-plus-trigger shape used by both
  `EditableCell` (grid) and `CreateRowSheet` (create form): renders a chip per currently-linked id (each
  with its own remove button), and a "+ pick row" trigger that opens the picker — always shown for an array
  column, shown only when empty for a scalar one. `console/src/api/rows.ts`'s `useRows` gained an `expand:
  string[] = []` parameter, appended as `expand=a,b` when non-empty.

## Deviations & notes

**A real bug found only by clicking through the owner-test, not by code review: portal clicks bubble
through the React tree, not the DOM tree.** `RelationshipPicker` renders via `createPortal(..., document.body)`
— its DOM node sits outside the grid row entirely, but React's synthetic event system dispatches bubbling
`onClick` events along the *component* tree that rendered the portal, not the DOM ancestor chain. Since
`RelationshipPicker` is rendered from `RelationshipValueEditor`, rendered from `EditableCell`, which sits
inside one `DataGrid` row, picking a candidate row was *also* bubbling up to that row's own `onClick`
handler and popping open the row sheet immediately after the pick — confirmed live on the first attempt
(picking Ada Lovelace for `authorRef` opened the row sheet unexpectedly), then confirmed fixed after adding
`onClick={(e) => e.stopPropagation()}` on the popover's root portaled `<div>` (a second pick-and-unlink
cycle, done via precise element refs rather than eyeballed coordinates, showed no sheet opening).
`RolePicker.tsx`'s own popover never hit this because every existing call site renders it from inside a
`Sheet` (a modal), which has no equivalent parent-row click handler to accidentally trigger — this is a
`RelationshipPicker`-specific fix, not a latent bug in the component it's modeled on.

**`RelationshipPicker` fetches via the console (operator-bypass) row endpoint, not the data-plane one.**
The picker only ever runs inside the console (an operator session), so
`/console/projects/{projectId}/databases/{databaseId}/tables/{targetTableId}/rows` — the same endpoint
`RowsPage.tsx` already uses for everything else — is the correct one; no new endpoint needed.

**Expanded rows get `$permissions` populated (via the existing batched `AttachPermissionsAsync`), not left
as the empty array `BuildRowJson` defaults to.** The prompt's text only requires reusing `BuildRowJson`
for field materialization; attaching real row permissions to the expanded object as well costs one more
batched query per target table (only when that table has `row_security` on) and makes an expanded row's
shape match what a direct fetch of it would actually return, rather than a JSON object that looks fetched
but silently lies about its own permissions.

## Known gaps (out of scope, noted for whoever picks them up)

- **No configurable display field** — explicitly out of scope per the design doc; the picker searches by
  `$id` prefix only, and the grid shows a short `#<id>` badge for an expanded relationship value. A future
  "which field represents a row to a human" feature would upgrade both, but is its own decision.
- **The picker's search is client-side over one fetched page (limit 100)**, not a server-side query — a
  target table with more than 100 rows may not surface a match outside that first page by prefix search
  alone. Documented as the deliberate first cut the prompt itself allowed; a server-side `$id` prefix filter
  would need loosening `QueryCompiler`'s current `IdType` rejection of `startsWith`, a separate, broader
  change.
- **No typed cross-table Flutter codegen** — out of scope for the whole three-phase sequence, not just this
  phase (a `praxy_codegen` architecture change, sits alongside Storage/TOTP/multi-org as its own future
  initiative).

## Tests

`tests/Praxy.Tests.Integration/RelationshipEngineTests.cs` — extended again, 8 new cases, all against real
Postgres via Testcontainers:
- `?expand=authorId` on get returns the author's full row JSON in place of the raw id.
- `?expand=authorId` on list does the same.
- `?expand=coAuthorIds` (array) returns an array of full row JSON objects in the same order as the raw ids.
- Expanding a value pointing at a row the caller's roles can't read (a bespoke setup withholding
  `read("any")` from the target table while keeping create/update/delete public) falls back to the raw id.
- Expanding a value pointing at a row that no longer exists falls back to the raw id — reached via a new
  `DeleteRowBypassingGuardsAsync` test helper that deletes the physical row directly, bypassing
  `RowsService.DeleteAsync`'s own guard entirely. This is the only deterministic way to reach this state:
  the array case's check-then-delete race is an accepted, documented Phase 2 gap (there's no app-reachable
  path to it), and the scalar case's real Postgres FK makes the equivalent state structurally impossible to
  reach at all, by design.
- Expanding a column whose target table was force-deleted (Phase 2 scenario, reusing that fixture) falls
  back to the raw id, doesn't throw.
- `?expand=` naming an unknown column, or a non-relationship column (`title`), is a clean
  `general_query_invalid` (400).
- Several rows sharing the same target table all expand correctly (a list of 3 posts referencing the same
  author). No query-counting harness exists in this repo to literally assert "exactly one query" — per the
  prompt's own allowance, that guarantee rests on code review of `ExpandAsync`'s single
  `Dictionary<Guid, HashSet<Guid>>` grouping (one entry per distinct target table id, built across every row
  and every expand column before any query runs) plus this correctness check.

Full-repo `dotnet test`: **389 unit, 238 integration**, all passing.

## Commands

No new commands or config knobs, no new EF migration. `?expand=` is a query parameter on the existing
`GET .../rows` and `GET .../rows/{rowId}` endpoints (both data-plane and console), not a new route.

## Owner-test checklist

Done by me this session against the live local dev instance (`api`/`console` launch configs,
`owner@test.local`) — the shared `api` process was running another chat's build and had to be restarted
(exact PID killed, confirmed with the owner first since it was serving a concurrent session) to pick up
this phase's code; the console (pure static dev server with HMR) needed no restart:

- Found `Authors` force-deleted (Phase 2's owner-test session left it that way, exactly as the prompt
  anticipated) and `Posts.authorId` still present showing its raw, now-permanently-orphaned id gracefully
  in the grid (`targetTableId` null — no picker rendered for it, correctly falls through to the plain text
  input, matching pre-Phase-3 behavior).
- Recreated `Authors` (`name` string), created author row "Ada Lovelace".
- Added a fresh scalar relationship column `authorRef` on `Posts` targeting `Authors` (the old `authorId`
  can never be re-targeted once orphaned) — confirmed Size/Elements/Default all still correctly hidden for
  this type.
- Linked the existing "Ghost Post, edited" row to Ada via the new search-picker (not a pasted id) — the
  popover listed Ada by a truncated `$id`, picking it saved instantly, and the row's `authorRef` cell showed
  a removable chip. Confirmed via the row sheet's Raw JSON tab that `authorRef` now holds Ada's full
  expanded row object, not just her id.
- Found and fixed a real bug live: the first pick also popped open the row's detail sheet unexpectedly
  (portal event bubbling through React's component tree — see Deviations); re-tested with a clean
  unlink-then-repick cycle after the fix and confirmed no sheet opens.
- Created a brand-new post ("Analytical Engine Notes") entirely through `CreateRowSheet`, picking Ada via
  the same search-picker (including typing a partial id prefix to confirm search-as-you-type filtering
  actually narrows the candidate list) rather than typing her id — row created, `authorRef` correctly linked
  from creation.
- Added an array relationship column `coAuthorIds` targeting `Authors`; confirmed the grid renders a
  "+ pick row" trigger that stays available after adding one linked row (arrays always allow more, unlike
  the scalar case which hides the trigger once linked).
- Confirmed the row-filter picker: `authorRef` (scalar) offers exactly `=`/`≠`/`is NULL`/`is not NULL`;
  `coAuthorIds` (array) adds `contains`; neither ever offers `starts with`/`ends with`. Added a live
  `coAuthorIds contains <Ada's id>` filter and confirmed it correctly narrowed the list to the one matching
  post via the real query compiler, then cleared it.
- Confirmed via the browser's network log that the rows list request actually carries
  `?expand=authorId,authorRef,coAuthorIds` and that the response body embeds each relationship column's
  full linked-row JSON (or `null`/raw id where nothing to expand), end to end.
- The "caller can't read" and "row deleted while still referenced" fallback scenarios are covered by the
  new integration tests above rather than a live console click-through — the console's own row endpoints
  always run with `bypassPermissions: true` (operators manage the whole project), so there's no console-only
  way to construct a caller whose roles can't read a target row; that fallback is meaningfully exercisable
  only through the data-plane API with a real session/key, which the integration tests already do.

## Next

None — this is the last phase in the three-phase sequence per `docs/research/table-relationships.md` and
`docs/roadmap.md`'s "Table relationships" section, which this session also updates to mark the whole
initiative shipped. No `relationships-phase-4-prompt.md`.
