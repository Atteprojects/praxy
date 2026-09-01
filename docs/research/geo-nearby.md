# Geo columns and `near` queries — design

## Context

The owner asked to implement geo operations, starting with "nearby" queries — the classic BaaS "find
things within N meters of this point" capability, and one of the concrete gaps found comparing Praxy's
column types against Appwrite's (which has `point`/`line`/`polygon` attribute types). This doc scopes
the first slice: a `geo` point column type plus a `near(lat, lng, radiusMeters)` query operator, using
real great-circle distance (not flat-plane approximation) via PostGIS — the owner's explicit choice over
the lighter `earthdistance`/`cube` contrib-module alternative, made knowing the real cost: **this is the
first Postgres extension this codebase has ever needed.**

Grounded in direct code research (query DSL/compiler, row read/write, column-type/index extension
points — the same subsystems the `docs/research/table-relationships.md` initiative just went through in
depth) plus a live check of what's actually deployed today (`grep -rn "CREATE EXTENSION"` across the
whole repo: zero matches).

## Why this is architecturally different from every column type added so far

Relationships (the most recently added type) reused 100% existing infrastructure — the `IsArray` flag,
the existing `unique` index type, the existing per-column-type `JsonElement`-to-scalar conversion
pattern. Geo does not get any of those for free:

- **First Postgres extension.** No `CREATE EXTENSION` exists anywhere in this codebase today. A `geo`
  column needs `CREATE EXTENSION IF NOT EXISTS postgis`, which needs to run once per database — most
  naturally as a raw-SQL step in `CatalogMigrator`'s existing startup-migration path (same
  `pg_advisory_lock`-guarded sequence that already applies EF migrations), not a new mechanism.
- **First non-btree/GIN index.** Every index this engine creates today (`key`, `unique`, `fulltext`'s
  generated-`tsvector`-plus-GIN) is btree or GIN. A spatial index needs **GiST** — a new `IndexDef.Type`
  value, and `near` queries against an unindexed column would silently sequential-scan. This engine
  already has a precedent for refusing to allow that: `search` requires a declared fulltext index and is
  *rejected* without one (`docs/architecture.md` §4.6: "Without the index, the query is rejected rather
  than silently doing a sequential `ILIKE`"). `near` should get the identical discipline — reject the
  query with a clear error if the column has no spatial index, don't silently accept a slow scan.
- **First column whose single (non-array) value isn't a bare JSON scalar.** Every existing type — even
  `enum`, even `relationship` — reads/writes as one JSON string/number/bool per value. A geo point is
  inherently a pair: `{"lat": <number>, "lng": <number>}` is the proposed wire shape (an object, not a
  2-element array — GeoJSON's own `[lng, lat]` array convention is a well-known footgun precisely because
  the order is the reverse of how people say it out loud; naming the fields avoids that class of bug
  entirely).
- **First query operator whose values aren't values *of* the column's type.** Every existing operator
  (`equal`, `between`, etc.) converts its `Values` through `RowValues.ToFilterScalar`/`ConvertValue`
  *against the column's own declared type* — a `between` on a `float` column converts two floats. `near`'s
  three values (lat, lng, radius) are not geo-typed at all; they're plain numbers describing a query
  point and a distance, unrelated to how the column itself is stored. `near` bypasses `RowValues`'s
  per-column-type conversion entirely and parses its own three doubles inline — a genuinely new shape in
  `QueryCompiler.Builder.CompileFilterNode`'s method switch, not an extension of an existing pattern.

## Design decisions

**No new .NET package.** This surprised the initial framing — PostGIS access looked like it might need
`Npgsql.NetTopologySuite` (the standard plugin for mapping PostGIS types to strongly-typed .NET geometry
objects). It doesn't: `RowValues.cs` never maps to strongly-typed objects for *any* column type today —
every read is a plain `reader.GetFieldValue<T>(ordinal)` scalar, every write is a plain `NpgsqlParameter`.
Geo fits the same shape: write via `ST_MakePoint(@lng, @lat)::geography` (two `double` parameters, no
NTS object), read back via `ST_Y(col::geometry)`/`ST_X(col::geometry)` (two `double` results). PostGIS is
purely a database-side extension here, reached through plain SQL function calls — the only new
dependency is the extension itself, not a NuGet package. This keeps the "consult
`docs/research/dotnet-stack.md` before adding any package" concern almost moot for this feature.

**Scalar only in v1 — no array-of-points.** Unlike relationships (where array support was free via the
existing `IsArray` flag), an array-valued geo column raises real new questions (does `near` match if
*any* point in the array is within radius? *all* of them? how does that compile to a single `ST_DWithin`
predicate?) that don't have an obvious free answer. Deferred, not designed here — same "ship the primitive
first" instinct every prior feature in this repo has followed.

**`near` is a pure radius filter — no automatic distance-sorting.** This is the more consequential
limitation, and it's a real one, not a style choice: `QueryCompiler.CompileList`'s sort model ties
`ORDER BY` **and** the keyset-cursor tuple-compare directly to a real `ColumnDef`/physical column
(`sortColQuoted = PhysicalNaming.Quote(effectiveSortColumn.PhysicalName)`, baked into both the `ORDER BY`
clause and the `(t.{sortCol}, t.{idCol}) {cmp} (...)` cursor comparison). There is no mechanism today for
sorting by a *computed expression* like `ST_Distance(...)`. Supporting MongoDB-style "results
automatically nearest-first" would mean either a new sort model carrying a SQL expression instead of a
column, or rewriting the cursor's tuple-compare around a distance expression — real surgery to a
foundational, correctness-sensitive part of the query compiler, not something to improvise inside this
feature. **v1 ships `near` as a filter only**: rows within `radiusMeters` of the point, in whatever order
the query's own `orderAsc`/`orderDesc`/default sort says — not nearest-first automatically. Distance-based
sorting is real, wanted follow-up work, explicitly out of this scope, not forgotten.

**Reject unindexed `near` queries, mirroring `search`'s existing discipline exactly.** A `near` query
against a `geo` column with no GiST index on it is rejected with a clear error (which spatial index is
missing, how to add one) rather than silently accepted as a sequential scan — the same posture
`CompileSearch` already takes for fulltext search without a declared index.

**Wire shape: `{"lat": <number>, "lng": <number>}`, SRID 4326 (WGS84 — standard GPS coordinates).**
Stored as `geography(Point, 4326)`. No default value in v1 — same reasoning relationships used for
rejecting defaults (a hardcoded default point is a plausible request, but not needed for the primitive to
work end to end, and keeping the surface small for a first pass is worth more than the convenience).

## Infrastructure impact — confirmed narrow

Exactly two files reference the Postgres image tag directly (verified by grep, not assumed):
`deploy/docker-compose.yml` (`postgres:17-alpine`) and
`tests/Praxy.Tests.Integration/Infrastructure/PostgresContainerFixture.cs` (same tag, Testcontainers).
Both need to move to a PostGIS-flavored image (e.g. `postgis/postgis:17-3.4-alpine` — the implementing
session verifies and pins the exact current tag, same discipline `docs/research/dotnet-stack.md` already
uses for every other pin). `deploy/backup.sh`/`restore.sh` are unaffected — they address the `postgres`
service by name via `docker compose exec`, never the image tag.

**This is a real upgrade-path concern for existing self-hosted instances, not just fresh installs.**
Swapping to a PostGIS-flavored image is still Postgres 17 underneath (PostGIS images are the official
Postgres image plus extensions, same major version) — the existing data volume should survive the swap
unchanged, and `CREATE EXTENSION postgis` runs once via the normal migration path on the next `api`
startup. `docs/self-host.md`'s Upgrading section needs a note about this exact sequence, the same care
already given to "always restart Caddy after a deploy" — an easy thing to get silently wrong once, not
verify-and-forget.

## Data model

```
ColumnTypes.Geo = "geo"                    -- src/Praxy.Tables/ColumnTypes.cs
PostgresType("geo", ...) => "geography(Point, 4326)"
```

No new `ColumnDef` field needed — unlike relationships (which needed `TargetTableId` to be relationally
queryable for delete-integrity checks), a geo column carries no cross-referencing metadata at all.

```sql
-- column DDL:
ALTER TABLE {qualified} ADD COLUMN {quoted} geography(Point, 4326);

-- a new IndexDef.Type value, "spatial":
CREATE INDEX {quoted} ON {qualified} USING GIST ({column});

-- near(lat, lng, radiusMeters) compiles to:
ST_DWithin({quoted}, ST_MakePoint(@lng, @lat)::geography, @radiusMeters)
```

**Every exhaustive `switch` over column type needs a `Geo` case** — the same landmine class relationships
hit, confirmed the identical list applies here: `ColumnTypes.PostgresType`, `RowByteBudget.EstimateBytes`
(a `geography(Point)` value is small and fixed-size on disk — roughly 32 bytes; confirm the exact figure
against a real column rather than guessing), `RowValues.ToScalar`/`ToWriteValue` (parse
`{"lat","lng"}` → an `ST_MakePoint(...)::geography` parameter — this one differs from every prior type in
that the *SQL* for the parameter isn't a bare `@param`, it's a function call wrapping two params; the
write-value-building code in `RowsService.cs` that emits `{physicalName} = @{pName}` for every other type
needs a geo-specific branch emitting `{physicalName} = ST_MakePoint(@{pName}_lng, @{pName}_lat)::geography`
instead of the generic single-parameter form — flag this precisely for whoever implements it, it's easy
to assume the existing single-parameter plumbing "just works" and be wrong), `RowValues.ReadScalar` (two
`double` reads — `ST_X`/`ST_Y` on the *selected expression*, not the raw column, so `SelectColumns`'s
column-list-building also needs a geo-specific branch, not just `ReadScalar`'s reader-side conversion).
`FormatLiteral`/`FormatArrayLiteral` need no case — no default value, no array, in v1.

## Phased rollout

- **Phase 1 — the primitive**: `postgis/postgis` image swap (self-host compose + Testcontainers), the
  `CREATE EXTENSION postgis` migration, the `geo` column type (scalar only), a new `spatial` index type
  (GiST), the `near(lat, lng, radiusMeters)` query operator gated on a declared spatial index (mirroring
  `search`'s fulltext-index requirement exactly), console support for creating a `geo` column and editing
  a row's lat/lng, Flutter codegen passthrough. No distance-sorting, no array-of-points, no
  `line`/`polygon` types.
- **Later, not designed here**: distance-based sorting (the real cursor/sort-model surgery flagged
  above); array-valued geo columns; additional geo column types (`line`, `polygon`) and their own
  operators (`within`, bounding-box search) if more geo operations beyond "nearby" are wanted, matching
  the owner's own framing ("geo operations like nearby *first*").

## Verification

This doc's claims about the current codebase were checked directly, not assumed: the query DSL's exact
arity-validation switch (`QueryDsl.ValidateArity`), the compiler's exact per-method dispatch and its
existing multi-value precedent (`between`'s two independent `AddParam` calls — the direct model for
`near`'s three), the sort/cursor model's hard dependency on a real `ColumnDef` (confirmed no
computed-expression sort path exists anywhere), and a repo-wide grep confirming zero existing Postgres
extensions and exactly two files referencing the Postgres image tag.

The Phase 1 implementation session (kickoff: `docs/handoff/geo-nearby-phase-1-prompt.md`) owns pinning
the exact PostGIS image tag (per `docs/research/dotnet-stack.md`'s standing discipline) and its own
verification: `dotnet test` against a real PostGIS-enabled Postgres via Testcontainers, the console
owner-test, and a real end-to-end `near` query (two rows at known coordinates, a radius that includes one
and excludes the other, confirmed against real distance math) — the same discipline every prior phase
used.
