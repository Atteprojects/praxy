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
- **Phase 2 — distance-sorting (`orderNear`)**: nearest-first ordering, with full keyset-cursor
  pagination alongside it. Designed below, kickoff: `docs/handoff/geo-nearby-phase-2-prompt.md`.
- **Phase 3 — `$distance` + `withinBox`**: the returned distance value and a viewport/bounding-box
  filter. Both are things Appwrite does *not* have (see "Competitive position" below), which is why
  they're sequenced ahead of the parity work. Designed below, kickoff:
  `docs/handoff/geo-nearby-phase-3-prompt.md`.
- **Phase 4 — `polygon` and `line` column types, `within`/`intersects`/`contains`** (not designed
  here — needs its own doc): the geofencing capability gap, and the expensive one. New wire shapes for
  multi-coordinate geometry, a console editor that is a real UI problem rather than a form field, and
  codegen decisions none of Phases 1-3 had to make. `polygon` and `line` belong in one phase because
  they share almost all of that plumbing.
- **Phase 5 — the parity tail** (not designed here): `crosses`/`touches`/`overlaps` and their negations,
  plus array-valued geo columns. Array-geo is last deliberately: relationships (shipped 2026-09-01)
  already model "one record, many locations" better, and `line` covers the ordered-path case more
  meaningfully — it's in scope because the owner asked for the full sweep, not because the modeling
  need is otherwise unmet.

## Competitive position — checked 2026-09-02, not assumed

The owner's stated goal for Phases 3-5 is to offer more than Appwrite. Verified against Appwrite's own
spatial-columns announcement rather than memory, because it changes the sequencing:

**Appwrite already ships** `point`, `line`, and `polygon` column types with twelve predicates —
`crosses`/`notCrosses`, `intersects`/`notIntersects`, `overlaps`/`notOverlaps`, `touches`/`notTouches`,
and `distanceEqual`/`distanceNotEqual`/`distanceGreaterThan`/`distanceLessThan`. So Praxy's `line`/
`polygon`/`within` work is **catch-up to parity, not differentiation** — worth knowing before spending
the largest phase on it.

**What Appwrite's announcement never mentions**: ordering by distance. All four of its distance
operators are filters, not sorts — there is no nearest-first, no KNN, and no returned distance value.
Praxy's `orderNear` (Phase 2, shipped 2026-09-02) is therefore already the differentiator, and Phase 3's
`$distance` extends the same lead.

Two further places Phases 1-2 already chose better defaults, both worth keeping in any comparison:
- Appwrite's geo queries **silently full-scan without a spatial index** ("queries without a spatial
  index will work, but won't perform well at scale"). Praxy rejects them with an actionable error
  naming the missing index — the `never a silent sequential scan` rule `CompileSearch` established.
- Appwrite represents coordinates as `[longitude, latitude]` arrays. Praxy's `{"lat","lng"}` object was
  chosen in Phase 1 specifically to kill that ordering footgun.

## Phase 3 — `$distance` and `withinBox`

Two independent additions that share no code, batched into one phase because both are small and both
are competitive differentiators.

### `$distance` — the computed distance, returned

A new system field alongside `$id`/`$createdAt`/`$updatedAt`/`$permissions`, in **meters** (matching
`near`'s `radiusMeters` and PostGIS's own geography unit).

**Emitted only when `orderNear` is present, measured from `orderNear`'s query point.** This is the
design doc's earlier open question ("what units? what key name? does it apply to bare `near()` too?")
settled deliberately:
- `orderNear` already computes this distance for its `ORDER BY`, so the value is essentially free there
  and answers the question the sort itself raises ("sorted by distance — how far?").
- `orderNear` is single and first-wins, so "which point is this measured from?" has exactly one answer.
  A bare `near()` filter can legitimately appear more than once in a query (different columns, or an
  `or` branch), which would make the same field ambiguous.
- Not emitting it for bare `near()` is additive to revisit later; emitting it ambiguously is not.

**Compute it with `ST_Distance`, not by reusing the `<->` value.** `<->` is what the `ORDER BY` uses
because it is the index-accelerated KNN operator, but its exact return value for `geography` has
historically differed between sphere and spheroid across PostGIS versions. `$distance` is a number an
app will display to a user, so it should be the explicit, documented, spheroid-by-default
`ST_Distance(col, point)`. That means two distance computations per returned row rather than one — a
cost worth paying, and bounded by the page limit rather than the table size. **The implementing session
must confirm the two agree closely enough that ordering never disagrees with the displayed values** (a
row shown as nearer must never sort after a row shown as farther); if they can disagree, prefer
`ST_Distance` in both places and accept the plan change.

**Plumbing** — the ordinal-safety point matters more than it looks:
- Append the distance expression to the **end** of `BuildSelectList`'s select list, never the middle.
  `RowsService.BuildRowJson` walks result ordinals positionally from 3, advancing by **2** for geo
  columns and 1 for everything else; a trailing column is the only placement that cannot disturb that.
- `CompiledListQuery` carries a flag saying the distance column is present (it already carries
  `SelectedKeys`/`Reversed` for the same kind of reason). `BuildRowJson` reads
  `reader.GetFieldValue<double>(ordinal)` once after its column loop, where `ordinal` is already exactly
  the count of consumed columns.
- `select(...)` does not suppress it. `BuildSelectList` already emits `_id`/`_created_at`/`_updated_at`
  unconditionally regardless of `select`, and `$distance` is a system field of the same kind.
- `Create`/`Get` never pass the flag — only `List` can carry an `orderNear`.

### `withinBox(minLat, minLng, maxLat, maxLng)` — the viewport filter

A 4-arity filter method: every point inside an axis-aligned rectangle. This is what a map UI needs on
every pan and zoom, and today the only approximation is an oversized `near()` radius plus client-side
filtering.

```sql
ST_Intersects({col}, ST_MakeEnvelope(@minLng, @minLat, @maxLng, @maxLat, 4326)::geography)
```

- **Named `withinBox`, not `within`** — `within` is deliberately reserved for Phase 4's polygon
  containment, which is the operation developers will expect that word to mean.
- **Values are lat-first**, matching `near(lat, lng, ...)`'s existing convention; the compiler reorders
  them into `ST_MakeEnvelope`'s x/y (lng/lat) argument order, exactly as `CompileNear` already does for
  `ST_MakePoint`. Never expose PostGIS's axis order on the wire.
- **Requires a declared spatial index**, same rule and same error shape as `near`/`orderNear`.
  Confirmed: PostGIS's named predicates (`ST_Intersects` included) use a GiST index automatically when
  one exists — the `&&` bounding-box operator does not need to be written out alongside it, and `&&` is
  a geometry operator anyway, not a geography one.
- **Validation**: `minLat < maxLat` required. `minLng > maxLng` means a box crossing the antimeridian,
  which `ST_MakeEnvelope` cannot express — **reject it with a clear error in v1** rather than silently
  producing an inverted envelope that matches nothing. Splitting into two envelopes is the real fix and
  is additive later; a wrong answer is not.
- **A geography envelope's edges follow great circles, not parallels.** Negligible for a map viewport,
  visible for a box spanning many degrees of latitude. The implementing session should verify against
  real coordinates and document the behavior rather than discover it in a bug report.

**Coordinate-range validation is a consistency decision, not a `withinBox` detail**: `near` does not
currently reject a latitude of 200. Adding range checks to `withinBox` alone would make the two
inconsistent, so either add `[-90,90]`/`[-180,180]` validation to both or to neither. Recommend both —
it is a small, purely-additive way to catch an obvious caller error, and the error shape already exists.

## Phase 2 — distance-sorting (`orderNear`)

**Corrects an earlier answer given to the owner.** When Phase 1 shipped, this doc's "Later, not
designed here" bullet called distance-sorting "the real cursor/sort-model surgery" and implied it
might need to fall back to offset-only pagination for a first cut. A closer, line-level re-read of
the actual `QueryCompiler.CompileList`/`Builder.AddParam` code (not the earlier summarized research)
shows that's overly cautious: **full keyset-cursor pagination is achievable in this phase**, detailed
below. Nothing about Phase 1's shipped `near()` contract changes — `orderNear` is new and additive.

### DSL: `orderNear(lat, lng)`

A new query method, attribute = the geo column key, 2-arity (`lat`, `lng` — matching `between`'s
existing two-value precedent, the same shape `near`'s three values already established for
`QueryDsl.ValidateArity`):

```
QueryDsl.AllMethods gains "orderNear"
QueryDsl.ValidateArity gains: "orderNear" => count == 2, // lat, lng
```

**Self-sufficient — does not require a co-occurring `near()` filter.** This is deliberate, for two
reasons: it supports "K nearest, no radius cutoff" as its own valid use case (the most common shape
for "find the 10 closest stores"), and it leaves Phase 1's shipped `near()` untouched as a pure radius
filter with no implicit sorting — exactly the contract its own doc comment already promises
(`CompileNear`'s doc comment: "a pure radius filter, never automatic nearest-first sorting"). A caller
who wants both radius-bounding *and* nearest-first order simply sends both `near(...)` and
`orderNear(...)` in the same request — they compose independently, no new interaction code needed,
since `near()` lives in the filter/WHERE path and `orderNear` lives in the order/cursor path.

**Nearest-first only — no `orderNearDesc`.** Farthest-first isn't a real use case worth a second
method; keeping the surface to one verb matches the "don't build for a hypothetical" discipline
already applied elsewhere in this engine.

**Requires the column to be `geo`-typed and to have a declared spatial (GiST) index** — the same two
checks `CompileNear` already makes for `near()` (`column.Type != ColumnTypes.Geo` → 400,
`entry.SpatialIndexFor(column.Key)` missing → 400 pointing at "create a spatial index" rather than let
an unindexed `<->` silently sequential-scan-and-sort). Validated where `CompileList`'s method switch
recognizes `orderNear`, mirroring `ResolveColumn`'s existing role for `orderAsc`/`orderDesc`.

### Compiles to PostGIS's KNN operator

```sql
ORDER BY {quoted} <-> ST_MakePoint(@lng, @lat)::geography ASC
```

`<->` on `geography` is GiST-index-assisted best-first nearest-neighbor search (confirmed via
websearch, not assumed) — efficient and `LIMIT`-aware, with one real constraint: a geometry/geography
*literal or parameter* on one side of the operator, never a column reference on both sides. That's
satisfied automatically here since the query point is always the request's own `lat`/`lng` values,
never derived from another row.

### Sort-key generalization in `CompileList`

Today, `sortColQuoted` (`QueryCompiler.cs:103`, `PhysicalNaming.Quote(effectiveSortColumn.PhysicalName)`)
is a single quoted-identifier string, computed once from a resolved `ColumnDef`, then reused verbatim
in exactly three places: the cursor subselect (`:122`), the cursor tuple-compare (`:124`), and the
final `ORDER BY` (`:129`). `orderNear` needs the same three call sites to emit a *computed expression*
instead of a bare column reference — the generalization is contained to those three sites because all
three already just interpolate a string, none of them inspect `sortColQuoted`'s shape:

- The loop (`:57-87`) gains a third arm in the order-method case (`case "orderAsc" or "orderDesc" or
  "orderNear":`), and on `orderNear` resolves the attribute to a `geo` column (with the type + spatial
  index checks above) and stashes the two parsed `lat`/`lng` doubles alongside it — `AddParam` can't
  be called yet here, since `select`/`count`'s `Builder` instances don't exist until after the loop
  (`:111`, `:139`).
- Immediately after `select = new Builder(entry)` (`:111`), when the resolved sort is `orderNear`,
  call `select.AddParam(lng)` and `select.AddParam(lat)` **once** each, building
  `nearPointExpr = $"ST_MakePoint({lngParam}, {latParam})::geography"` from the two returned `"@pN"`
  strings.
- Replace the three `{sortColQuoted}`/`t.{sortColQuoted}` interpolations with calls to a small
  `SortKeyExpr(string alias)` local function: for a column sort it returns
  `$"{alias}{PhysicalNaming.Quote(resolvedColumn.PhysicalName)}"` (today's behavior, unchanged bit for
  bit); for `orderNear` it returns `$"{alias}{PhysicalNaming.Quote(geoColumn.PhysicalName)} <-> {nearPointExpr}"`.
  `alias` is `""` in the unaliased cursor subselect and `"t."` in the two aliased sites — matching
  today's `sortColQuoted` vs `t.{sortColQuoted}` split exactly.
- The `count` query (`:139`, used only for `includeTotal`) needs none of this — it has no `ORDER BY`
  and no cursor, so `orderNear`'s params are never added to its independent `Builder`/`Params` list.

**Why keyset pagination just works, reusing the exact mechanism `Builder.AddParam` already provides**:
`AddParam` (`:155-160`) returns a `"@pN"` name string after appending one entry to `Params`; Npgsql
resolves bound parameters **by name**, so the same `"@pN"` text can appear multiple times in one SQL
statement while the parameter itself is bound only once. This is exactly what makes reusing
`nearPointExpr` verbatim across the subselect, the tuple-compare, and the `ORDER BY` free — no
cross-request state, no second round of parameter binding. It also matches how a client already has to
behave with today's column-based cursors: `orderAsc(col)` must be resent on every page request for the
cursor to mean anything, and `orderNear(lat, lng)` is the identical shape — the client resends the
same query point on every page, which it already has to do anyway since it's the thing being searched
near.

```sql
-- resuming after row @cursorId, nearest-first:
... AND (
  ({quoted} <-> ST_MakePoint(@lng, @lat)::geography, _id)
    > (
      (SELECT {quoted} <-> ST_MakePoint(@lng, @lat)::geography FROM {qualified} WHERE _id = @cursorId),
      @cursorId
    )
)
ORDER BY t.{quoted} <-> ST_MakePoint(@lng, @lat)::geography ASC, t._id ASC
LIMIT @limit
```

**One open question the implementing session must verify, not assume**: whether Postgres still picks
the GiST KNN index-assisted plan for the `ORDER BY ... <-> ... LIMIT` once it's combined with the
keyset `WHERE` clause's own (non-indexable) distance recomputation for the tuple-compare. Confirm with
`EXPLAIN ANALYZE` against a realistically-sized seeded table, not by inspection alone. If the planner
does *not* use the index-assisted scan under that combination, that's not a correctness problem —
results are still right — only a performance one; `CompileList` already supports plain `offset`-based
pagination unconditionally (`:93-94`, `:132-133`), so nothing needs to be removed or gated to keep that
path available as a documented fallback for callers who hit it.

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

Phase 2's design above was checked the same way, directly against the current
`src/Praxy.Tables/QueryCompiler.cs` (not the earlier, coarser research this doc's Phase 1 section was
originally grounded in): `CompileList`'s exact loop structure and line numbers, `Builder.AddParam`'s
by-name reuse semantics, and `CompileNear`'s existing type/spatial-index checks as the direct model for
`orderNear`'s own. PostGIS's `<->` KNN behavior (GiST-index-assisted, `LIMIT`-aware, one-literal-side
constraint) was confirmed via websearch, not assumed. The Phase 2 implementation session (kickoff:
`docs/handoff/geo-nearby-phase-2-prompt.md`) owns its own verification: `dotnet test`, the console
owner-test, a real end-to-end `orderNear` query (several rows at known coordinates and known relative
distances, confirmed returned in the right order), keyset pagination across `orderNear` specifically
(page through past a cursor, confirm no duplicate/skipped rows), and the `EXPLAIN ANALYZE` index-usage
check flagged above.
