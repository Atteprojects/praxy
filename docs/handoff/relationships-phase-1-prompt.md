# Session task — Table relationships, Phase 1 (the primitive)

## Why this exists

Relationships have been deferred since before v0.1.0 shipped ("Relationships deferred to v1.1" —
Phase 2's own scope note in `docs/roadmap.md`). Read `docs/research/table-relationships.md` in full
before writing any code — it's the complete architecture (why the model is "reuse the existing
array-column mechanism," not an Appwrite-style `relationType`/junction-table model; why expansion is a
separate enrichment pass; why `TargetTableId` is a real relational column, not jsonb; why same-database
only) with every design decision's reasoning spelled out. This prompt assumes you've read it and
doesn't re-explain what's already settled there.

This is Phase 1 of a 3-phase sequence (see `docs/roadmap.md`'s "Table relationships" section for the
full breakdown). This phase ships **the primitive**: a working `relationship` column type you can
create, write to (with existence validation), and query — end to end, for real, against a real
Postgres instance. Delete-time integrity (Phase 2) and read-time expansion + a real console picker
(Phase 3) are explicitly not this phase's job. Work on a new branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **No `?expand=`.** Reading a relationship column returns the raw id (or array of ids) as a wire
  string, exactly like every other column type today. Phase 3's job.
- **No delete-time blocking.** Deleting a row or table that's referenced by a relationship column is
  not this phase's concern — the scalar case will currently surface as a raw, unhandled
  `foreign_key_violation` (effectively a 500) until Phase 2 catches it cleanly. This is a known,
  accepted rough edge for this phase, not something to patch around here — don't add an ad hoc catch
  for it; Phase 2 owns the real fix (`row_referenced`/`relationship_dependency`).
- **No search-picker UI.** The console's row editor gets a plain text input for a relationship
  column's value in this phase — the server validates existence on save, same as any other field
  error today. Phase 3 replaces this with a real picker modeled on `RolePicker.tsx`.
- **No typed cross-table Flutter codegen.** `praxy_codegen` generates a relationship column as a plain
  `String`/`List<String>` (the raw id), exactly like it would for any other scalar/array column. Do
  not attempt to make codegen aware of the target table's generated class — that needs multi-table
  graph awareness `praxy_codegen`'s `generate()` doesn't have today, and is out of scope for this
  entire feature (see the research doc's closing section), not just this phase.
- **No cross-database relationships.** A relationship column's target table must be in the *same*
  database as the referencing table — validate and reject otherwise with a normal field error
  (reuse `TableNotFound` if the target doesn't resolve within the same database at all).
- **No default value on relationship columns.** Reject `default` outright at column-create validation
  time if `type == "relationship"` — a normal `ArgumentInvalid` field error, same place enum's
  `elements`-required check already lives. This means `ColumnTypes.FormatLiteral`/`FormatArrayLiteral`
  need no new case at all — don't add one.

## Scope

1. **`ColumnTypes.cs`**: add `Relationship = "relationship"` to the type constants and `All`.
   `PostgresType("relationship", ...)` returns `"uuid"`. No `FormatLiteral`/`FormatArrayLiteral` case
   (see non-goals — defaults are rejected before either would ever be called with this type).
2. **`ColumnDef` gains `TargetTableId (Guid?)`** (`src/Praxy.Persistence/Entities/Tables.cs`), FK to
   `TableDef`, nullable (only set when `Type == "relationship"`), same-database validated at write
   time in `ColumnsService`. New EF migration from `src/Praxy.Persistence`.
3. **`RowByteBudget.EstimateBytes`** — add the `Relationship` case. **Do this before anything else
   that touches columns** — `ColumnsService.CreateAsync` calls `AssertRowBudgetAsync` unconditionally
   on every column create (re-estimating every existing column, not just the new one), so adding
   `Relationship` to `ColumnTypes.All` without this throws immediately on the very first relationship
   column create attempt on any table that already has other columns. 16 bytes scalar (uuid width);
   array capped the same way every other array type already is.
4. **`ColumnsService.CreateAsync`**: new `targetTableId` parameter/request field. Validate it resolves
   to a real `TableDef` in the same database. DDL:
   - Scalar (`isArray: false`): `ALTER TABLE {qualified} ADD COLUMN {quoted} uuid REFERENCES
     {target_qualified}("_id") ON DELETE RESTRICT`.
   - Array (`isArray: true`): `ALTER TABLE {qualified} ADD COLUMN {quoted} uuid[]` — no FK, Postgres
     can't constrain array elements.
   Both stay in the existing **synchronous** DDL path (same transaction as the metadata write) — a
   fresh, empty column needs no rewrite or backfill, so this does **not** go through the
   `schema_jobs` async queue.
5. **`RowValues.cs`** — write path: `ToScalar`/`ToWriteValue`'s array branch get a `Relationship` case
   parsing a wire id string (or array of them) via `Ids.TryParseWire` (same helper `QueryCompiler`'s
   `$id` handling already uses) — this stage only validates *shape* (is it a well-formed id?), not
   existence (that's the new async pre-pass, next item). Read path: `ReadScalar`/`ReadArray` need an
   **explicit** `Relationship` case, not the string-fallback default — Npgsql maps Postgres `uuid` to
   `Guid` natively, and the existing default branch's `GetFieldValue<string>` would format it as
   Npgsql's own dashed representation, not Praxy's 32-hex-no-dashes wire format. Read as `Guid`,
   format with `Ids.Wire(...)`.
6. **New async existence pre-pass** in `RowsService.CreateAsync`/`UpdateAsync`, ahead of the existing
   synchronous per-field builder (both already run inside `SchemaDdl.InTransactionAsync`'s async
   delegate — this is a same-shape addition, not a rework). For every relationship-typed column
   present in the incoming write, collect referenced ids grouped by `TargetTableId`, run **one**
   `SELECT _id FROM {target} WHERE _id = ANY(@ids)` per distinct target table (not per column, not per
   id), and reject with the new `relationship_target_not_found` (400, field-scoped, naming the missing
   id(s)) if any referenced id doesn't come back.
7. **`QueryCompiler`/`RowValues.ToFilterScalar`**: a relationship column supports `equal`, `notEqual`,
   `isNull`, `isNotNull`, and (array only) `contains` — the same generic per-column-type dispatch every
   other type already goes through, once the filter-scalar conversion has a `Relationship` case. No
   structural change to `QueryCompiler.cs` itself. Add `Relationship` to `CompileSearch`'s early-rejection
   list (alongside `IdType`/`Datetime`/`Integer`/`Float`/`Boolean`) for a precise error message —
   `search` on a uuid makes no sense.
8. **One-to-one relationships need zero new code**: a scalar relationship column plus the *existing*
   `unique` index type already gives you it — `IndexesService.cs` has no type restriction on
   `key`/`unique` indexes today. Just confirm this actually works end to end in your own testing;
   don't add anything to `IndexesService.cs`.
9. **API DTOs** (`src/Praxy.Api/Endpoints/TablesDtos.cs`/`RowDtos.cs` or wherever `Column`
   request/response records live) — thread `targetTableId` through create-column request and
   column-response shapes.
10. **Console**: `ColumnsPage.tsx`'s `CreateColumnSheet` gets a 4th type branch — a plain `<select>`
    populated from the current database's tables (reuse whatever hook already lists them for the
    sidebar) to pick the target table. Matches `size`/`elements`: immutable after creation, so
    `EditColumnSheet` needs no relationship-specific field. `console/src/api/types.ts`'s `ColumnSchema`
    gains `targetTableId: string | null`. `RowsPage.tsx`'s `EditableCell`/`CreateRowSheet` get a plain
    text input for a relationship column's value — no picker, no search, just a text field; server
    validation surfaces through the same field-error path every other cell already uses.
11. **Flutter codegen** (`sdk/flutter/praxy_codegen/lib/src/generator.dart`'s `_dartType`): add
    `'relationship' => 'String'` to the scalar-type switch — the existing `isArray` wrapping
    (`List<$scalar>`) applies automatically, no special-casing needed.

## Landmines — read before writing code

- **Every exhaustive `switch` over column type that has no `default` fallthrough (or throws on
  unknown) needs a `Relationship` case or it 500s, not 400s, the moment it's hit.** Confirmed list:
  `ColumnTypes.PostgresType`, `RowByteBudget.EstimateBytes` (see Scope #3 — do this one first),
  `RowValues.ToScalar`, `RowValues.ToWriteValue`'s array branch, `RowValues.ReadScalar`,
  `RowValues.ReadArray`. Grep for `ArgumentOutOfRangeException`/`throw new` inside a `switch (type)`
  or `switch (column.Type)` across `src/Praxy.Tables/` yourself before you're done, in case this list
  missed one.
- **`ReadScalar`/`ReadArray`'s string-fallback default will silently "work" for a relationship column
  in the sense of not throwing — and produce the wrong wire format.** Don't trust "it compiles and
  returns something" here; verify the actual returned id string matches `Ids.Wire(...)`'s 32-hex,
  no-dashes shape, not Npgsql's default dashed `Guid.ToString()`.
- **The existence pre-pass must batch by target table, not run one query per id or per column.** A row
  with three relationship columns all pointing at the same target table should produce one `SELECT ...
  WHERE _id = ANY(@ids)` covering all three columns' ids, not three separate queries.
- **Don't couple the existence check to any permission check.** It's `SELECT 1 FROM target WHERE _id =
  ANY(@ids)` — no role resolution, no `QueryCompiler.CompilePermissionPredicate`. Linking to a row
  requires only that it exist; the research doc's "linking to a row requires only that it exist, not
  that the writer can read it" section explains why coupling these would be a real, novel
  cross-subsystem dependency, not a small addition.
- **`AssertRowBudgetAsync` runs on every column create, not just relationship ones** — if you add the
  `RowByteBudget` case *after* adding `Relationship` to `ColumnTypes.All`, even briefly, any column
  create on any table in your dev environment will start throwing. Land these together, or the
  `RowByteBudget` case first.

## Tests

`tests/Praxy.Tests.Unit/` and `tests/Praxy.Tests.Integration/` (real Postgres via Testcontainers):
- Unit: `ColumnTypesTests`/`RowByteBudgetTests`/`QueryCompilerTests` extended with a `Relationship`
  case each, matching the existing per-type test shape.
- Integration (new `RelationshipEngineTests.cs`, or extend an existing tables-engine test file):
  create two tables, add a scalar relationship column, confirm the real Postgres FK actually rejects
  a row insert referencing a nonexistent target id (`relationship_target_not_found`); confirm a valid
  reference succeeds and reads back correctly formatted; same for an array-valued relationship column
  (multiple ids, one missing → rejected, all present → succeeds); confirm a scalar relationship column
  plus a `unique` index behaves as a working one-to-one (second row can't reuse the same target id);
  confirm `equal`/`isNull`/array `contains` filtering works through the query DSL; confirm `search`
  against a relationship column is rejected with a clear error, not a silent wrong result.

## Done means

- `dotnet test` green (unit + integration, real Postgres).
- Console build clean (`tsc -b && vite build`).
- `dart analyze .` clean for `praxy_codegen`, and a real `dart run praxy_codegen` invocation against a
  table with a relationship column produces the expected `String`/`List<String>` accessor.
- **Owner test, actually run**: in the console, create two tables (e.g. `authors`, `posts`), add a
  scalar `relationship` column on `posts` targeting `authors`, create an author row, create a post row
  referencing it, confirm the post row reads back with the author's id in the expected wire format,
  attempt to create a post referencing a nonexistent author id and confirm a clean field error (not a
  crash), then add a `unique` index on the relationship column and confirm a second post can't target
  the same author.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/relationships-phase-1-report.md` and
  `docs/handoff/relationships-phase-2-prompt.md` (Phase 2's kickoff — delete-time integrity, per
  `docs/research/table-relationships.md`'s own Phase 2 scope).
