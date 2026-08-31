# Table relationships — design

## Context

Praxy's Tables engine has shipped nine column types since Phase 2 (`string`, `integer`, `float`,
`boolean`, `datetime`, `email`, `url`, `ip`, `enum`) with relationships explicitly deferred — every
design doc (`docs/architecture.md` §4.4, `docs/roadmap.md`) has said "deferred past v0.1.0, no date
set" since before v0.1.0 even shipped, and `src/Praxy.Tables/ColumnTypes.cs`'s own doc comment still
says so today. The question came up while comparing Praxy's column types against Appwrite's (which has
a real `relationship` attribute type with `oneToOne`/`oneToMany`/`manyToOne`/`manyToMany` and a
"two-way" reciprocal-attribute option) — the owner asked for a plan to close this gap. This is that
plan, in the same style as `docs/research/praxy-sites.md`: architecture plus a phased rollout, matching
how every other multi-part Praxy feature has shipped.

Grounded in three parallel research passes over the actual current code — the schema/DDL engine and
catalog metadata (`src/Praxy.Tables/TablesService.cs`/`ColumnsService.cs`/`IndexesService.cs`,
`src/Praxy.Persistence/Entities/Tables.cs`), the query compiler, row read/write path, and permission
model (`QueryDsl.cs`/`QueryCompiler.cs`/`RowValues.cs`/`RowsService.cs`, `src/Praxy.Auth/RoleResolver.cs`),
and the console column/row UI and Flutter codegen (`console/src/screens/ColumnsPage.tsx`/`RowsPage.tsx`,
`sdk/flutter/praxy_codegen/`) — followed by an independent stress-test pass that traced the concrete
DDL/SQL/permission interactions end-to-end and corrected two real gaps in the first draft before this
doc was finalized (noted inline below).

## Design decisions

**Relationship model: reuse the existing array-column mechanism — no `relationType` enum, no junction
tables, no automatic two-way.** A relationship column stores either a single `uuid` (`IsArray: false`,
the flag every column type already has) or a `uuid[]` (`IsArray: true`). This is a deliberate choice
over an Appwrite-style full model, made because it reuses infrastructure that already exists end-to-end
rather than adding a second storage shape (junction tables) and a third metadata concept (relation type)
on top of it:
- **Scalar** (`IsArray: false`) gets a **real Postgres FK constraint**:
  `REFERENCES <target>("_id") ON DELETE RESTRICT` — free DB-level integrity, the same proven pattern
  `TablesService.SetRowSecurityAsync`'s `__perms` table already uses. **One-to-one falls out for free**:
  a scalar relationship column plus the existing `unique` index type (`IndexesService.cs` has no type
  restriction on `key`/`unique` indexes today) — no code change needed in `IndexesService.cs` at all.
- **Array** (`IsArray: true`) gets a plain `uuid[]` column — Postgres cannot constrain array elements
  with a native FK, so integrity here is enforced at the application layer (Phase 2, below).
- No automatic reciprocal column on the target table. If an app wants "posts visible from a tag,"
  that's a second, independent relationship column the developer adds on the other table themselves.

**Read-time expansion is in scope, not deferred**: `?expand=<columnKey>[,...]` on list/get-row
endpoints embeds the related row's actual JSON in place of the raw id(s). Built as a **separate
enrichment pass** after the primary query runs — one batched `SELECT ... WHERE _id = ANY(@ids)` per
distinct target table, filtered through the *same* `QueryCompiler.CompilePermissionPredicate` read
check the target table's own list/get endpoints already use. This means `QueryCompiler.cs`'s
WHERE/filter-compilation logic itself never changes — expansion sits entirely in `RowsService`. A row
the caller can't read, a row that no longer exists, or a target table that was itself force-deleted all
fall back to the raw id rather than erroring the request or leaking data — three distinct causes, one
uniform fallback. Expansion is one level deep only; an expanded row's own relationship columns are
never recursively expanded.

**Metadata shape — corrected from the first-pass assumption**: unlike enum's `elements` (stuffed into
`ColumnDef.Options` jsonb, since nothing needs to query across it), a relationship column's target
table **must be queryable relationally** — Phase 2's "is this table referenced anywhere" delete-check
needs `WHERE TargetTableId = @tableId` to be a real, indexable column lookup, not a jsonb scan. So
`ColumnDef` gains a first-class `TargetTableId (Guid?)` column (FK to `TableDef`, same-database only),
not another `Options` entry.

**Same-database only.** Not a hard Postgres limitation (cross-schema FKs work fine in one cluster) —
the real reason is `CatalogEntry`/`CatalogCache`, the type nearly everything in the read/write/permission
path depends on, is built around "one table, one database, one round trip." Supporting a cross-database
target would mean that widely-depended-on type needs to carry a second database's schema-qualified name
for foreign targets — real scope, for a use case likely to be rare. Also has a pleasant emergent
consequence: since a table and everything that could reference it always live in the same
`px_<database>` schema, `DatabasesService.DeleteAsync`'s existing `DROP SCHEMA ... CASCADE` already
drops both sides of every relationship atomically — **no change needed there at all**.

**No default value on relationship columns.** A hardcoded default UUID pointing at one specific row
raises its own edge case (what happens when that row is later deleted, under `ON DELETE RESTRICT`?).
Rejected outright at column-create validation time, same place enum's `elements`-required check already
lives. Consequence: `ColumnTypes.FormatLiteral`/`FormatArrayLiteral` never need a `relationship` case at
all — smaller surface, no edge case to reason about.

**Linking to a row requires only that it exist, not that the writer can read it.** The write path
(`RowValues.cs`) is 100% synchronous today with zero DB I/O or permission awareness; permission
predicates are compiled and run only on read paths (`QueryCompiler.CompilePermissionPredicate`,
consumed by `GetAsync`/`ListAsync`/`UpdateAsync`/`DeleteAsync`'s row-scoping, never by anything in the
write-value-building path). Coupling "can I write this row" to "can I read the thing it points at"
would be a new, one-off cross-subsystem dependency nothing else in the engine has — existence-only
(`SELECT 1 FROM target WHERE _id = ANY(@ids)`, no role resolution) keeps write and read cleanly
separated, and stays consistent with expansion itself, which *is* a read and *does* apply the
permission predicate for exactly that reason.

**Row delete gets no `force` flag; table delete does, matching existing convention exactly.** `force`
in this engine has only ever gated destructive *schema* changes (dropping a table/column/database,
narrowing a type) — never a data operation. A row delete blocked by a relationship reference fails 409,
full stop; the caller unlinks or updates the referencing row(s) first, then deletes. A table delete
blocked by being someone's relationship target gets the familiar `force=true` escape hatch, mirroring
`ColumnsService.DeleteAsync`'s existing `IndexDependency` gate precisely.

## Data model

```
ColumnDef (existing table, new column + migration):
  target_table_id  uuid NULL   -- FK -> tables.id, same database only; NULL for every non-relationship column
```

```sql
-- scalar relationship column DDL:
ALTER TABLE {qualified} ADD COLUMN {quoted} uuid REFERENCES {target_qualified}("_id") ON DELETE RESTRICT;

-- array relationship column DDL (no native FK — Postgres can't constrain array elements):
ALTER TABLE {qualified} ADD COLUMN {quoted} uuid[];
```

Both are `ALTER TABLE ... ADD COLUMN` on a fresh, currently-empty column — instant, no rewrite, no
existing-row backfill — so relationship columns stay in the **synchronous** DDL path
(`ColumnsService.CreateAsync`, same transaction as the metadata write), never the async `schema_jobs`
queue. `PostgresType("relationship", ...)` returns `"uuid"`.

**Every exhaustive `switch` over column type needs a `Relationship` case or it 500s (not 400s) the
first time it's hit** — this is the concrete list, confirmed by direct code reading, not assumed:
- `ColumnTypes.PostgresType` → `"uuid"`
- `RowByteBudget.EstimateBytes` → **not optional**: `ColumnsService.CreateAsync` calls
  `AssertRowBudgetAsync` unconditionally on every column create (re-estimates every existing column
  plus the new one) — miss this and creating the *first* relationship column on any table with
  existing columns throws immediately. 16 bytes scalar (uuid width); array capped like every other
  array type.
- `RowValues.ToScalar`/`ToWriteValue`'s array branch → parse a wire id string (or array of them) via
  `Ids.TryParseWire`, same helper `QueryCompiler`'s `$id`/`IdType` handling already uses.
- `RowValues.ReadScalar`/`ReadArray` → **needs an explicit case, not the string-fallback default**.
  Npgsql maps Postgres `uuid` to `Guid` natively; the existing default branch's
  `reader.GetFieldValue<string>(...)` would format it as Npgsql's own dashed representation, not
  Praxy's 32-hex-no-dashes wire format. Read as `Guid`, format with `Ids.Wire(...)` — keeps
  relationship ids shaped identically to `$id` everywhere else in the API.
- `ColumnTypes.FormatLiteral`/`FormatArrayLiteral` → **no case needed** (see "no default value," above).

## Phased rollout

Three phases — each independently useful and demoable, not an arbitrary slice:

- **Phase 1 — the primitive**: `ColumnTypes.Relationship`, `ColumnDef.TargetTableId` + migration,
  scalar/array DDL (including free one-to-one via the existing `unique` index), write-time existence
  checking (new async pre-pass ahead of the existing synchronous per-field builder, batched one query
  per distinct target table — the existing `SchemaDdl.InTransactionAsync` wrapper `CreateAsync`/
  `UpdateAsync` already run inside makes this a same-shape addition, not a rework), basic query support
  (`equal`/`notEqual`/`isNull`/`isNotNull`/array `contains` — no new `QueryCompiler.cs` structure needed
  once `RowValues`'s filter-scalar conversion has a relationship case), a plain `<select>` target-table
  picker in `ColumnsPage.tsx` (target immutable after creation, matching `size`/`elements` today), a
  plain text row-id input in `RowsPage.tsx` (server validates on save), and the two-line Flutter codegen
  passthrough (`'relationship' => 'String'`/`List<String>`, raw id — ships alongside the type since it's
  trivial, not held back). New error type: `relationship_target_not_found` (400).
  **Non-goals**: no `?expand=`, no delete-blocking (a blocked scalar delete still 500s via the raw FK
  violation until Phase 2 catches it cleanly — a documented rough edge, not a crash risk to data), no
  search-picker UI, no typed cross-table codegen, no cross-database relationships.

- **Phase 2 — delete-time integrity**: catch the scalar FK's `23503 foreign_key_violation` in
  `RowsService.DeleteAsync` (same established pattern as this codebase's existing `23505`
  unique-violation catches in `RowsService.cs`/`ColumnsService.cs`/`TablesService.cs`/etc.) and
  translate to `row_referenced` (409); a new array-case pre-check (one `EXISTS` query per array
  relationship column elsewhere targeting this table) for the same error — documented as accepted
  check-then-delete race exposure under read-committed isolation, since it's app-level enforcement, not
  a real constraint, unlike the scalar case. `TablesService.DeleteAsync` gains a `relationship_dependency`
  (409) gate mirroring `ColumnsService.DeleteAsync`'s existing `IndexDependency` check exactly —
  `force=true` bypasses it, and on force the existing `DROP TABLE ... CASCADE` already silently drops
  the scalar FK constraint; referencing columns are deliberately **orphaned, not auto-deleted** (a
  relationship column is its own user-visible, independently-managed resource — auto-deleting it as a
  side effect of deleting a different resource breaks the "every destructive action here is explicit"
  convention the whole engine follows). `DatabasesService.DeleteAsync` needs **no change** (see
  "same-database only," above).
  **Non-goals**: no `?expand=`, no console picker polish, no attempt to close the array case's inherent
  race (documented limitation, not fixed).

- **Phase 3 — read-time expansion + console ergonomics**: `?expand=` on list/get-row endpoints as
  designed above; replaces Phase 1's plain text row-id input with a real search-as-you-type picker
  modeled on `console/src/components/RolePicker.tsx`'s proven portal-popover-with-search shape (its
  structure, not its role-specific content) — searching by `$id` prefix in this phase, since there's no
  existing "which field represents a row to a human" concept in Praxy today and inventing one is a
  real, separate feature worth its own future decision, not something to improvise here. Grid/row-sheet
  rendering needs no new component (`RowSheet` already only shows raw JSON; an expanded value is just
  richer raw JSON). `OPERATORS_BY_TYPE` gains a `relationship` entry matching exactly what the query
  compiler supports (`equal`/`notEqual`/`isNull`/`isNotNull` always, `contains` only when array — never
  `startsWith`/`endsWith`, meaningless on uuids). No new error type — `?expand=` validation errors reuse
  `general_query_invalid`.
  **Non-goals**: no configurable per-table "display field," no realtime changes anywhere
  (`ChannelGrammar.cs` stays untouched across all three phases — a row event on table A never fans out
  to table B's subscribers, by design), no recursive expand, no typed cross-table Dart codegen.

**Explicitly out of scope for this entire phase sequence**, not just deferred within it: typed
cross-table Flutter codegen (`praxy_codegen`'s `generate()` has zero multi-table-graph awareness today
— a real architecture change to that tool, not a relationships-feature deliverable; sits alongside
Storage/TOTP/multi-org as its own future initiative if ever wanted).

## New error types (all three, `src/Praxy.Core/Errors/ErrorTypes.cs`)

| Type | HTTP | Phase | Meaning |
|---|---|---|---|
| `relationship_target_not_found` | 400 | 1 | Create/update: a relationship value doesn't point at an existing row in the target table. |
| `row_referenced` | 409 | 2 | Delete: another row's relationship column (scalar FK-caught or array pre-checked) still points at this row. |
| `relationship_dependency` | 409 | 2 | Delete table: a relationship column elsewhere still targets it; needs `force=true`. |

All covered automatically by the existing reflection-based error-type coverage test once added to
`ErrorTypes.All` — no test-infrastructure change needed.

## Verification

This doc's own claims were checked against the actual current code, not assumption or memory,
including a direct grep confirming the `23505`/`PostgresException.SqlState` constraint-violation-catching
pattern Phase 2's design depends on is a real, repeated, existing convention (six call sites across
`DatabasesService.cs`/`ColumnsService.cs`/`IndexesService.cs`/`TablesService.cs`/`RowsService.cs`), not
a novel pattern being introduced.

The Phase 1 implementation session (kickoff: `docs/handoff/relationships-phase-1-prompt.md`) owns its
own verification: `dotnet test`, the console owner-test click-through, and a real end-to-end
relationship (create two tables, link a row, query by the relationship column, confirm the FK actually
rejects a dangling reference) against a real Postgres instance — the same discipline every prior phase
used.
