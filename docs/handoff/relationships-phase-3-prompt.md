# Session task — Table relationships, Phase 3 (read-time expansion + console ergonomics)

## Why this exists

Phases 1 and 2 shipped the `relationship` column type end to end — create, write with existence
validation, query, and now delete-time integrity (`row_referenced`/`relationship_dependency`,
`force=true` on the table side). What's left, by design, is entirely read-time and console-UX: reading a
relationship column still returns a raw id (or array of ids); the console's row editor is still a plain
text input; the row-filter picker offers zero operators for a relationship column. Read
`docs/research/table-relationships.md` in full before writing any code — its "Phase 3" section is the
complete design this prompt scopes from — and `docs/handoff/relationships-phase-1-report.md` /
`docs/handoff/relationships-phase-2-report.md` for what's already built and two things worth knowing
before you start:

- Phase 1's `OPERATORS_BY_TYPE` is already `Partial<Record<ColumnType, ...>>`, not a full `Record` — it
  was widened early because TypeScript required an entry the moment `relationship` joined `ColumnType`,
  even though the design doc originally assigned that wiring to this phase. The type change is done;
  only the actual `relationship` entry (this phase's job) is missing.
- Phase 2 discovered that `ColumnDef.TargetTableId` can be `null` even on a column whose `Type` is still
  `"relationship"` — force-deleting a target table clears it (`ON DELETE SET NULL`, metadata-level,
  distinct from the physical FK) while leaving the column and its data in place, orphaned. Any code this
  phase adds that reads `column.TargetTableId` for a relationship-typed column **must** handle `null`
  without throwing — this is exactly the "target table that was itself force-deleted" fallback case the
  design doc already calls out for expansion, so it's expected, not a new edge case to invent.

Work on a new branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **No configurable per-table "display field."** The console picker searches by `$id` prefix only, per
  the design doc's explicit reasoning: there's no existing "which field represents a row to a human"
  concept in Praxy today, and inventing one is a real, separate feature worth its own future decision.
- **No realtime changes anywhere.** `ChannelGrammar.cs` stays untouched — a row event on table A never
  fans out to table B's subscribers, by design, across all three phases of this feature.
- **No recursive expand.** Expansion is one level deep only. An expanded row's own relationship columns
  are never themselves expanded — they stay as raw ids inside the expanded object.
- **No typed cross-table Dart codegen.** Out of scope for the whole three-phase sequence, not just this
  phase (see the design doc's closing section — a `praxy_codegen` architecture change, sits alongside
  Storage/TOTP/multi-org as its own future initiative).
- **No new error type for `?expand=` validation.** An invalid `expand` value (unknown column key, or a
  key that isn't a relationship column) reuses the existing `general_query_invalid` — the same error
  `QueryCompiler`'s `search`-on-relationship rejection already uses for "this doesn't make sense for this
  column."

## Scope

1. **`?expand=<columnKey>[,<columnKey>...]` on list/get-row endpoints**
   (`src/Praxy.Api/Endpoints/RowEndpoints.cs`'s `ListRows`/`GetRow`, threaded into
   `RowsService.ListAsync`/`GetAsync`). Read the same way `queries[]`/`queries` already are
   (`RowEndpoints.QueryStrings`'s sibling for a single comma-separated query param — check whether the
   console/SDKs expect `expand=a,b` or `expand[]=a&expand[]=b` and match whatever's simplest given no
   existing precedent forces one over the other; a single comma-separated string is the more common REST
   convention and matches the design doc's own `?expand=<columnKey>[,...]` framing).
2. **Validate each requested key names a real relationship column on this table** before running any
   query — unknown key or non-relationship key is `general_query_invalid` (400), field-scoped if that's
   the existing convention for this error type (check how `QueryCompiler`'s current
   `general_query_invalid` throws shape their message/fields).
3. **The enrichment pass** — a new method in `RowsService.cs`, run *after* the primary query (list or
   get) has already produced its rows, not folded into the primary SQL:
   - Collect every relationship value present in the requested expand columns across all result rows
     (a list endpoint may return many rows; batch across all of them, not per-row).
   - Skip any relationship value that's `null` (never linked) — nothing to expand.
   - Skip any relationship column whose `TargetTableId` is `null` (Phase 2: target table was
     force-deleted) — fall back to the raw id, per the design doc's three-fallback-causes framing.
   - Group the remaining ids by target table, same shape as Phase 1's
     `AssertRelationshipTargetsExistAsync` batching (one query per distinct target table, not per row,
     not per id) — but this time an actual `SELECT` of the row's full column set, not just `_id`, and
     resolve each target table via `CatalogCache.GetAsync` exactly like Phase 1/2 already do.
   - **Run each target table's rows through that target table's own
     `QueryCompiler.CompilePermissionPredicate` read check**, using the *caller's* roles — this is the
     one piece of read-path permission logic expansion needs, and it must reuse the compiler, not
     reimplement a check. A row the caller can't read is treated identically to a row that no longer
     exists: fall back to the raw id, don't error the request, don't leak the row's data.
   - Replace the raw id (or each element of the raw id array) with the expanded row's full JSON object
     (built the same way `RowsService.BuildRowJson` already builds any row — reuse it, don't duplicate
     field-materialization logic) in the response, keyed under the same column key the raw id used to
     occupy. Confirm with the design doc / your own judgment call exactly how an expanded value should be
     shaped inline (e.g. does the column key now hold an object/array-of-objects instead of a
     string/array-of-strings — almost certainly yes, since `RowSheet` already renders arbitrary JSON with
     no assumption about a column's shape).
4. **`QueryCompiler`'s `OPERATORS_BY_TYPE`-equivalent on the server side is unaffected** — expansion is
   read-time enrichment, not a new filter/query capability. Query filtering on a relationship column
   (`equal`/`isNull`/etc.) already works from Phase 1 and needs no change here.
5. **Console**: `console/src/api/types.ts`'s `OPERATORS_BY_TYPE` gains a `relationship` entry — `equal`,
   `notEqual`, `isNull`, `isNotNull` always; `contains` only when the column `IsArray`. Never
   `startsWith`/`endsWith` (meaningless on uuids) — matches exactly what the query compiler supports, per
   the design doc.
6. **Console search-picker**, replacing `RowsPage.tsx`'s plain text input for a relationship column's
   value in both `EditableCell` and `CreateRowSheet`: model it on
   `console/src/components/RolePicker.tsx`'s portal-popover-with-search *structure* (positioning,
   open/close, keyboard nav, search-as-you-type debounce shape) — not its role-specific content, which is
   a fixed local list, not a network search. This picker searches the *target table's* rows by `$id`
   prefix (per the non-goal above — no display-field concept yet), calling the target table's existing
   list endpoint with a query filtering `$id` by prefix (check whether the query DSL already supports a
   prefix/`startsWith`-shaped filter on `$id`/`IdType`, or whether this needs the picker to fetch a page
   and filter client-side for a first cut — either is defensible, but be explicit in the report about
   which you chose and why). Target immutable after column creation still holds (unchanged from Phase 1)
   — the picker only affects *row* editing, not column configuration.
7. **Grid/row-sheet rendering needs no new component.** `RowSheet` already only shows raw JSON; an
   expanded value from `?expand=` is just richer raw JSON. Confirm this holds rather than assuming it —
   if the rows grid or row sheet make any assumption about a relationship column's value always being a
   string/array-of-strings (e.g. for display formatting), that assumption breaks once expansion is wired
   up and needs a small, targeted fix, not a new component.

## Landmines — read before writing code

- **`TargetTableId` can be `null` on a `relationship`-typed column (Phase 2's orphaning).** Every place
  this phase reads `column.TargetTableId` for a relationship column — the expand-key validation, the
  enrichment pass's target-table grouping — must treat `null` as "fall back to raw id," not throw. This
  is the same shape as the existing null-check Phase 2 added to
  `RowsService.AssertRelationshipTargetsExistAsync` for the write path; read the surrounding code there
  before writing the read-path equivalent.
- **Expansion must reuse `QueryCompiler.CompilePermissionPredicate`, not reimplement a permission
  check.** This is the one role-resolution dependency expansion has, and the design doc is explicit that
  coupling it to anything *other* than the real read-check would be a second, divergent permission
  implementation — a durable maintenance hazard, not a shortcut.
- **A row that no longer exists, a row the caller can't read, and a relationship column whose target
  table was force-deleted are three different causes with one uniform outcome**: fall back to the raw
  id, never error the whole request, never leak data. Don't special-case any of the three differently in
  the response shape.
- **Batch the enrichment query the same way Phase 1 batched the existence pre-pass**: one query per
  distinct target table across the *entire* result set (all rows of a list response), not per row and
  not per relationship column. A list of 50 rows each with a `coAuthorIds` array pointing at the same
  `authors` table should produce one `SELECT ... WHERE _id = ANY(@ids)` against `authors`, not 50.
- **`CatalogCache` is keyed per table with a 5-second TTL** (Phase 2 learned this the hard way — see its
  report's Deviations section). Nothing in this phase's scope needs new cache-invalidation logic, but if
  you find yourself reasoning about stale `TargetTableId`/column metadata anywhere, that's very likely
  the cache, not a bug in your new code — check there before adding a workaround.

## Tests

`tests/Praxy.Tests.Integration/RelationshipEngineTests.cs` — extend again rather than starting a new
file:
- `?expand=authorId` on a list/get endpoint returns the author's full row JSON in place of the raw id.
- `?expand=coAuthorIds` (array) returns an array of full row JSON objects in the same order the raw ids
  were in.
- Expanding a relationship value that points at a row the caller's roles can't read falls back to the raw
  id, not an error and not the row's data.
- Expanding a relationship value pointing at a row that was itself deleted (row-level, not table-level)
  falls back to the raw id.
- Expanding a relationship column whose target table was force-deleted (Phase 2 scenario — build on the
  existing orphaning test fixture) falls back to the raw id, doesn't throw.
- `?expand=` naming an unknown column, or a non-relationship column, is a clean `general_query_invalid`
  (400).
- A list response with several rows sharing the same target table produces exactly one batched query
  against that target table (verify via whatever query-counting mechanism existing tests in this repo
  already use for batching assertions, if any — otherwise this can stay a design-level guarantee verified
  by code review rather than a literal query-count assertion).

`tests/Praxy.Tests.Unit/`: extend `QueryCompilerTests.cs`/wherever `OPERATORS_BY_TYPE`'s console-side
type lives if there's a unit-testable piece of the operator-list change; the bulk of this phase's console
work (the search-picker) is UI, verified via the owner test below, not unit tests.

## Done means

- `dotnet test` green (unit + integration, real Postgres).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run**: in the console, on the existing `Blog`/`Authors`/`Posts` tables (recreate
  `Authors` if Phase 2's owner-test session left it force-deleted — check current state first), create a
  fresh author, create a post linking to it via the new search-picker (not a pasted id), confirm the rows
  grid / row sheet shows the linked author's data when the column is expanded, confirm the row-filter
  picker now offers `equal`/`isNull`/etc. for the relationship column, and confirm a relationship value
  pointing at a row the current caller can't read (or that's been deleted) falls back to showing the raw
  id gracefully rather than erroring the screen.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/relationships-phase-3-report.md`. This is the last phase in the sequence per
  `docs/research/table-relationships.md` and `docs/roadmap.md`'s "Table relationships" section — no
  `relationships-phase-4-prompt.md` needed unless the report identifies genuinely new scope. Update
  `docs/roadmap.md`'s "Table relationships" section to mark the whole initiative shipped, the same way
  other completed post-v0.1.0 initiatives (Sites, Next.js SDK) are recorded there.
