# Session task — Geo columns and `near` queries, Phase 1 (the primitive)

## Why this exists

The owner asked for geo operations, starting with "nearby" queries. Read `docs/research/geo-nearby.md`
in full before writing any code — it's the complete architecture (why this needed real research and
isn't just "another column type like relationships": the first Postgres extension this codebase has
ever loaded, the first non-btree/GIN index, the first column whose value isn't a bare JSON scalar, why
no new .NET package is needed, why distance-sorting is explicitly out of scope). This prompt assumes
you've read it and doesn't re-explain what's already settled there. Work on a new branch off `main`.
Read `CLAUDE.md` first.

This is Phase 1 of what's likely a longer sequence (see `docs/roadmap.md`'s "Geo columns and `near`
queries" section) — but unlike relationships, later phases aren't designed yet, because the owner asked
for "nearby" specifically as the starting point, not the whole geo surface. Ship the primitive well;
don't design Phase 2 speculatively.

## Non-goals — do not build these

- **No automatic distance-sorting.** `near` is a pure radius filter. Results come back in whatever order
  the query's own `orderAsc`/`orderDesc`/default sort produces — never automatically nearest-first. The
  design doc explains why this needs real surgery to the sort/cursor model, not a quick add: don't
  attempt it here.
- **No array-valued geo columns.** `geo` columns are scalar only in this phase. Reject `isArray: true`
  for a `geo` column at validation time, same place other type-specific validation lives.
- **No `line`/`polygon` types, no `within`/bounding-box operators.** Just the point type and `near`.
- **No default value on geo columns.** Reject `default` outright at column-create validation time for
  `type == "geo"` — keeps this phase's surface smaller; revisit later if actually wanted.
- **No new .NET package.** Confirmed in the design doc: every column type already reads/writes via plain
  `NpgsqlParameter`/`GetFieldValue<T>` scalars, never a strongly-typed mapped object (no EF/NetTopologySuite
  anywhere in this engine). Geo reaches PostGIS through plain SQL function calls
  (`ST_MakePoint`/`ST_DWithin`/`ST_X`/`ST_Y`) with `double` parameters — don't add
  `Npgsql.NetTopologySuite` or any spatial .NET library; you don't need it.

## Scope

1. **Postgres image swap.** `deploy/docker-compose.yml`'s `postgres:17-alpine` and
   `tests/Praxy.Tests.Integration/Infrastructure/PostgresContainerFixture.cs`'s Testcontainers image
   both need to become a PostGIS-flavored image (e.g. `postgis/postgis:17-3.4-alpine` — **verify and pin
   the exact current tag yourself**, per `docs/research/dotnet-stack.md`'s standing "don't trust memory,
   verify" discipline; this design doc did not do that verification). `deploy/backup.sh`/`restore.sh` need
   no change (they address the `postgres` service by name, never the image tag — confirmed by the design
   doc's own grep). Document the upgrade sequence for an *existing* self-hosted instance in
   `docs/self-host.md`'s Upgrading section (same image, same Postgres major version, same data volume —
   `docker compose up -d --build` recreates the container against the existing volume, `CREATE EXTENSION
   postgis` runs once via the normal migration path on next `api` startup).
2. **`CREATE EXTENSION IF NOT EXISTS postgis`** as a raw-SQL step in a new EF migration (from
   `src/Praxy.Persistence`) — runs through the existing `CatalogMigrator` startup path (the same
   `pg_advisory_lock`-guarded sequence every other migration already uses; no new mechanism).
3. **`ColumnTypes.cs`**: `Geo = "geo"`, added to `All`. `PostgresType("geo", ...)` returns
   `"geography(Point, 4326)"`.
4. **`RowByteBudget.EstimateBytes`** — add the `Geo` case. **Do this before anything else that touches
   columns**, same landmine relationships hit: `ColumnsService.CreateAsync` calls
   `AssertRowBudgetAsync` unconditionally on every column create. Confirm the actual on-disk size of a
   `geography(Point)` value against a real column (don't guess) before picking the byte estimate.
5. **Column DDL** (`ColumnsService.cs`): `ALTER TABLE {qualified} ADD COLUMN {quoted} geography(Point, 4326)`
   — a fresh, empty column, stays in the existing synchronous DDL path, no `schema_jobs` involvement.
   Reject `isArray`/`default` for `type == "geo"` at validation time (same place enum's
   `elements`-required check and relationships' target/default checks already live).
6. **New `IndexDef.Type` value, `"spatial"`** (alongside the existing `key`/`unique`/`fulltext`):
   `CREATE INDEX {quoted} ON {qualified} USING GIST ({column})`. Look at how `fulltext` indexes are
   created (`IndexesService.cs`/`SchemaJobRunner.cs`'s `create_index` job kind and its
   `CreateIndexJobPayload`) — a spatial index is DDL-shape-similar (async via the job queue, since GiST
   index creation on an existing table with rows should use `CREATE INDEX CONCURRENTLY` the same way
   other indexes do), not sync like the column DDL itself.
7. **Write path** (`RowValues.cs`/`RowsService.cs`'s value-building code): parse `{"lat": <number>,
   "lng": <number>}` and emit a parameterized `ST_MakePoint(@{pName}_lng, @{pName}_lat)::geography` in
   place of the generic single-`@{pName}` form every other type uses today — **this is a real deviation
   from the existing single-parameter plumbing, not a drop-in extra `switch` case**; trace exactly where
   `RowsService.cs` builds `{physicalName} = @{pName}` for `INSERT`/`UPDATE` and give geo its own
   two-parameter branch there.
8. **Read path**: `SelectColumns` (`RowsService.cs`) needs a geo-specific branch selecting
   `ST_X({col}::geometry) AS {col}_lng, ST_Y({col}::geometry) AS {col}_lat` (or equivalent) instead of
   the bare column every other type selects — `RowValues.ReadScalar` alone isn't enough, since geo needs
   *two* result columns from *one* declared column, unlike every existing type's one-to-one reader
   mapping. Reassemble into `{"lat": ..., "lng": ...}` JSON in `BuildRowJson`.
9. **`near(lat, lng, radiusMeters)` query operator**:
   - `QueryDsl.cs`: add `"near"` to `AllMethods` and its arity rule (`count == 3`).
   - `QueryCompiler.cs`'s `Builder.CompileFilterNode`: a new case, gated on `column.Type ==
     ColumnTypes.Geo` (throw a clear error otherwise — reuse the pattern `CompileSearch`'s type-gating
     already establishes). Compiles to `ST_DWithin({colSql}, ST_MakePoint(@lng, @lat)::geography,
     @radiusMeters)` — three independent `AddParam` calls, the same shape `between`'s two-value handling
     already establishes as precedent (`q.Values[0]`/`[1]`/`[2]`, each parsed as a plain `double`, *not*
     run through `RowValues.ToFilterScalar`/`ConvertValue` — those convert a value *of the column's own
     type*, and lat/lng/radius aren't that).
   - **Require a `spatial` index on the column** before allowing `near` — mirror `CompileSearch`'s
     `entry.FulltextIndexFor(column.Key)` check exactly, with an equivalent `SpatialIndexFor` lookup;
     reject with a clear, actionable error (which index is missing, how to add one) if none exists. Do
     not let `near` silently sequential-scan.
10. **API DTOs**: thread the `geo` type and its lat/lng wire shape through wherever column/row
    request/response records live, same pattern relationships' `TargetTableId` threading already used.
11. **Console**: a `geo` type option in `ColumnsPage.tsx`'s `CreateColumnSheet` (no target/size/elements/
    default fields — just the type selector itself). `RowsPage.tsx`'s row editor gets two number inputs
    (lat, lng) for a geo column's value, replacing the plain text fallback. A `spatial` badge/indicator on
    the Indexes screen alongside the existing `key`/`unique`/`fulltext` badges.
12. **Flutter codegen**: `_dartType('geo', isArray)` — pick a pragmatic v1 representation (a raw
    `Map<String, dynamic>`-shaped passthrough is defensible and consistent with relationships' own
    "don't over-invest in typed codegen this phase" precedent; a small dedicated `GeoPoint`-shaped class
    is also defensible if it's cheap — your call, state which you chose and why in the report).

## Landmines — read before writing code

- **The write-path parameter shape is genuinely different, not just a new switch arm.** Every existing
  column type's `INSERT`/`UPDATE` SQL-building code assumes "one column, one parameter." Geo is "one
  column, two parameters, wrapped in a function call." Find every place that assumption is baked in
  before you start, not after something breaks.
- **The read-path is the same problem in reverse** — one column needs two SELECTed expressions. Don't
  try to force this through `RowValues.ReadScalar`'s existing one-value-per-column signature; it needs a
  companion change in how the SELECT list itself is built (`SelectColumns`).
- **`near` without a spatial index must be rejected, not silently slow.** This is a hard requirement, not
  a nice-to-have — an unindexed `ST_DWithin` on a large table is a real production footgun, and this
  engine has an established, working precedent (fulltext search) for refusing exactly this class of
  mistake instead of accepting it.
- **PostGIS extension privileges.** Confirm the Postgres role Praxy connects as (`praxy` in the self-host
  compose) can actually run `CREATE EXTENSION postgis` — typically fine if it owns the `praxy` database,
  but verify against a real container rather than assuming, and say so in the report either way.
- **A spatial index is async (job queue), the column itself is sync** — don't conflate the two DDL paths.
  Creating a `geo` column should feel instant, same as every other column type; creating the spatial
  index on it goes through `schema_jobs` like `fulltext`/`unique` index creation already does.

## Tests

`tests/Praxy.Tests.Unit/` and `tests/Praxy.Tests.Integration/` (real PostGIS-enabled Postgres via
Testcontainers — confirm the swapped fixture image actually has PostGIS *before* writing tests against
it, don't assume the image swap alone was sufficient):
- Unit: `ColumnTypesTests`/`RowByteBudgetTests` extended with a `Geo` case; `QueryDsl` arity validation
  for `near` (exactly 3 values, not 2 or 4).
- Integration (new test file or extend an existing tables-engine one): create a `geo` column, write a
  point, read it back and confirm the lat/lng round-trips correctly (watch for precision loss); create
  two rows at known, real-world coordinates a known real distance apart; `near` with a radius that
  includes one and excludes the other, confirmed against the actual real-world distance, not a made-up
  number; `near` against a column with no spatial index is rejected with a clear error, not a slow silent
  scan; creating a spatial index actually settles from `processing` to `available` the same way a
  fulltext/unique index does.

## Done means

- `dotnet test` green (unit + integration, real PostGIS-enabled Postgres).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run**: in the console, create a table with a `geo` column, add two rows at real
  coordinates you can verify by eye (e.g. two landmarks a known distance apart), add a spatial index,
  wait for it to settle to `available`, run a `near` query with a radius that should include one row and
  exclude the other, confirm the result matches — then attempt `near` against a column with no spatial
  index and confirm a clean, actionable error, not a hang or a crash.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/geo-nearby-phase-1-report.md`. Whether a Phase 2 is warranted (distance-sorting,
  array support, more geo types) is the owner's call, not something to prompt for automatically — the
  design doc deliberately didn't design one.
