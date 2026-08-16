# Phase 3 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run end to end against the dev
stack — in the console, via curl against the raw API, and via a dedicated integration test. 260 tests
green (181 unit, 79 integration).

## What shipped

**Catalog cache** (`Praxy.Tables/CatalogCache.cs`) — a `CatalogEntry` (database + table + columns + indexes
+ table permissions) loaded in one round trip per table instead of the five separate lookups schema
management makes. Invalidated **directly by the mutating services** (`TablesService`, `ColumnsService`,
`IndexesService`, `SchemaJobsService`, `SchemaJobRunner`) rather than through `IEventBus` — see deviations.
A 5s TTL is kept as a safety net for any path that forgets to invalidate.

**Row values** (`RowValues.cs`) — the JSON⇄Postgres boundary: validates a wire value against a column's
declared type (string length, email/url/ip format, enum membership, array element-by-element) and converts
it to a CLR value Npgsql can bind; reads a row back out of an `NpgsqlDataReader` as JSON, with datetimes
round-tripping as `yyyy-MM-ddTHH:mm:ss.fffZ` end to end.

**Query DSL** (`QueryDsl.cs`) — parses the appwrite-api.md JSON-per-query wire format into an AST, enforcing
architecture.md §4.6's caps (100 queries × 4096 chars, nesting depth 3, `limit` default 25 max 100). All 24
v1 methods. Every cap violation is a `general_query_invalid` 400 with a `fields` entry naming the offending
query key.

**Query compiler** (`QueryCompiler.cs`) — AST → parameterized SQL. Identifiers only ever come from
`CatalogEntry`/`PhysicalNaming` lookups, never a request string. Permission filtering is folded into the
same `WHERE` clause as the user's filters: a matching table-level grant short-circuits to `TRUE`; otherwise,
when `row_security` is on, an `EXISTS` against the table's `__perms` side table; otherwise `FALSE`. Keyset
pagination compiles `cursorAfter`/`cursorBefore` to a `(sort_column, _id) > (@v, @id)` tuple compare via a
correlated subquery against the cursor row; `cursorBefore` scans in the reversed direction and the caller
flips the result back. `search` without an available fulltext index on that exact column is rejected, never
a silent `ILIKE`.

**Row CRUD** (`RowsService.cs`) — create (client-supplied `rowId` or server `Guid.CreateVersion7()`), get,
list, update (**genuinely partial** — only properties present in `data` are touched; explicit JSON `null`
clears a nullable column; omitted keys are untouched), delete. Every write shares the ambient EF transaction
(`SchemaDdl.InTransactionAsync`, the same pattern DDL already uses) so the row change, its `__perms` writes,
and its outbox event commit or roll back together. Row and table permissions combine as **OR**: a caller
passes if the table-level matrix grants their role for the action, *or* — when `row_security` is on — the
row's own `__perms` grants it; row-level `create` isn't a thing (no row exists yet), so create is table-level
only. `$permissions` on a response is the row's *own* grants (empty when `row_security` is off).

**Row-level permissions** (`RowPermissions.cs`) — same `action("role")` grammar as table-level
(`PermissionStrings`), restricted to `read`/`update`/`delete`; `create`/`write` are rejected outright so a
row can never smuggle in its own creation grant.

**Outbox + realtime plumbing** — every row write inserts into `praxy.events` inside the transaction (Phase 6
reads this later) and, after commit, best-effort publishes to `IEventBus` (Phase 4's realtime attaches
here). Both carry the same payload: `databaseId`/`tableId`/`rowId`/`roles`, where `roles` is the set of
roles that could *read* the row, **computed pre-commit** — for `delete`, that happens before the row (and
its cascaded `__perms` rows) are gone, which is what makes the event authorizable after the fact.

**API key row-permission bypass** — `ApiKey.BypassRowPermissions` (new column, off by default per
architecture.md §5: "that bypass is exactly the flag that leaks data when it defaults wrong"). On: row CRUD
with that key skips table- and row-level filtering entirely, like a trusted server integration. This closes
the loop `RoleResolver.cs` left open in Phase 1 ("whether they bypass row permissions is an explicit flag
decided in Phase 3").

**API** — `/v1/databases/{db}/tables/{t}/rows` (data-plane) reachable by **app-user sessions and guests, not
just API keys** — unlike schema management, this is the real thing an end-user SDK calls, so permission
filtering *is* the access control. A key still needs `databases.read`/`databases.write`, checked only when
the caller actually is a key. `/v1/console/projects/{id}/databases/{db}/tables/{t}/rows` (console-admin)
bypasses permissions unconditionally — an operator manages the whole project — and audit-logs every write.
Row list is `GET .../rows?queries[]=<json>&queries[]=<json>...&total=false`. 4 new error types
(`row_not_found`, `row_already_exists`, `row_invalid_structure`, `general_query_invalid`).

**Console** — `RowsPage.tsx`: row browser on `<DataGrid />` (extended with an `onNearEnd` hook for
cursor-driven infinite scroll via `useInfiniteQuery`), inline cell editing (click a cell, edit, Enter/blur
saves only that field — verified only the edited column and `$updatedAt` change), NULL rendered as an
italic grey "NULL" distinct from a red `false`, filters popover → chips → `?query=` URL param (read/written
directly, not through the router's search-param typing, which nothing else in the console uses yet), column
header click to sort, row side sheet with prev/next, raw JSON view + copy-as-JSON, a permissions tab
(read/update/delete only — no Create column at row level, matching console-design.md), bulk select +
floating "N selected · Cancel · Delete" bar, ghost-sheet empty state with real column headers. "Rows" is now
the first tab on a table and the sidebar's default landing route. API keys gained a "Bypass row permissions"
toggle on the create-key sheet.

**Tests** — 4 new unit suites (`QueryDslTests`, `QueryCompilerTests`, `RowValuesTests`,
`RowPermissionsTests` — 80 new unit tests) covering cap enforcement, permission-predicate short-circuiting,
SQL-injection-shaped filter values proven parameterized-not-interpolated, and per-type value validation; one
new integration suite (`RowEngineTests`, 9 tests) covering the full CRUD round trip, partial-PATCH isolation,
client-supplied-id conflicts, unknown-column/missing-required 400s, real cursor pagination across pages,
the limit-cap 400, `search`-without-an-index rejection, the `row_security` toggle changing a second app
user's reads (the exact owner-test scenario, via two real signed-up sessions), the permissions-require-
row_security guard, and a direct check that `praxy.events` receives a row on create. All 200 Phase 1/2 tests
stayed green throughout — 260 total (181 unit, 79 integration).

## Deviations & notes

- **Catalog cache invalidation is direct, not event-bus-based.** roadmap.md says "invalidated by
  schema-change events"; in a single Praxy process, a mutating service calling `cache.Invalidate(tableId)`
  directly is strictly more immediate than a pub/sub hop through `IEventBus`; and every mutation path
  already runs in-process. `IEventBus` stays reserved for its designed job (realtime fan-out, cross-cutting
  best-effort consumers), which is exactly what row writes still publish to.
- **`rowId` is an optional field on the create request, not Appwrite's `"unique()"` sentinel string.**
  Appwrite's SDKs need the sentinel because their `documentId` parameter is positional and required; Praxy's
  JSON body can just omit `rowId`. A supplied `rowId` must parse as a UUID (any version, via
  `Ids.TryParseWire`) — `_id` is a `uuid` column per architecture.md §4.2, not Appwrite's arbitrary
  custom-string document id, so "client-supplied id" here means "bring your own UUID" (idempotent writes,
  migrating known ids), not an arbitrary slug.
- **Single-column sort for keyset purposes.** architecture.md §4.6 already specifies the cursor tuple as
  `(sort_column, _id)` — singular. If more than one `orderAsc`/`orderDesc` query is sent, the first is used
  and later ones are silently ignored rather than erroring; multi-column keyset cursors are a real design
  problem (which column breaks the tie?) that didn't have a concrete need this phase.
- **`search` requires an index whose columns are exactly `[that one column]`.** A composite fulltext index
  covering multiple columns isn't matched. No owner-test step needs it, and the simpler single-column rule
  is easy to reason about; broadening it is additive later.
- **List is GET-only** (`queries[]` repeated query-string params), not the POST-body form
  appwrite-api.md mentions as an alternative. Every owner-test and SDK-shaped use case fits in a URL; a
  body-based variant is a mechanical addition if query strings ever get too long for a client.
- **The console's `?query=` URL param is read/written directly** (`URLSearchParams` +
  `history.replaceState`), not through TanStack Router's typed search-param API — nothing else in this
  console uses `validateSearch` yet, and introducing that machinery for one screen would be a bigger surface
  change than the feature warrants. Sort and pagination position aren't persisted to the URL, only filters.
- **No dedicated bulk-delete endpoint.** The console's bulk-select action fires per-row `DELETE` calls in
  parallel (`Promise.all`) rather than the backend growing a batch endpoint — the roadmap only asks for the
  console feature, not a new wire method, and row tables aren't expected to need batches larger than a
  screenful at once yet.
- **Found and fixed two pre-existing bugs while building this:** (1) `ApiKeysPage.tsx`'s scope picker only
  listed the Phase 1 scopes (`users.*`, `teams.*`) — Phase 2 added `databases.read`/`databases.write` to
  `ApiKeyScopes.All` but never updated the console's checkbox list, so a key with schema access could only
  ever be minted via raw API calls. (2) `DataTable`/`EmptyState` in `components/ui.tsx` keyed each header
  cell by the header *string* — harmless while every screen's headers were unique, but the row browser's
  select/actions columns both have blank `""` headers, which React flags as a duplicate-key error. Both
  fixed; keyed by index now.

## Known gaps (deliberate, next phases or later)

- Inline editing a **boolean array** column uses the same comma-separated text editor as other arrays
  (`"true,false"` typed as strings) — the backend correctly rejects the resulting non-boolean JSON values
  with a 400 field error rather than silently corrupting data, but there's no dedicated multi-select control
  for it yet. Boolean arrays are rare; console polish for later.
- CSV import/export — explicitly deferred, per roadmap.md's Phase 3 console bullet ("can wait").
- Multi-column keyset sort (see deviations above).
- Row-level realtime/webhook consumers of the outbox and `IEventBus` publishes this phase writes — nothing
  reads them yet by design; Phase 4 (realtime) and Phase 6 (webhooks) are the consumers.

## Commands

```
docker run -d --name praxy-dev-pg -e POSTGRES_USER=praxy -e POSTGRES_PASSWORD=praxy \
  -e POSTGRES_DB=praxy -p 5432:5432 postgres:17-alpine   # dev database
dotnet run --project src/Praxy.Api                       # API :5090 (Scalar at /scalar/v1)
npm run dev --prefix console                              # console :5173, /v1 proxied to :5090
dotnet test                                                # 260 tests; Docker required (Testcontainers)
cd deploy && ./up.sh                                       # self-host stack → http://localhost:8080/console
```

No change to the command set — noted here since the handoff protocol asks. The new EF migration
(`DataPlane`, adds `api_keys.bypass_row_permissions`) applies automatically on API startup like every prior
migration; no manual step.

## Owner-test checklist (run by this session, all passing)

1. Console → Databases → Blog → created table "Posts" → added `title` (string, required), `views`
   (integer), `published` (boolean) columns → landed on the new **Rows** tab (now the default) showing the
   ghost empty state with real column headers.
2. Created rows via the console's "+ Create row" sheet; a row with `views` left blank and `published=false`
   confirmed **NULL renders visually distinct from FALSE** (italic grey "NULL" vs. red "false").
3. Clicked a cell → inline-edited `views` from NULL to 42 → grid updated in place; raw-JSON view on the row
   sheet confirmed only `views` and `$updatedAt` changed, `title`/`published`/`$createdAt` untouched.
4. **Filter**: `+ Filter` → `title contains "Second"` → chip appeared, grid narrowed to the matching row,
   `1 total`; URL gained `?query=[{"method":"contains","attribute":"title","values":["Second"]}]`; "Clear
   filters" restored both rows.
5. **Sort**: clicked the `views` column header → ascending arrow appeared, rows reordered 10 → 42.
6. **Bulk select + delete**: header checkbox selected both rows → floating "2 selected · Cancel · Delete"
   bar appeared → Delete → grid returned to the empty ghost state, `0 total`.
7. **Row permissions**: Settings → "Owner only" preset → `row_security` badge appeared next to the table
   name, permission matrix showed `create` granted to `users`. Reopened a row's sheet → Permissions tab
   showed the read/update/delete matrix (**no Create column at row level**) → added `label:vip` with read
   checked → raw JSON confirmed `"$permissions": ["read(\"label:vip\")"]` and `$updatedAt` moved.
8. **Cursor pagination via API**: created 5 rows via curl with an API key, `GET .../rows?queries[]=<orderAsc
   views>&queries[]=<limit 2>` returned `views: [1,2]` and `total: 5`; a follow-up request with
   `cursorAfter` set to the last row's `$id` returned `views: [3,4]` — no overlap, correct order.
9. **Exceed a query cap**: `GET .../rows?queries[]={"method":"limit","values":[500]}` → `400
   general_query_invalid` with `fields.limit: ["'limit' must be between 1 and 100."]`.
10. **`row_security` flips a non-owner session's reads** (automated as
    `RowEngineTests.Row_security_toggle_changes_a_non_owner_sessions_reads`, and matches the scenario
    Table Settings' own copy describes): table granted `create("users")` + `read`/`update` only to user A;
    user B got `404 row_not_found` on A's row. Flipping `row_security` on and A sharing that one row with
    `read("user:B")` made B's `GET` on *that* row `200`, while an unshared row stayed `404` for B — B's
    reads changed exactly where the grant said they should, nowhere else.

## Next: Phase 4

Praxy's data plane is real. The prompt below is ready to paste into a fresh session.
