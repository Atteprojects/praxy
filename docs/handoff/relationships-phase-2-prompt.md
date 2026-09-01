# Session task — Table relationships, Phase 2 (delete-time integrity)

## Why this exists

Phase 1 shipped the `relationship` column type end to end — create, write with existence validation,
query — but deliberately left delete-time integrity as a documented rough edge: deleting a row or table
referenced by a relationship column either 500s (scalar, an unhandled raw Postgres FK violation) or
silently succeeds with no protection at all (array, no native FK possible). Read
`docs/research/table-relationships.md` in full before writing any code — its "Phase 2" section is the
complete design this prompt scopes from — and `docs/handoff/relationships-phase-1-report.md` for exactly
what Phase 1 built and the two deviations worth knowing about (`OPERATORS_BY_TYPE` had to become
`Partial`; `ColumnDef.TargetTableId`'s FK is `Restrict`, matching the codebase's explicit-`OnDelete`
style). This prompt assumes you've read both and doesn't re-explain what's already settled there. Work
on a new branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **No `?expand=`.** Still raw ids on read. Phase 3's job.
- **No console picker polish.** The plain text relationship input and the plain target-table `<select>`
  stay exactly as Phase 1 left them.
- **No attempt to close the array case's inherent race.** Row delete's array pre-check (below) runs a
  plain `SELECT`, then a `DELETE`, with nothing holding a lock between them — a row could gain a new
  array reference in that window under read-committed isolation. This is a documented, accepted
  limitation (it's application-level enforcement, not a real constraint, unlike the scalar case), not
  something to fix here.
- **No change to `DatabasesService.DeleteAsync`.** A table and everything that could reference it always
  live in the same `px_<database>` schema, so the existing `DROP SCHEMA ... CASCADE` already drops both
  sides of every relationship atomically. Confirm this by reading the method, don't add anything to it.

## Scope

1. **`RowsService.DeleteAsync` — scalar case.** Wrap the existing `DELETE FROM ... RETURNING _id`
   execution in a catch for `PostgresException { SqlState: "23503" }` (foreign_key_violation), same
   established pattern as this file's own `23505` catch in `CreateAsync` and the other `23505` catches
   across `ColumnsService.cs`/`IndexesService.cs`/`TablesService.cs`/`DatabasesService.cs` — six call
   sites total confirmed by direct grep before this doc was written, so this is a repeated convention,
   not a novel one. Translate to the new `row_referenced` (409). No `force` bypass — row deletes have
   never had one anywhere in this engine, and this doesn't start now; the caller unlinks or updates the
   referencing row(s) first, then deletes.
2. **`RowsService.DeleteAsync` — array case, a genuine pre-check.** Postgres can't enforce a constraint
   on array elements, so there's no free 23503 here. Before the DELETE runs, query the catalog for every
   *array* relationship column anywhere whose `TargetTableId` equals this table's id
   (`db.Columns.Where(c => c.TargetTableId == table.Id && c.IsArray)`), and for each one found, run
   `SELECT EXISTS (SELECT 1 FROM {referencing_table} WHERE @rowId = ANY({referencing_column}))` against
   that column's own table (resolve it via `CatalogCache.GetAsync`, same pattern Phase 1's
   `AssertRelationshipTargetsExistAsync` already established for resolving a target table by id). Any
   `true` result rejects with the same `row_referenced` (409) the scalar case uses — one error type,
   two causes, matching the design doc's framing exactly. This is the one query pattern in the entire
   phase that is genuinely new shape (an `EXISTS` against array membership), not a batched
   `ANY(@ids)` like Phase 1's existence pre-pass.
3. **`TablesService.DeleteAsync` — structural dependency gate.** Read the current method first:
   `force` is *already* unconditionally required for any table delete today (`if (!force) throw
   GeneralForceRequired`) — this is not new. The new behavior only changes what happens **without**
   force: check `await db.Columns.AnyAsync(c => c.TargetTableId == table.Id)` (a pure metadata check,
   exactly like `ColumnsService.DeleteAsync`'s existing `IndexDependency` check — no row data involved,
   just "does a relationship column exist that targets this table") *before* the existing force check.
   If it's `true`, throw the new `relationship_dependency` (409) instead of falling through to the
   generic `general_force_required` — a more specific, more informative error explaining *why* force is
   needed, not a second flag or a harder block. With `force=true`, delete proceeds exactly as it does
   today: `DROP TABLE ... CASCADE` already silently drops the scalar FK constraint on every referencing
   column elsewhere (verified: Postgres `CASCADE` on a referenced table drops the *constraint* on the
   referencing table, not the referencing table itself) — referencing columns are deliberately
   **orphaned, not auto-deleted**, matching this engine's "every destructive action is explicit"
   convention (auto-deleting a different table's column as a side effect of deleting this one would
   violate it). Do not add a second force-like flag; `force=true` is the one and only escape hatch, for
   both the pre-existing generic gate and this new one.
4. **New error types** (`src/Praxy.Core/Errors/ErrorTypes.cs`, both covered automatically by the
   existing reflection-based coverage test once added to `All`):
   - `row_referenced` (409) — delete: another row's relationship column (scalar FK-caught or array
     pre-checked) still points at this row.
   - `relationship_dependency` (409) — delete table: a relationship column elsewhere still targets it;
     needs `force=true`.

## Landmines — read before writing code

- **`ColumnsService.DeleteAsync`'s `IndexDependency` gate is *not* force-bypassable at all** — it blocks
  unconditionally; the caller must delete the dependent index first, full stop. Don't copy that
  bypass behavior for `relationship_dependency`: the design doc is explicit that a table delete *does*
  get the `force=true` escape hatch. The two gates share the same *shape* (a metadata existence check
  producing a distinct 409 before the generic destructive-change gate), not the same bypass semantics.
- **The array pre-check's `EXISTS` query needs the referencing column's own table resolved**, which may
  be a *different* table than the one being deleted (someone else's table has the array column) or, for
  a self-referential relationship, the *same* table. Resolve via `CatalogCache.GetAsync(column.TableId,
  ct)`, not by assuming it's always `entry`/`table`.
- **A table can have more than one relationship column targeting the table being deleted, from more
  than one other table.** The array pre-check must check every one of them (don't stop at the first
  match's table), and the structural `AnyAsync` check for table delete already handles "any number of
  columns" correctly since it's a plain existence query — don't overthink that one into a loop.
- **Row delete's `ComputeReadRolesAsync` call already happens before the `DELETE` executes** (captures
  roles before the row disappears, for the outbox event). The new array pre-check should run before that
  too — no point computing roles for a delete that's about to be rejected. Order: array pre-check →
  compute read roles → `DELETE` (with its own 23503 catch) → outbox write.
- **Don't add a `force` parameter to `RowsService.DeleteAsync`'s signature or its route.** Every other
  row-delete convention in this engine (and the design doc's explicit statement) says row deletes never
  get one; only the table-delete gate does.

## Tests

`tests/Praxy.Tests.Integration/` (real Postgres via Testcontainers) — extend
`RelationshipEngineTests.cs` from Phase 1 rather than starting a new file, since the fixtures
(`authors`/`posts`) already exist there:
- Deleting an author still referenced by a post's scalar `authorId` is rejected with `row_referenced`
  (409), not a raw 500 — confirms the new catch actually fires on the real FK violation Phase 1 left
  undocumented-but-expected.
- Deleting an author still referenced by a post's array `coAuthorIds` is rejected with `row_referenced`
  (409) — confirms the new pre-check, since there's no FK to catch here at all.
- Deleting an author no longer referenced by anything succeeds.
- Deleting the `posts` table while `authors` has no dependents on it succeeds without `force` needing to
  matter here (the dependency direction is `posts -> authors`, not the reverse) — a sanity check that
  the new table-delete gate only fires for the actual target side of the relationship.
- Deleting the `authors` table without `force` while `posts.authorId` still targets it is rejected with
  `relationship_dependency` (409), not the generic `general_force_required` — confirms the more specific
  error wins.
- Deleting the `authors` table *with* `force=true` while still targeted succeeds, and a follow-up read
  of a `posts` row shows `authorId` still present as a raw (now-dangling) id — confirms columns are
  orphaned, not silently cleaned up.

`tests/Praxy.Tests.Unit/`: no new unit-test surface expected — this phase is pure `RowsService.cs`/
`TablesService.cs` behavior against a real database, not a pure-function change like Phase 1's
`ColumnTypes`/`RowByteBudget`/`RowValues`/`QueryCompiler` additions.

## Done means

- `dotnet test` green (unit + integration, real Postgres).
- Console build clean (`tsc -b && vite build`) — no console changes expected this phase, but verify
  nothing else regressed.
- **Owner test, actually run**: in the console (or via `curl`, since this phase has no new console UI),
  reuse or recreate `authors`/`posts` from Phase 1's owner-test. Confirm: deleting a referenced author
  fails cleanly (not a crash); deleting an unreferenced author succeeds; deleting the `authors` table
  without `force` fails with a relationship-specific message; deleting it with `force=true` succeeds and
  leaves the `posts` row's `authorId` dangling rather than erroring or auto-cleaning.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/relationships-phase-2-report.md` and `docs/handoff/relationships-phase-3-prompt.md`
  (Phase 3's kickoff — `?expand=` and the console search-picker, per
  `docs/research/table-relationships.md`'s own Phase 3 scope).
