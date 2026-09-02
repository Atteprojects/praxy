# Geo columns and `near` queries, Phase 1 (the primitive) — report

**Status: complete.** Every item in `docs/handoff/geo-nearby-phase-1-prompt.md`'s scope shipped.
`dotnet test` green: **394 unit, 250 integration** (real PostGIS-enabled Postgres via Testcontainers,
including 12 new tests for this feature). Console `tsc -b && vite build` clean. `dart test` clean for
`praxy_codegen` (new `geo` codegen case). Owner-tested end-to-end against a fresh local instance
(this session's own scratch instance, not the shared canonical dev one — see
[Owner-test checklist](#owner-test-checklist)): created a `geo` column, wrote/read rows through both
the grid's inline editor and the Create Row sheet, created a `spatial` index and watched it settle,
and confirmed a real end-to-end `near` query via the integration suite's real-coordinate assertions.

## What shipped

**Infrastructure — the Postgres image swap.** `deploy/docker-compose.yml` and
`tests/Praxy.Tests.Integration/Infrastructure/PostgresContainerFixture.cs` both moved from
`postgres:17-alpine` to `postgis/postgis:17-3.6-alpine` (verified against a real pull and a real
`CREATE EXTENSION postgis` — see [Landmine found](#landmine-found-arm64) below, not the tag the
design doc guessed at `17-3.4-alpine`). `deploy/backup.sh`/`restore.sh` needed no change, confirmed —
they address the `postgres` service by name via `docker compose exec`, never the image tag.
`docs/self-host.md` gained a new Requirements bullet and an Upgrading callout with the exact upgrade
sequence and the arm64 caveat.

**`Praxy.Persistence`**: one new EF migration, `20260902022849_EnablePostGis` — a raw-SQL
`CREATE EXTENSION IF NOT EXISTS postgis;` (`Down`: best-effort `DROP EXTENSION IF EXISTS postgis;`),
running through the existing `CatalogMigrator` startup path, no new mechanism. No entity/model change
at all — `geo`/`spatial` are just another string value for `ColumnDef.Type`/`IndexDef.Type`, already
wide enough (`varchar(32)`/`varchar(16)`).

**`Praxy.Tables`** — the primitive:
- [`ColumnTypes.cs`](../../src/Praxy.Tables/ColumnTypes.cs): `Geo = "geo"` added to the type constants
  and `All`; `PostgresType` returns `"geography(Point, 4326)"`. No `FormatLiteral`/`FormatArrayLiteral`
  case — defaults and arrays are both rejected before either would ever be called with this type.
- [`RowByteBudget.cs`](../../src/Praxy.Tables/RowByteBudget.cs): `Geo => 32`. Measured against a real
  `postgis/postgis:17-3.6-alpine` container rather than guessed: `pg_column_size()` of a real
  `geography(Point,4326)` value is **29 bytes**; 32 is the rounded-up estimate.
- [`ColumnsService.cs`](../../src/Praxy.Tables/ColumnsService.cs): `ValidateTypeShape` gained an
  `isArray` parameter and rejects `array: true` and any `default` outright for `type == "geo"`
  (`general_argument_invalid`, same place enum's `elements`-required check and relationships' own
  target/default checks already live).
- [`RowValues.cs`](../../src/Praxy.Tables/RowValues.cs): a new `GeoPoint(double Lat, double Lng)`
  record. `ParseGeoPoint` validates `{"lat","lng"}` — object shape, numeric fields, range-checked
  app-side (`lat` ∈ [-90,90], `lng` ∈ [-180,180]) so an out-of-range coordinate is the same clean
  per-field 400 every other type's validation produces, not a raw Postgres error from the `geography`
  cast. `ToScalar`'s `Geo` case returns a `GeoPoint` — used by the write path via `ToWriteValue`.
  `ToFilterScalar` explicitly rejects `Geo` columns before delegating to `ToScalar` — the query
  compiler's generic operators (`equal`, `between`, etc.) would otherwise try to bind a `GeoPoint`
  as an Npgsql parameter and blow up with an unhandled type error; `near` (the only filter that makes
  sense on a geo column) bypasses this method entirely, per the design doc.
- [`RowsService.cs`](../../src/Praxy.Tables/RowsService.cs) — the real surgery the design doc flagged:
  - **Write path**: a new `ValuePlaceholder` helper, shared by `CreateAsync`'s INSERT-column loop and
    `UpdateAsync`'s SET-clause loop. Every type but geo still binds `@{pName}` to one parameter; a
    `GeoPoint` value binds `ST_MakePoint(@{pName}_lng, @{pName}_lat)::geography` with two parameters
    instead — genuinely different from every other type's plumbing, not a new `switch` arm on the
    existing shape.
  - **Read path**: a new `GeoAwareColumnExpr` helper, used by both `SelectColumns` (`Get`/`Expand`) and
    `ReturningColumns` (`Create`/`Update`'s `RETURNING`) — a geo column expands into two SELECTed
    expressions (`ST_X(col::geometry) AS col_lng, ST_Y(col::geometry) AS col_lat`) instead of the bare
    column every other type selects. `BuildRowJson`'s per-column loop now advances its result-set
    ordinal by 2 (not 1) for a geo column and reassembles the pair via a new `ReadGeoPoint` helper into
    `{"lat":...,"lng":...}` JSON — `RowValues.ReadValue`'s one-ordinal-per-column signature couldn't
    express this, so it isn't used for geo at all.
  - `QueryCompiler.cs`'s `BuildSelectList` (the separate SQL-building path `List` uses) needed the
    identical two-expression treatment via its own `GeoAwareColumnExpr`, kept independent of
    `RowsService`'s copy since `List`'s SQL is built entirely in the compiler.
- [`QueryDsl.cs`](../../src/Praxy.Tables/QueryDsl.cs): `"near"` added to `AllMethods` and
  `ValidateArity` (`count == 3` — lat, lng, radiusMeters).
- [`QueryCompiler.cs`](../../src/Praxy.Tables/QueryCompiler.cs): `CompileFilterNode` gained a `"near"`
  case → `CompileNear`, gated on `column.Type == ColumnTypes.Geo` (a clean `general_query_invalid`
  otherwise, mirroring `CompileSearch`'s type-gating). Requires an available `spatial` index on the
  column via a new `CatalogEntry.SpatialIndexFor` (mirrors `FulltextIndexFor` exactly) — rejected with
  a clear, actionable error naming the missing index if none exists. Compiles to
  `ST_DWithin(col, ST_MakePoint(@lng, @lat)::geography, @radiusMeters)`, three independent `AddParam`
  calls (the `between` precedent for multi-value methods), each value parsed as a plain `double` —
  never routed through `RowValues.ToFilterScalar`/`ConvertValue`, per the design doc.
- [`IndexesService.cs`](../../src/Praxy.Tables/IndexesService.cs): new `TypeSpatial = "spatial"` added
  to `Types`. `ResolveIndexColumns` requires exactly one column for a spatial index and requires that
  column be `geo`-typed (`index_invalid` otherwise) — a spatial index wraps one geography column in
  `ST_DWithin`, no multi-column composite shape the way fulltext concatenates text columns. `Orders` is
  `[]` for spatial, same as fulltext (no per-column direction concept for a GiST index).
  `CreateIndexJobPayload` gained a `Spatial` bool.
- [`SchemaJobRunner.cs`](../../src/Praxy.Tables/SchemaJobRunner.cs): `ExecuteCreateIndexAsync` gained a
  `payload.Spatial` branch — `CREATE INDEX CONCURRENTLY IF NOT EXISTS ... USING GIST (col)`. Same async,
  job-queue DDL path every other index already uses (never the synchronous column-DDL path); a spatial
  index on an existing table with rows correctly used the GiST index rather than a sequential scan,
  confirmed via `EXPLAIN` against a real container during verification.
- `CompileSearch`'s type-gate now also excludes `Geo` (alongside the pre-existing `Relationship`
  exclusion) — without it, `search` on a geo column fell through to "no fulltext index" instead of the
  more direct "search isn't supported on this attribute," a confusing-but-not-crashing rough edge found
  by re-reading the existing exclusion list rather than by a failing test.
- **`PhysicalNaming.EntityName` gained a `reserveSuffixChars` parameter, used only for geo columns.**
  Found by re-checking the physical-naming budget math, not by a failing test: a geo column's read
  path derives `{physicalName}_lng`/`{physicalName}_lat` aliases on top of the column's own physical
  name (`GeoAwareColumnExpr`), but `PhysicalNaming`'s existing budget already lets a max-length
  (64-char) column key produce a physical name that fills the entire 63-char Postgres identifier
  budget on its own — leaving zero room for a further `_lng`/`_lat` suffix. Undetected, this would
  throw `PhysicalNaming.Quote`'s own length guard on the *first row write or read* against such a
  column (not at column creation, since column DDL itself never builds the alias) — `ColumnsService.
  CreateAsync` now reserves 4 extra characters when generating a geo column's physical name.
  Regression test: `GeoEngineTests.A_max_length_key_leaves_room_for_the_lat_lng_alias_suffix`.
  While looking at this, noticed the *same* class of bug likely also affects fulltext indexes
  (`PhysicalNaming.IndexName` + `FulltextColumnName`'s `__fts` suffix, 5 chars) — pre-existing,
  unrelated to this phase, not fixed here; flagged as a follow-up task rather than expanding this
  phase's scope.

**`Praxy.Api`**: no DTO changes at all — `CreateColumnRequest`/`ColumnResponse` were already fully
type-agnostic (no per-type fields beyond what geo doesn't need: size/elements/target/default), and
`type` comes from the existing `/columns/{type}` route segment. `docs/openapi/v1.json` needed no
regeneration — confirmed by `OpenApiDocumentTests.The_committed_snapshot_matches_what_the_code_generates`
passing unchanged.

**Console**:
- `api/types.ts`: `COLUMN_TYPES` gains `"geo"`, `INDEX_TYPES` gains `"spatial"`, new `GeoPoint`
  interface.
- `ColumnsPage.tsx`: `geo` type option in `CreateColumnSheet` — selecting it hides Array, Default, and
  (already-hidden-for-non-string/enum/relationship) Size/Elements/Target, leaving just Type/Key/Required,
  exactly the "no target/size/elements/default fields" scope asked for. `TYPE_LABEL` gains `GEO`.
- `RowsPage.tsx`: a new `GeoValueEditor` (two `number` inputs) replaces the plain-text fallback for a
  `geo` column, wired into both the grid's always-live inline `EditableCell` and the `CreateRowSheet`.
  `formatCell` gained a `geo`-point preview (`lat, lng`) so the grid doesn't fall through to the
  relationship preview's `$id`-shaped-object assumption and print `[object Object]`.
- `IndexesPage.tsx`: `TYPE_TONE` gains `spatial: "amber"` (distinct from key/unique/fulltext); the
  per-column order-arrow display and the orders-on-submit logic both extended to treat `spatial` the
  same as `fulltext` (no per-column direction for either).

**Flutter**: `praxy_codegen/lib/src/generator.dart`'s `_dartType` maps `'geo'` to
`'Map<String, dynamic>'` — a raw passthrough, not a dedicated `GeoPoint` class. Chosen because geo has
no array support this phase (so `isArray` is always false for it in practice) and a typed class would
be pure ceremony for a field whose only two consumers this phase are "read it back" and "write it
back" — the same "don't over-invest in typed codegen this phase" call relationships' own codegen made.

## Landmine found: arm64 {#landmine-found-arm64}

Not anticipated by the design doc, found by actually pulling the image rather than trusting the tag
name: **`postgis/postgis` publishes no `arm64` manifest at all**, checked across every current
Postgres-17/PostGIS-minor combination (`17-3.4[-alpine]`, `17-3.5[-alpine]`, `17-3.6-alpine`) — `amd64`
only. A plain `docker run`/Testcontainers pull with no platform hint fails outright on an arm64 host
(this was written and verified on one) with `no matching manifest for linux/arm64/v8`.

Fixed in both places that reference the image:
- `deploy/docker-compose.yml`'s `postgres` service now pins `platform: linux/amd64` explicitly, so an
  arm64 self-host host (or an arm64 Docker Desktop) runs it under emulation instead of hard-failing —
  documented in `docs/self-host.md` as needing `qemu-user-static`/binfmt emulation on a bare arm64
  Linux server (Docker Desktop bundles this; a raw Linux install typically doesn't).
- `PostgresContainerFixture.cs`'s Testcontainers builder now constructs the image via
  `new DockerImage("postgis/postgis:17-3.6-alpine", new Platform("linux/amd64"))` (a `PostgreSqlBuilder`
  constructor overload accepting `IImage`, available in the installed Testcontainers.PostgreSql 4.14.0)
  instead of the plain `PostgreSqlBuilder(string image)` constructor — otherwise every contributor on
  an arm64 dev machine would hit the same hard failure running `dotnet test`.

## Landmine found: geo columns needed a real seam in the write/read plumbing

Confirmed, not just anticipated: every other column type's write path assumes "one column, one bound
parameter," and every other type's read path assumes "one column, one selected expression, one
`RowValues.ReadValue` call." Both assumptions are baked into `RowsService.cs` in several places
(the INSERT column/placeholder loop, the UPDATE SET-clause loop, `SelectColumns`, `ReturningColumns`,
`BuildRowJson`'s ordinal-advancing loop) and into `QueryCompiler.cs`'s separate `BuildSelectList`. Each
one needed its own geo-aware branch — see the `RowsService.cs`/`QueryCompiler.cs` bullets above. This
matched the design doc's warning almost exactly; the only correction found in practice was that
`QueryCompiler.BuildSelectList` needed the identical treatment independently, since `List`'s SQL never
goes through `RowsService.SelectColumns` at all (a detail the design doc didn't call out by name).

## Tests

`tests/Praxy.Tests.Unit/`:
- `ColumnTypesTests.cs`: `Geo` is registered and backed by `geography(Point, 4326)`.
- `RowByteBudgetTests.cs`: the 32-byte estimate, with the measured 29-byte real figure in the comment.
- `QueryDslTests.cs`: `near` requires exactly 3 values (2 and 4 both rejected); a valid 3-value `near`
  parses.

`tests/Praxy.Tests.Integration/GeoEngineTests.cs` (new, real PostGIS-enabled Postgres via
Testcontainers) — 12 tests:
- Lat/lng round-trips through create and a separate read, precision-checked to 9 decimal places.
- A 64-char (`Keys.MaxLength`) column key leaves enough room in the physical name for the read
  path's `_lng`/`_lat` alias suffix — the regression test for the `PhysicalNaming` fix above.
- A null geo value round-trips as `null`.
- Updating a geo value persists the new point.
- `array: true` and any `default` are both rejected (`general_argument_invalid`, field-scoped).
- An out-of-range coordinate (`lat: 200`) is a clean `row_invalid_structure` 400, not a raw Postgres
  error surfacing from the `geography` cast.
- `near` against a column with no spatial index is rejected (`general_query_invalid`).
- `near` on a non-geo column is rejected.
- A generic operator (`equal`) against a geo column is a clean 400, not a crash from binding a
  `GeoPoint` as an Npgsql parameter.
- A `spatial` index settles from `processing` to `available`.
- A `spatial` index over a non-geo column is rejected (`index_invalid`).
- **The real end-to-end case**: three rows at real San Francisco coordinates (City Hall, the Golden
  Gate Bridge, the Ferry Building) with a spatial index; `near` centered on City Hall with a 5000m
  radius includes City Hall itself and the Ferry Building (measured `ST_Distance` ≈ 3217m) and excludes
  the Golden Gate Bridge (measured `ST_Distance` ≈ 7201m) — real distances confirmed against the
  container directly during verification, not made-up numbers.

Full-repo `dotnet test`: **394 unit, 250 integration**, all passing — every pre-existing test still
green against the swapped PostGIS fixture image, plus these 12 new geo tests.

## Owner-test checklist

Done by me this session — **against a fresh scratch instance this session created and tore down**,
not the shared canonical local dev instance (`praxy-dev-pg` still runs plain `postgres:17-alpine`;
swapping *that* container's image is a decision for the owner to make deliberately, not something to
do silently mid-session to someone else's persistent dev data). The scratch instance: a throwaway
`postgis/postgis:17-3.6-alpine` container on port 5433, a `dotnet run --project src/Praxy.Api` pointed
at it on port 5090, and the normal `console` dev server — all torn down after verification.

- Created database `Geo`, table `Places`.
- Created a `geo` column `location` — confirmed the create-column sheet shows only Type/Key/Required
  for this type (Array/Default/Size/Elements/Target all correctly absent).
- Created a row via the grid's inline lat/lng editor — **found and fixed a real bug here**: the first
  attempt round-tripped `null` because `GeoValueEditor` derived each field's "other" value from the
  parent's `value` prop, which itself collapses to `null` while the pair is incomplete — so completing
  the second field couldn't see the first field's already-typed value. Fixed by giving the editor its
  own local draft state for both fields (see `RowsPage.tsx`'s `GeoValueEditor`); re-verified afterward
  that both the grid's inline editor and the separate Create Row sheet persist `{"lat":37.7749,
  "lng":-122.4194}` correctly, confirmed via the row's raw JSON view.
- Created a `spatial` index on `location` — the Type dropdown shows `spatial` alongside
  key/unique/fulltext, no per-column order selector appears (correct — GiST has none), and it settled
  from `processing` to `available` with a distinct amber badge, confirmed via a fresh page load (a
  polling staleness artifact appeared while actively driving the browser through many rapid actions in
  the same session — a real page reload showed the correct settled state immediately; the underlying
  job/index rows in Postgres were confirmed `available` throughout, so this was a UI staleness quirk
  during heavy automated interaction, not a settling bug).
- The real `near` inclusion/exclusion behavior against real coordinates and a real spatial index is
  exercised by `GeoEngineTests.Near_with_a_spatial_index_includes_the_closer_row_and_excludes_the_farther_one`
  (see Tests above) — the console has no `near` filter UI this phase (out of scope, matching the
  prompt's console bullet list exactly: column type, row editor, index badge — no filter-picker
  wiring), so the owner's own end-to-end `near` check should go through the API directly (`curl` or
  `/scalar/v1`), the same way this session verified it.

## Next

Whether a Phase 2 (distance-sorting, array-valued geo columns, more geo types) is warranted is the
owner's call — `docs/research/geo-nearby.md` deliberately didn't design one, and this report doesn't
either. No `docs/handoff/geo-nearby-phase-2-prompt.md` was written.
