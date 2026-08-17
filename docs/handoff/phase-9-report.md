# Phase 9 — report

**Status: complete. v0.1.0 tagged.** All roadmap items shipped; owner-test checklist run end to end
against an isolated throwaway instance (fresh Postgres container on a scratch port, a second API
instance on a scratch port, the console dev server temporarily repointed at it — same isolation
pattern every prior phase used), driving the real console UI in the Browser pane, and with direct
`docker exec`/`psql`/`pg_dump`/`pg_restore` for the pieces that need real Postgres tooling rather
than the app. 404 .NET tests green (301 unit + 103 integration, up from 373 total in Phase 8).
Console `tsc -b && vite build` clean. This is the last phase in `docs/roadmap.md` — no
`docs/handoff/phase-10-prompt.md` follows.

This phase was explicitly framed as an audit, not a new feature module: verify what prior phases
claimed against real code, close real gaps, prove backup/restore and upgrades actually work. Every
item below was checked against the running system, not assumed from memory — three of the fixes
below (the query-compiler crashes, the null-key crashes, and the Messaging SSRF gap) were found this
way and would not have surfaced from a checklist read alone.

## What shipped

**Org-level quotas** (`src/Praxy.Tables/Quotas/`): `OrganizationLimits` (parses
`organizations.limits` jsonb, tolerant-parse shape matching `EmailProviderConfig.Parse`) +
`QuotaOptions` (instance-wide defaults, bound from `Praxy:Quotas:*`, numerically identical to the
hardcoded consts every dimension used before this phase — zero behavior change for an operator who
touches neither config nor `organizations.limits`) + `QuotaService` (resolves org limits, falls back
to instance defaults, enforces project/database/table/column/index caps, and builds a
`QuotaSnapshot` for console display). `DatabasesService`/`TablesService`/`ColumnsService`/
`IndexesService` now call `QuotaService` instead of hardcoded `const int Max...` fields;
`ProjectEndpoints.Create` gained a projects-per-org check that didn't exist before (Phase 2 capped
databases/tables/columns/indexes per parent but never projects per org). Every trip reuses
`ErrorTypes.GeneralResourceLimitExceeded` — the same type these dimensions already used, not a new
one, per "never reworded casually." New endpoint: `GET /v1/console/projects/{projectId}/quotas`.
**Console:** a "Usage" card on the Project Overview page — five rows (projects/organization,
databases, and the busiest database's tables/busiest table's columns/indexes in this project),
progress bars that go amber at 80% and red at 100%. No org switcher, no org identity shown — the
least-invasive surfacing the prompt asked for, since orgs stay hidden in this UI. Setting an org's
`limits` has no console UI yet (orgs are still invisible) — it's a direct SQL edit, documented in
`docs/self-host.md`.

**Audit log actor tag** (`src/Praxy.Persistence/Entities/Audit.cs`, all 8 `AuditAsync`/inline
`db.AuditLog.Add` call sites): every entry's `Actor` changed from `user:<id>` to `admin:<id>`.
Every entry written to `praxy.audit_log` today is a console-operator action — the ambiguity was that
`user:<id>` is *also* architecture.md §4.3's permission-role grammar for one app user, so an operator
id tagged that way read as an app user's own action to anyone parsing the log. Decided **not** to
start logging app-user data-plane actions this phase (every current call site is already
console-only; adding a new audit-coverage surface is exactly the kind of scope this phase was told
not to invent) — the fix is that the existing admin entries are now unambiguous, and `user:<id>` is
reserved, unused, for if/when app-user actions are ever audited.

**Backup/restore** (`deploy/backup.sh`, `deploy/restore.sh`, `docs/self-host.md`): `pg_dump -n praxy`
(the whole system catalog) plus one `pg_dump -n px_<id>` per database schema, custom format, into a
timestamped directory; restore is `pg_restore --clean --if-exists` for each, with an explicit
"stop the `api` container first" instruction (the schema engine's catalog cache has no way to learn
a raw `pg_restore` changed things out from under a running process). **Proven, not just written up:**
seeded a real table + row through the actual console, ran the exact `backup.sh` commands, then
`DROP SCHEMA ... CASCADE` on **both** `praxy` and the database's `px_<id>` (total loss, not a partial
failure — proves the two dumps are sufficient on their own), restored both, restarted the API cold,
and confirmed via the console that the same project/database/table/row — same ids, same
`physical_name`s, same row content and `_created_at` — came back. Full transcript below.

**Upgrade test** (`docs/self-host.md`'s Upgrading section): no previous tag exists yet, so this
proves the *mechanism* per the prompt's own framing. Migrated a fresh database only as far as
`20260815184658_InitialCatalog` (Phase 0's schema), hand-seeded realistic pre-existing data at that
shape (org, console operator + membership, app project, audit entry), then pointed the **current**
API binary at it and let it boot normally. `CatalogMigrator` logged `Applying 6 catalog
migration(s): [...]`, applied them in order, came up healthy on the first try; all 35 current tables
existed afterward with every seeded row unchanged, and `POST /v1/console/claim` correctly returned
`409 instance_already_claimed` — proof the app's real query path recognizes the pre-upgrade operator
through the new schema, not just that the raw SQL survived. A second boot against the now-current
database logged `Catalog is up to date` and applied nothing, confirming the idempotent no-op a
multi-node rollout depends on.

**Load tests** (`tests/Praxy.LoadTests/` — a runnable console tool, deliberately not an xUnit
project since these are long-running resource-heavy runs against a live instance, not fast CI
assertions):
- `schemas`: creates N databases (each a real `CREATE SCHEMA px_<id>`), measuring `CREATE SCHEMA`
  latency plus a `pg_catalog` scan and fresh-connection time before/after. **1000 databases: 1000/1000
  succeeded, 1.1s total, p50=24ms p95=55ms p99=89ms** — no meaningful catalog-scan degradation at
  this scale on this hardware.
- `websockets`: opens connections spread across N projects (respecting the real per-project
  `MaxConnectionsPerProject` quota rather than raising it), then round-trips a ping/pong on every
  held-open socket. **10 projects × 1000 connections = 10,000/10,000 connected, 3.6s total,
  p50=64ms p95=103ms p99=263ms max=718ms; every single ping→pong round-tripped, p50=2ms p95=7ms
  max=11ms.**
- `fuzz`: a fixed adversarial corpus (SQL-injection-shaped values, type mismatches, cap violations,
  NUL bytes, malformed JSON) plus randomized query payloads against `GET .../rows`, asserting zero
  5xx responses. **First run against pre-Phase-9 code: 411/5034 payloads produced a 500.** Both root
  causes are fixed below; **final run: 0/8034 produced a 500** (728 succeeded as valid queries,
  7304 cleanly rejected with 4xx, 2 hit Kestrel's own 414 for a deliberately-oversized URL —
  below the app layer, not an error-envelope concern).

**Security pass** — architecture.md §11 walked row by row against real code (not memory):

| Threat | Verified | Finding |
|---|---|---|
| SQL injection through table/column keys | ✅ unchanged | `PhysicalNaming.cs` still generates + regex-validates + quotes every identifier at emit; values still always parameterized. |
| Cross-tenant data access | ✅ unchanged | Schema-per-database, `QualifiedTable` used everywhere, no `search_path` reliance. |
| Defence in depth (v1.1 per-project Postgres role) | ✅ still correctly deferred | No `SET LOCAL ROLE` anywhere — matches the roadmap's "v1.1," not silently half-built. |
| Resource exhaustion | ⚠️ **gap found & fixed** | `statement_timeout` was set on DDL/schema-job connections only, never on the shared pool the data plane's row reads/writes actually run on — the general threat-model claim of "on every connection" wasn't true. Fixed: `Praxy:Database:StatementTimeoutSeconds` (default 30) via the connection string's `Options` startup parameter, applied to every pooled connection; DDL paths `SET` their own longer value per session, unaffected. |
| Account enumeration | ✅ verified, one accepted gap | Login still burns a dummy hash either way (`AppAuthService.LoginAsync`). Recovery's timing *isn't* uniform (an unknown email returns immediately, a real one after token-creation + an email send) — but signup already reveals existence directly via `409 user_already_exists`, so this timing variance adds no attacker advantage beyond what's already by-design exposed. Documented, not patched — the fix's complexity wasn't justified by the marginal risk. |
| Cross-origin abuse | ✅ unchanged | `PlatformCorsMiddleware` untouched; `/v1/console` still excluded from CORS entirely. |
| Slow-consumer memory exhaustion | ✅ unchanged | Bounded channel, 256, `Wait` mode, unchanged since Phase 4. |
| Console-project guard | ✅ still holding | `ConsoleGuardTests` (Phase 0) still green, including the check-constraint test. |
| SSRF | ⚠️ **gap found & fixed** | `SsrfGuard` (Phase 6) protected webhook targets only. Messaging's per-project SMTP provider `Host`/`Port` (Phase 8) — exactly as attacker-steerable, any project's own console operator sets it — had **zero** SSRF protection: a provider pointed at `127.0.0.1` or `169.254.169.254` connected directly. Fixed: the range predicate moved to `Praxy.Core.Net.SsrfAddressGuard` (shared, so `Praxy.Webhooks.SsrfGuard` and the new SMTP guard can't drift apart), plus a resolve-then-check pre-connect guard (`SmtpClient` has no connect-callback seam like `SocketsHttpHandler` does, so this is weaker than Webhooks' resolve-and-connect-directly — a narrow DNS-rebinding race remains theoretically possible, documented as a known residual). New `Praxy:Smtp:AllowPrivateNetworkTargets` (default `false`), same shape as Webhooks' own flag. Verified live end to end: a provider pointed at `127.0.0.1:2525`, sent through the real console → send worker → resolver path, landed on the target `failed` with `'127.0.0.1' resolves only to addresses blocked by the SSRF guard.` |
| Rate limits | ✅ unchanged | Still 429 (not the 503 default), `Retry-After`/`RateLimit-*` emitted, still tight on `auth`/`auth-email`, still partitioned on project before IP. |

**Two more bugs found by the query fuzzer**, not in the threat-model table but real crashes:
1. A filter value that didn't match its column's type (`equal("views", "not-a-number")`, a boolean
   compared against a string, etc.) reached Postgres as a bad parameter and came back as an
   unhandled 500. Fixed in `QueryCompiler.ConvertValue` — the existing `RowValues.ToFilterScalar`
   already threw `FormatException` for this, it just wasn't caught and converted to the same 400
   every other malformed-query shape produces.
2. A string containing a NUL character (U+0000) — Postgres' `text` type can't represent it at the
   wire protocol level at all, `22021: invalid byte sequence for encoding "UTF8": 0x00`, no amount of
   parameterization helps. Fixed at the one shared string boundary (`RowValues.RequireString`),
   covering both row writes and query filter values in one change.

**One more bug found by ordinary manual testing** (not fuzzing): a request body missing a
"required" JSON string field binds it to `null` — System.Text.Json doesn't enforce C#'s
non-nullable-reference annotations at runtime — and that `null` reached `Ids.IsValidCustomId`,
`Keys.IsValid`, and `PermissionStrings.Parse` without a guard, throwing `ArgumentNullException`/
`NullReferenceException` (unhandled 500s) for something as ordinary as `POST .../databases` with no
`"key"`. All three fixed at their shared validation boundary, null-safe now, matching the pattern
`Ids.TryParseWire` already used. A full audit of every request DTO for the same shape is not done —
these three were the ones actually exercised; noted as a good next hardening candidate.

**Error-type lint:** `ErrorTypesTests.Registry_covers_every_declared_constant` already does this via
reflection (every `const string` on `ErrorTypes` must appear in `All`, and vice versa) plus the
snake_case regex test plus a no-duplicates test — genuinely comprehensive, not just spot-checked, and
it runs on every `dotnet test`. No new error types were added this phase (every fix above reused an
existing type), so nothing to add to the registry. Decided this already satisfies "lint" — this repo
has no CI pipeline config at all (no `.github/workflows/`; the process is session-per-phase with
`dotnet test` as the gate, per CLAUDE.md), so adding one would be new infrastructure never asked for
by the roadmap, not a hardening fix.

**Docs:** `docs/self-host.md` (new) — the operator's guide: quick start, a config-key reference
table, the backup/restore runbook with its verified transcript, and the upgrade procedure.
`docs/api-reference.md` (new) + `docs/openapi/v1.json` (new, generated, 113 paths) — the OpenAPI
document ships as a committed, regeneratable artifact ("published per release" per architecture.md
§8) since `/openapi/v1.json`/`/scalar/v1` stay Development-only in the running app (they disclose the
full API surface). `sdk/flutter/README.md` (new, workspace overview) plus real content for all three
package READMEs (`praxy_core`, `praxy_flutter`, `praxy_codegen`) and the example app's — all four
were still the unmodified `flutter create`/`dart create` boilerplate before this phase, literally
TODOs. Every code sample was checked against the real source (`TableRef`/`RowCodec`'s actual shape,
`RowEvent`'s sealed-class pattern, exact method signatures) and compile-verified with `dart analyze`
against a throwaway file exercising every snippet before being deleted — two of the first-draft
snippets were wrong (`RowCodec` isn't `extends`-able, it's constructed with named `decode`/`encode`
functions; `liveList` lives on `px.tables`, not `px.realtime`) and would have shipped broken examples
without that check.

## Deviations & notes

- **Quotas are instance-wide defaults (`Praxy:Quotas:*`, Options-record pattern, matching every
  other phase's config shape) overridable per-organization via the jsonb column** — not a second,
  separate per-org config surface. This is what "org-configurable" in the roadmap line actually
  needs (a future multi-tenant SaaS deployment sets different limits per org) without inventing a
  new settings screen for a single-org-per-instance world today.
- **The console's quota usage card reports "busiest" tables/columns/indexes in the project, not a
  per-resource breakdown.** A full per-table drill-down is a bigger console feature than "the
  least-invasive surfacing" the prompt asked for; the busiest-resource number is enough to see a
  project approaching a cap.
- **`statement_timeout`'s fix touches every connection in the shared pool, including the ones
  DDL/schema-job code already manages its own timeout on.** Verified this doesn't conflict: `SET
  LOCAL`/`SET` at the start of a session always overrides whatever the connection's startup `Options`
  set, for the rest of that session — confirmed by the full test suite staying green, including the
  schema-engine tests that rely on longer-running DDL.
- **The account-enumeration timing gap on `/account/recovery` is documented, not fixed** — see the
  security-pass table above. Recorded as a real finding rather than silently accepted; the call not
  to patch it is explicit and reasoned (signup already reveals existence directly), not an oversight.
- **No new EF migration this phase.** `organizations.limits` already existed (Phase 0); the audit
  actor rename is a data-format convention, not a schema change. Confirmed via `git status` on
  `src/Praxy.Persistence/Migrations/` staying empty.

## Known gaps (deliberate, not carried forward past v0.1.0 without a reason)

- **No retention/cleanup job for `praxy.events`/`praxy.audit_log`/`webhook_delivery_attempts`.**
  Flagged as a Phase 9 candidate by Phase 6/7's reports, but the roadmap's actual Phase 9 line never
  named it, and the prompt explicitly warned against silently adopting every flagged gap. Still
  unbounded growth; a real concern for a long-running instance, just not this phase's scope.
- **No per-function execute permissions** (Phase 7's flagged gap) — same reasoning, not named by this
  phase's roadmap line.
- **A full null-safety audit of every request DTO** wasn't done — three real crashes were found and
  fixed at their shared validation boundaries (which protects every call site through them, not just
  the ones tested), but other DTOs with the same "required string binds to null" shape may exist
  unexercised.
- **The SMTP SSRF guard is resolve-then-check, not resolve-and-connect-to-that-address** like
  Webhooks' guard — `System.Net.Mail.SmtpClient` has no connect-callback seam. A sophisticated
  DNS-rebinding attack (public address at check time, private at actual connect time) is
  theoretically still possible; the overwhelmingly common case (a static private-IP/hostname target)
  is fully closed.
- **Org `limits` jsonb has no console UI to set it** — orgs stay hidden per the fixed decisions;
  it's a documented direct-SQL operation today.

## Tests

New this phase: `tests/Praxy.Tests.Unit/OrganizationLimitsTests.cs`, `KeysTests.cs`,
`SsrfAddressGuardTests.cs`; additions to `RowValuesTests.cs` (NUL-byte rejection),
`QueryCompilerTests.cs` (type-mismatch → 400 not a crash), `IdsTests.cs`/`PermissionStringsTests.cs`
(null-safety). `tests/Praxy.Tests.Integration/QuotaTests.cs` (quota enforcement + the usage
snapshot), `AuditLogTests.cs` (every entry matches `^admin:<uuid>$`), `StatementTimeoutTests.cs`
(the real DI-registered `NpgsqlDataSource` honors a configured timeout — not a standalone connection
string), and an addition to `MessagingTests.cs` (a provider pointed at a private address is blocked,
not attempted, through the real send path). Backup/restore and the upgrade test are proven via a
live, scripted transcript (see above) rather than an automated test — both need real `pg_dump`/
`pg_restore`/`dotnet ef database update` and process restarts that don't fit the Testcontainers-per-
test-class shape the rest of the suite uses; the exact commands are captured in `docs/self-host.md`
so the same proof is reproducible.

`dotnet test Praxy.sln`: **404/404** (301 unit + 103 integration, up from 373 in Phase 8).
`npm run build --prefix console` (`tsc -b && vite build`): clean.

## Commands

New this phase:

- `docs/self-host.md` — the full operator's guide (was previously only a "Quick start" in the
  README).
- `docs/api-reference.md` — how the OpenAPI reference ships; regenerate with:
  ```
  ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Praxy.Api &
  curl -sS http://localhost:5090/openapi/v1.json -o docs/openapi/v1.json
  ```
- Backup/restore (self-host stack): `cd deploy && ./backup.sh [output-dir]` and
  `./restore.sh <backup-dir>` (stop the `api` container before restoring — see
  `docs/self-host.md`).
- Load tests: `dotnet run --project tests/Praxy.LoadTests -- schemas|websockets|fuzz [options]` —
  see `tests/Praxy.LoadTests/README.md`. Not part of `dotnet test`.
- New config keys (all under the existing `Praxy:*` binding convention, env vars work too):
  `Praxy:Quotas:MaxProjects|MaxDatabasesPerProject|MaxTablesPerDatabase|MaxColumnsPerTable|MaxIndexesPerTable`
  (defaults match the pre-Phase-9 hardcoded values — 100/20/200/200/64), `Praxy:Database:StatementTimeoutSeconds`
  (default 30), `Praxy:Smtp:AllowPrivateNetworkTargets` (default `false`).
- Flutter SDK: unchanged commands, now with real documentation — `sdk/flutter/README.md` is the
  starting point.

## Owner-test checklist (run by this session, all passing)

Run against an isolated throwaway instance (fresh Postgres container on a scratch port, a second API
instance on a scratch port, the console dev server temporarily repointed at it —
`console/vite.config.ts`'s proxy-target edit reverted afterward, `git diff` on it is empty), driving
the real console UI in the Browser pane:

1. **A quota tripping with a clear error.** Set `organizations.limits` to
   `{"maxDatabasesPerProject": 1}`, created one database (succeeded), tried a second from the
   console: `"This project already has the maximum of 1 databases."` — the Usage card's Databases
   row went red, `1 / 1`.
2. **An audit log entry showing the admin/user distinction.** Every entry in `praxy.audit_log`
   confirmed matching `^admin:[0-9a-f-]{36}$` after the rename — verified live via `psql` against the
   throwaway instance and by the `AuditLogTests` integration test.
3. **A real backup→restore round trip with verified data.** Full transcript above — total loss of
   both `praxy` and one `px_<id>` schema, restored from `backup.sh`'s exact output, same project/
   database/table/row confirmed via the console after a cold API restart.
4. **The load test scripts running and reporting results.** All three (`schemas`, `websockets`,
   `fuzz`) run to completion with numbers reported above; the fuzz run's first pass found two real
   bugs, the fixed code's final pass found zero.
5. **The security-pass findings written down.** The table above; two real gaps found and fixed
   (statement_timeout, Messaging SSRF), one accepted-and-documented (recovery timing), everything
   else confirmed unchanged.
6. **The error-type lint passing.** `ErrorTypesTests` (all three: snake_case, no-duplicates,
   registry-covers-every-constant) green.

## Next: v0.1.0

Praxy's roadmap ends here. Auth, the dynamic schema engine, the full data plane, realtime, a native
Flutter client, webhooks, functions, messaging, and now hardening — quotas enforced and surfaced, an
unambiguous audit trail, a proven backup/restore and upgrade path, load-tested at the scales the
roadmap named, a real security pass that found and fixed five genuine bugs rather than confirming a
checklist, and documentation an operator who's never read this codebase can actually follow.

There is no `docs/handoff/phase-10-prompt.md` — this was the last phase. `v0.1.0` is tagged on this
commit.
