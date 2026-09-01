# Table relationships, Phase 2 (delete-time integrity) — report

**Status: complete.** Every item in `docs/handoff/relationships-phase-2-prompt.md`'s scope shipped, plus
one necessary fix beyond its literal text (see Deviations below — the `force=true` scenario the prompt's
own owner-test requires could not actually succeed without it). `dotnet test` green — **389 unit, 230
integration** (real Postgres via Testcontainers, 7 new tests for this phase, one of them a regression test
for the deviation). Console `tsc -b && vite build` clean, no console source changed. Owner-tested end to
end against the live local dev instance, reusing Phase 1's `Blog`/`Authors`/`Posts` setup.

## What shipped

**`Praxy.Tables`**:
- [`RowsService.cs`](../../src/Praxy.Tables/RowsService.cs) `DeleteAsync`: wraps the `DELETE ... RETURNING`
  in a catch for `PostgresException { SqlState: "23503" }`, translating the scalar FK violation to
  `row_referenced` (409) — the same established `PostgresException.SqlState` pattern this file's own
  `CreateAsync` already uses for `23505`. New `AssertNotArrayReferencedAsync`: queries
  `db.Columns.Where(c => c.TargetTableId == entry.Table.Id && c.IsArray)` for every array relationship
  column anywhere targeting this table, and runs one `SELECT EXISTS (... WHERE @rowId = ANY(col))` per
  column found (resolving each referencing column's own table via `CatalogCache.GetAsync`, since it may be
  a different table than the one being deleted, or the same one for a self-referential relationship). Runs
  before `ComputeReadRolesAsync`, so a delete that's about to be rejected never bothers computing roles.
  `AssertRelationshipTargetsExistAsync` (Phase 1) gained a null-check on `column.TargetTableId` — see
  Deviations.
- [`TablesService.cs`](../../src/Praxy.Tables/TablesService.cs) `DeleteAsync`: now queries
  `db.Columns.Where(c => c.TargetTableId == table.Id).Select(c => c.TableId).Distinct()` up front (reused
  for both the `!force` gate and post-delete cache invalidation — see Deviations). Without `force`, a
  non-empty result throws the new `relationship_dependency` (409) instead of falling through to the
  existing generic `general_force_required` (400) — the more specific error wins. With `force=true`, the
  pre-existing generic gate and the new one are both skipped entirely; delete proceeds exactly as before
  (`DROP TABLE ... CASCADE`, which already silently drops the scalar physical FK constraint on every
  referencing column). No `force`-bypass added to row deletes anywhere — row deletes never had one and
  don't start now.

**`Praxy.Core`**: two new error types in [`ErrorTypes.cs`](../../src/Praxy.Core/Errors/ErrorTypes.cs) —
`row_referenced` (409) and `relationship_dependency` (409) — both added to `All`, covered automatically by
the existing reflection-based coverage test.

**`Praxy.Persistence`**: migration `20260901032948_RelationshipColumnTargetSetNull` — see Deviations.

**Tests**: `tests/Praxy.Tests.Integration/RelationshipEngineTests.cs` extended (not a new file, reusing
Phase 1's `authors`/`posts` fixtures) with 7 new cases — the 6 the prompt asked for, plus one regression
test for the deviation below.

## Deviations & notes

**A genuine gap the prompt's own text didn't anticipate, found only by actually running the required
owner-test scenario, not by re-reading the design doc harder.** The prompt's Phase 2 scope and the design
doc both describe only the *physical* Postgres FK (`px_<db>.posts.authorId REFERENCES px_<db>.authors`)
that `DROP TABLE ... CASCADE` silently drops. But `ColumnDef.TargetTableId` *also* has its own FK at the
**metadata** level (`praxy.columns.target_table_id → praxy.tables.id`), added in Phase 1 with
`OnDelete(DeleteBehavior.Restrict)` specifically so that deleting a target table wouldn't silently
cascade-delete another table's column row. That FK is a normal Postgres RESTRICT/NO ACTION constraint —
there is no way for an application-level `force=true` flag to make Postgres skip it. So the very scenario
Phase 2's own "Done means" and owner-test require — `force=true` succeeding while a relationship column
still targets the table — was **structurally impossible** with the FK left as `Restrict`: `db.Tables.Remove
(table); db.SaveChangesAsync(ct)` would 500 on a raw `23503` from the metadata table, before the physical
`DROP TABLE` DDL even ran. This was caught by the first version of the new integration test
(`Deleting_the_authors_table_with_force_orphans_the_referencing_column`), not by re-reading the prompt.

Fixed by changing that FK's behavior from `Restrict` to `SetNull`
([`PraxyDb.cs:208`](../../src/Praxy.Persistence/PraxyDb.cs)) — a third option distinct from both `Restrict`
(blocks entirely) and `Cascade` (would delete the column row, explicitly rejected by Phase 1's own
in-code comment): SetNull clears the now-meaningless target reference while leaving the column and its
physical data alone, which is a direct, literal implementation of "orphaned, not auto-deleted." New
migration `20260901032948_RelationshipColumnTargetSetNull`.

This surfaced a second-order issue: `CatalogCache` caches a table's full `CatalogEntry` (including its
columns) for up to 5 seconds. `TablesService.DeleteAsync` was only invalidating the *deleted* table's own
cache slot, not the cache slots of tables that reference it. A referencing table's cached `ColumnDef` still
showed the old (now-stale) `TargetTableId` until that cache entry happened to expire — so a write to the
orphaned column shortly after a force-delete would try to validate against a target table id that no
longer exists, itself throwing `table_not_found`. Fixed by invalidating every referencing table's cache
entry too, using the same `referencingTableIds` query already computed for the `!force` gate. Also added a
null-check in `AssertRelationshipTargetsExistAsync` (`RowsService.cs`) so a write to an already-orphaned
column (`TargetTableId == null`) skips existence validation instead of throwing on `.Value` — there's
nothing left to validate against. A new regression test
(`Writing_to_an_orphaned_relationship_column_after_force_delete_does_not_crash`) covers both fixes
together; without either one it fails (first with a 500 on the table delete itself, then — after the
`SetNull` fix alone — with a stale-cache `table_not_found` on the follow-up write).

**`TablesService.DeleteAsync`'s dependency check switched from `AnyAsync` to a materialized `Select(...)
.Distinct().ToListAsync()`.** The prompt specified `AnyAsync` for the metadata check; this was widened
because the same query's *results* (not just its truth value) are needed for cache invalidation on the
force path. Semantically equivalent for the `!force` gate (`Count > 0` ⇔ `AnyAsync`); the list is small
(distinct referencing table ids, not rows) so there's no meaningful cost difference.

## Known gaps (out of scope, noted for whoever picks them up)

- **The array pre-check's inherent check-then-delete race is accepted, not fixed** — a `SELECT` then a
  `DELETE` with nothing holding a lock between them under read-committed isolation. Documented in the
  prompt's own non-goals; application-level enforcement, not a real constraint, unlike the scalar case.
- **No `?expand=`, no console search-picker** — both Phase 3, per the design doc.
- **No console UI for a table delete without `force`** — `TableSettingsPage`'s delete flow
  (`console/src/api/databases.ts`'s `useDeleteTable`) always sends `force=true` after its own typed-name
  confirm dialog, so `relationship_dependency`/`general_force_required` never surface through the console
  UI today — only through direct API calls (curl, SDKs). This is pre-existing Phase-0-era console behavior,
  unrelated to this phase; not something this prompt asked to change.

## Tests

`tests/Praxy.Tests.Integration/RelationshipEngineTests.cs` — 7 new cases, all against real Postgres via
Testcontainers:
- Deleting an author still referenced by a post's scalar `authorId` → `row_referenced` (409), confirming
  the new `23503` catch fires on the real FK violation (Phase 1's documented rough edge).
- Deleting an author still referenced only by a post's array `coAuthorIds` → `row_referenced` (409),
  confirming the new pre-check (isolated from the scalar case by using a different author for each).
- Deleting an author referenced by nothing → succeeds (204).
- Deleting the `posts` table (never anyone's relationship target) → the new gate does not fire; only the
  pre-existing generic `general_force_required` gate does, and `force=true` succeeds — confirms the
  dependency-direction check only fires for the actual target side.
- Deleting the `authors` table without `force` while `posts.authorId` still targets it →
  `relationship_dependency` (409), not the generic error — confirms the more specific error wins.
- Deleting the `authors` table with `force=true` while still targeted → succeeds; a follow-up read of the
  `posts` row shows `authorId` still present as a raw, now-dangling id — confirms orphaning, not silent
  cleanup.
- Writing a new value to the now-orphaned `authorId` column after the force-delete → succeeds (200), not a
  crash — regression test for the deviation above.

Full-repo `dotnet test`: **389 unit, 230 integration**, all passing.

## Commands

No new commands or config knobs. One new EF migration:
`20260901032948_RelationshipColumnTargetSetNull` (applied to the shared local dev instance during this
session's owner-test — `dotnet ef database update` from `src/Praxy.Persistence`, same as any other
migration per the README).

## Owner-test checklist

Done by me this session against the live local dev instance (`api`/`console` launch configs,
`owner@test.local`) — restarted the shared `api` process (it was running an older build) to pick up this
session's code, applied the new migration, then reused Phase 1's `Blog`/`Authors`/`Posts` setup rather
than recreating it:

- **Console**: opened the existing `Authors` table (still holding Ada Lovelace, referenced by Phase 1's
  `Ghost Post` in `Posts`) and clicked delete on her row — got a clean inline error, "This row is still
  referenced by another row's relationship column," not a crash.
- **Console**: created a new, unreferenced author row and deleted it — succeeded with a "Row deleted."
  toast.
- **API (curl, since this phase has no new console UI for the table-delete gates — `TableSettingsPage`
  always sends `force=true`)**: `DELETE .../tables/{authorsId}` without `force` → clean `409
  relationship_dependency` with a message naming the reason, not a crash and not the generic
  `general_force_required`.
- **API**: `DELETE .../tables/{authorsId}?force=true` → `204`. Read back the `Ghost Post` row afterward:
  `authorId` still present as the same raw, now-dangling id — not erroring, not silently cleaned up.
  Confirmed in the console too: `Authors` disappeared from the database sidebar, `Posts` remained, and its
  Columns tab showed `authorId`'s target badge gracefully falling back (`→ …`) instead of crashing on the
  now-missing target table.
- **API**: `PATCH`ed the same `Ghost Post` row's `authorId` to a brand-new id post-delete — succeeded
  (`200`), confirming the orphaned column keeps accepting writes rather than 500ing.
- Cleaned up: revoked the two temporary API keys created for this verification pass.

## Next

`docs/handoff/relationships-phase-3-prompt.md` — read-time expansion (`?expand=`) and the console
search-picker, per `docs/research/table-relationships.md`'s own Phase 3 scope.
