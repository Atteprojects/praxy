# Table relationships, Phase 1 (the primitive) — report

**Status: complete.** Every item in `docs/handoff/relationships-phase-1-prompt.md`'s scope shipped.
`dotnet test` green — **389 unit, 223 integration** (real Postgres via Testcontainers, including 5 new
tests for this feature). Console `tsc -b && vite build` clean. `dart analyze .` clean for `praxy_codegen`
(pre-existing unrelated info lints in `praxy_flutter` only); `dart test praxy_core praxy_codegen` green,
including a new relationship-column codegen case. Owner-tested end-to-end against the live local dev
instance: created `authors`/`posts`, added a scalar `relationship` column, linked a real row, confirmed
the wire-formatted id round-trips, confirmed a nonexistent target id is a clean field error, added a
`unique` index and confirmed the second row referencing the same author is rejected.

## What shipped

**`Praxy.Tables`** — the primitive, exactly the shape the design doc and prompt described:
- [`ColumnTypes.cs`](../../src/Praxy.Tables/ColumnTypes.cs): `Relationship = "relationship"` added to
  the type constants and `All`; `PostgresType` returns `"uuid"`. No `FormatLiteral`/`FormatArrayLiteral`
  case — defaults are rejected before either would ever be called with this type.
- [`RowByteBudget.cs`](../../src/Praxy.Tables/RowByteBudget.cs): `Relationship => 16` (uuid width),
  landed in the same edit as adding `Relationship` to `ColumnTypes.All` so there was never a window
  where a relationship column create would throw on the byte-budget re-estimate.
- [`ColumnsService.cs`](../../src/Praxy.Tables/ColumnsService.cs): `CreateAsync` gained a `targetTableId`
  parameter. `ValidateTypeShape` rejects a relationship column with no valid `targetTableId` and rejects
  any `default` value outright (`ArgumentInvalid`, same place enum's `elements`-required check lives).
  The target table is resolved via a same-database `db.Tables` lookup (`TableNotFound` if it doesn't
  resolve) before the DDL runs. Scalar DDL: `ALTER TABLE ... ADD COLUMN ... uuid REFERENCES
  target("_id") ON DELETE RESTRICT`; array: plain `uuid[]`, no FK — both stay in the existing synchronous
  transaction, no `schema_jobs` involvement.
- [`RowValues.cs`](../../src/Praxy.Tables/RowValues.cs): write path parses a wire id (or array of them)
  via `Ids.TryParseWire` in both `ToScalar` and `ToWriteValue`'s array branch. Read path has an
  **explicit** `Relationship` case in `ReadScalar`/`ReadArray` — reads as `Guid`, formats with
  `Ids.Wire(...)` — deliberately *not* the string-fallback default, which would emit Npgsql's dashed
  `Guid.ToString()` instead of Praxy's 32-hex-no-dashes wire shape.
- [`RowsService.cs`](../../src/Praxy.Tables/RowsService.cs): new `AssertRelationshipTargetsExistAsync`,
  called from inside `CreateAsync`'s and `UpdateAsync`'s existing `SchemaDdl.InTransactionAsync`
  delegates, right before the INSERT/UPDATE executes. Collects every relationship value present in the
  write, groups referenced ids by `TargetTableId`, and runs **one** `SELECT _id FROM target WHERE _id =
  ANY(@ids)` per distinct target table (not per column, not per id) — a row with several relationship
  columns pointing at the same table produces exactly one query. Missing ids reject with the new
  `relationship_target_not_found` (400), field-scoped, naming the missing id(s). No permission check
  coupled to this — plain existence, per the design doc's "linking to a row requires only that it exist."
- [`QueryCompiler.cs`](../../src/Praxy.Tables/QueryCompiler.cs): one-line change — `Relationship` added
  to `CompileSearch`'s early-rejection list. `equal`/`notEqual`/`isNull`/`isNotNull`/array `contains` all
  work already, once `RowValues.ToFilterScalar` has a `Relationship` case (the same generic per-type
  dispatch every other column type already goes through — no structural change needed).
- **One-to-one confirmed, zero new code**: a scalar relationship column plus the existing `unique` index
  type behaves as a working one-to-one out of the box — verified both in
  `RelationshipEngineTests.Scalar_relationship_plus_a_unique_index_behaves_as_one_to_one` and by hand in
  the console. `IndexesService.cs` was not touched.

**`Praxy.Persistence`**: `ColumnDef.TargetTableId` (`Guid?`), migration
`20260901005756_RelationshipColumns` — nullable `uuid` column, FK to `tables(id)`,
`ON DELETE RESTRICT` (not cascade — deleting a target table must never silently delete other tables'
relationship-column metadata; Phase 2 gates that delete explicitly).

**`Praxy.Api`**: `CreateColumnRequest`/`ColumnResponse` gained `TargetTableId`, threaded through both
`DatabaseEndpoints.CreateColumn` (data-plane) and `ConsoleDatabaseEndpoints.CreateColumn` (console).
New error type `relationship_target_not_found` in `ErrorTypes.cs`. `docs/openapi/v1.json` regenerated
(two new schema properties, no new operations).

**Console**: `ColumnsPage.tsx`'s `CreateColumnSheet` gets a `relationship` type option — a plain
`<select>` populated from `useTables` (the same hook the sidebar already uses) to pick the target table;
the Default field is hidden entirely for this type (rejected server-side anyway, so hiding it avoids a
guaranteed-to-fail round trip). The Columns grid's Attributes cell shows a `→ <target key>` badge.
`RowsPage.tsx`'s `EditableCell`/`CreateRowSheet` needed **no code change** — both already fall through to
a plain text `<input>` for any column type without a special case, which is exactly the Phase 1 behavior
the prompt asked for. `OPERATORS_BY_TYPE` (the row-filter picker's operator list) is now `Partial`, not a
full `Record`, so TypeScript compiles with `relationship` added to `ColumnType` without requiring an
entry — the filter picker simply offers no operators for a relationship column yet, matching the design
doc's explicit Phase 3 assignment for that wiring (see Deviations below).

**Flutter**: `praxy_codegen/lib/src/generator.dart`'s `_dartType` maps `'relationship'` to `'String'` —
the existing `isArray` wrapping (`List<String>`) applies automatically.

## Deviations & notes

- **`OPERATORS_BY_TYPE` needed a type change the design doc didn't anticipate.** The design doc
  (`docs/research/table-relationships.md`) assigns "`OPERATORS_BY_TYPE` gains a `relationship` entry" to
  Phase 3, on the assumption Phase 1 wouldn't need to touch it at all. But `RowsPage.tsx` declares it as
  `Record<ColumnType, ...>` — once `relationship` is added to the shared `ColumnType` union (required for
  `ColumnSchema.type`/`CreateColumnSheet`'s type selector), that `Record` stopped compiling without an
  entry for every key. Fixed by widening the type to `Partial<Record<ColumnType, ...>>` with a `?? []`
  fallback at both call sites (`describeFilter`, `FilterPicker`) — relationship columns are listed in the
  filter picker's attribute dropdown but offer zero operators, a graceful no-op rather than a crash.
  This is a type-checker necessity, not new console functionality; the real search-picker + operator
  wiring is still Phase 3's job, deferred as designed.
- **`ColumnDef.TargetTableId`'s EF delete behavior is `Restrict`**, not the default convention's
  `ClientSetNull`/`NoAction` — made explicit to match every other `HasForeignKey(...).OnDelete(...)` call
  in `PraxyDb.cs`'s style, even though the two behave identically in the generated migration for a
  nullable FK. This means attempting to delete a table that's still someone's relationship target
  already fails today, just as a raw, uncaught `23503` (an accepted Phase 1 rough edge per the prompt's
  own non-goals) — Phase 2 catches it and turns it into a clean `relationship_dependency` 409 with the
  `force=true` escape hatch.

## Known gaps (out of scope, noted for whoever picks them up)

- **Deleting a row or table that's a relationship target isn't blocked cleanly yet** — a scalar FK
  violation is a raw, unhandled `23503` (effectively a 500); an array relationship has no protection at
  all. This is Phase 1's documented non-goal, not an oversight — Phase 2 owns the fix.
- **No `?expand=`, no console search-picker** — both explicitly Phase 3. The console's relationship
  row-editor is a plain text id input; the row-filter picker offers no operators for a relationship
  column yet (see Deviations above).
- **No typed cross-table Flutter codegen** — out of scope for the whole three-phase sequence, not just
  Phase 1 (a `praxy_codegen` architecture change, sits alongside Storage/TOTP/multi-org as its own future
  initiative).

## Tests

`tests/Praxy.Tests.Unit/`:
- `ColumnTypesTests.cs` (new): `Relationship` is registered and backed by `uuid`/`uuid[]`.
- `RowByteBudgetTests.cs`: scalar (16B) and array-capped (160B) estimates.
- `RowValuesTests.cs`: wire-id round-trip to `Guid`, invalid-id rejection, array element conversion.
- `QueryCompilerTests.cs`: `equal`/`isNull` compile against a relationship column; an invalid id value is
  a clean 400, not a crash; `search` against a relationship column is rejected.

`tests/Praxy.Tests.Integration/RelationshipEngineTests.cs` (new, real Postgres):
- Scalar relationship rejects a nonexistent target (both create and update) and reads back the
  32-hex-no-dashes wire id.
- Array relationship rejects when any one id in the array is missing, accepts when all are present.
- A scalar relationship column plus a `unique` index behaves as a working one-to-one — the real Postgres
  FK plus the real unique-violation catch, no relationship-specific code.
- `equal`/`isNull`/array `contains` filter a relationship column through the query DSL.
- `search` against a relationship column is a clean 400 (`general_query_invalid`), not a silent wrong
  result.

Full-repo `dotnet test`: **389 unit, 223 integration**, all passing.

## Commands

No new commands or config knobs — relationship columns go through the exact same
`POST /v1/databases/{databaseId}/tables/{tableId}/columns/{type}` endpoint every other column type uses,
now accepting `targetTableId` when `type == "relationship"`. EF migration:
`20260901005756_RelationshipColumns` (already applied to the shared local dev instance during this
session's owner-test).

## Owner-test checklist

Done by me this session against the live local dev instance (`api`/`console` launch configs,
`owner@test.local`) — the shared dev api process had to be restarted to pick up this session's code (it
was running another chat's build); done with explicit owner sign-off, and the console (a pure static
dev server with HMR) needed no restart:

- Created database `Blog`, tables `Authors` (`name` string) and `Posts` (`title` string).
- Added a scalar `relationship` column `authorId` on `Posts` targeting `Authors` via the new
  target-table `<select>` — confirmed the Size/Elements/Default fields all correctly disappear for this
  type, matching `size`/`elements`'s existing immutable-after-creation pattern.
- Created an author row (`Ada Lovelace`).
- Attempted a post referencing a made-up id — got a clean inline field error ("References a row that
  doesn't exist: ...") through the same field-error path every other cell already uses, not a crash.
- Created a post referencing the real author id — it read back in the rows grid with the author's id in
  the correct 32-hex-no-dashes wire format.
- Added a `unique` index on `authorId` — queued via `schema_jobs` as usual, settled from `processing` to
  `available` within seconds.
- Created a second post referencing the same author — rejected with `409 A row with this id already
  exists.`, the same `23505` unique-violation catch every other unique index already goes through.

## Next

`docs/handoff/relationships-phase-2-prompt.md` — delete-time integrity (`row_referenced`,
`relationship_dependency`, `force=true` on the table side), per
`docs/research/table-relationships.md`'s own Phase 2 scope.
