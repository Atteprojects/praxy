# Session task — CI, and a retention job for the tables that grow forever

## Why this exists

There is no `.github/` directory. Roughly 470 tests (`dotnet test`) and a tagged
`v0.1.0` release exist with nothing running them automatically on push — every
merge to `main` today is trusted on the strength of whoever ran the suite
locally before pushing.

Separately, three tables have grown without bound since the phase that created
them, each one flagged and each time explicitly declined as out of that phase's
scope: `praxy.events` (Phase 3), `webhook_delivery_attempts`/`webhook_deliveries`
(Phase 6), `praxy.audit_log` (Phase 9's own report named this exact gap and
said "not this phase's scope"). This is the phase.

This is item #4 of the post-v0.1.0 gap analysis. Items #1–#3 are written up in
`docs/handoff/function-permissions...` (merged), `admin-user-management-*`
(merged), and `audit-log-read-surface-prompt.md` (written, not yet
implemented as of this prompt).

Work on a new branch off `main`. Read `CLAUDE.md` first. This is a single
post-Phase-9 feature, not a numbered phase — do not re-plan the roadmap or pull
work forward.

## Non-goals — do not build these

- **No function image/container cleanup.** Genuinely a different, harder
  problem: `DockerExecutor` has no image-listing or image-removal method at
  all today (only `StopAndRemoveAsync` for a *container*), and there is no
  tracking of which images are still referenced by an active deployment versus
  superseded. Doing this safely means new Docker API calls and a
  referenced-image query, not a DELETE with a WHERE clause. Flag it as a
  follow-up in your final summary; do not fold it into this task.
- **No retention *policy UI*.** Configure it the way every other limit in
  Praxy is configured — `Praxy:Retention:*` in config, documented in
  `docs/self-host.md`'s table, not a per-project console screen. Consistent
  with `Praxy:RateLimits:*`/`Praxy:Quotas:*`.
- **No deploy/release automation in CI.** Build and test only. Deploying to
  `praxycore.dev` needs an SSH key that lives on the owner's machine and stays
  a manual, owner-approved step — do not wire CI to touch it.
- **No retention for `schema_jobs`, `function_executions`, or `messages`.**
  Not named by the gap analysis or any prior phase report; scope creep here
  prejudges a design nobody has asked for yet.

## Scope

1. **`.github/workflows/ci.yml`** — on push and PR to `main`: build the
   solution, run `dotnet test` (needs a Docker daemon for Testcontainers —
   `ubuntu-latest` GitHub-hosted runners have one preinstalled at
   `/var/run/docker.sock`; do not add a `services:` Postgres container,
   Testcontainers manages its own), and build the console
   (`npm ci && npm run build --prefix console`). `TreatWarningsAsErrors` is
   already `true` in `Directory.Build.props` and `npm run build` is
   `tsc -b && vite build`, so a type error already fails the build — no
   separate lint step is required.
2. **A retention `BackgroundService`** (own project or `Praxy.Api`'s
   `Infrastructure/`, your call) that periodically deletes rows past their
   configured age from `praxy.events`, `praxy.webhook_deliveries` (which
   cascades to `webhook_delivery_attempts` — see landmine), and
   `praxy.audit_log`.
3. **`RetentionOptions`** following the `WebhookOptions`/`MessagingOptions`/
   `QuotaOptions` record-of-defaults shape, bound from `Praxy:Retention:*`.
4. **Document it** in `docs/self-host.md`'s config table, same row shape as
   the rate-limit rows added on 2026-08-19.

## Landmines — read before writing code

Verified against current `main`, not recalled.

- **You cannot verify CI actually runs from this environment.** Writing YAML
  that looks right is not the same as a green run. After pushing the branch,
  open a PR (or push to a branch and check the Actions tab) and confirm the
  workflow actually executes and passes — paste or describe the real result in
  your final summary, not an assumption that it would work.

- **`praxy.events` has two independent, unclaimed-until-set consumer columns**
  — `WebhooksDispatchedAt` and `FunctionsDispatchedAt`
  (`src/Praxy.Persistence/Entities/Outbox.cs`). Realtime never reads this
  table at all (it fans out purely from the in-process event bus), so it is
  not a third consumer to worry about — but the other two are real. **Only
  delete a row once both columns are non-null.** An age-based delete with no
  regard for claim state would silently drop an event a dispatcher hasn't
  gotten to yet — e.g. a function whose build is stuck, or a webhook
  subscription mid-backoff — and that dispatcher would never see it. If you
  find rows past the retention window that are still unclaimed, the safe
  default is to skip them and let the next sweep re-check, not force-delete.

- **`webhook_delivery_attempts` is not the table to target directly.** It
  cascades from `WebhookDelivery` (`OnDelete(DeleteBehavior.Cascade)` on
  `DeliveryId`, `PraxyDb.cs:266`). Deleting the parent `WebhookDelivery` row
  deletes its attempts for free; deleting attempts alone leaves the parent
  row (and the console's delivery list) unchanged. Only delete deliveries in
  a terminal state (`succeeded`/`failed`) — never `queued`/`delivering`.

- **`WebhookDelivery.RedeliveredFromId` has no FK constraint**
  (`PraxyDb.cs`'s `Entity<WebhookDelivery>` mapping has no
  `HasForeignKey(x => x.RedeliveredFromId)`). Deleting an old delivery that a
  later redelivery points back to leaves that reference dangling — no
  constraint violation, just a "redelivery" badge in the console
  (`WebhookDeliveriesPage.tsx`) pointing at an id that 404s. Decide whether
  that is acceptable (probably yes — the redelivery's own row is what matters,
  the origin is provenance) and say so; do not add a cascade to "fix" it.

- **Pick defaults that do not undercut gap #3.** If the audit-log read surface
  lands before or after this task, a retention window of a few days would make
  it nearly useless the moment it ships. A generous default —
  90 days is a reasonable starting point, stated as a default you chose, not a
  number handed to you — keeps both features coherent regardless of build
  order.

- **The sweep should follow `FunctionPoolSweeper`'s shape**
  (`src/Praxy.Functions/FunctionPoolSweeper.cs`): a `while` loop, a try/catch
  around the actual work that logs and continues rather than crashing the
  host, then `Task.Delay` on a configurable interval. Do not reinvent this
  shape; that file is the whole pattern in under 30 lines.

- **Use `ExecuteDeleteAsync`, not load-then-remove**, for the actual deletes —
  `TablesService.cs:176`
  (`db.TablePermissions.Where(...).ExecuteDeleteAsync(ct)`) and
  `DatabasesService.cs:79`
  (`db.SchemaJobs.Where(...).ExecuteDeleteAsync(ct)`) are the existing
  precedent. Loading rows into the change tracker just to delete them is real
  memory for a table that might have millions of rows.

## Tests

`tests/Praxy.Tests.Integration/` — Testcontainers, `postgres:17-alpine`, shared
collection fixture. There is no existing retention test to extend; a new file
is right. Cover:

- An old, fully-claimed event row is deleted; an old row claimed by only one
  of the two dispatchers is not.
- An old `succeeded`/`failed` delivery is deleted along with its attempts (the
  cascade, exercised end to end, not assumed); an old `queued`/`delivering`
  one is not.
- An old audit-log row is deleted; a recent one is not.
- The retention window is configurable via `ExtraSettings`, following
  `RateLimitAndCorsTests.cs`'s pattern of overriding config per test class.

## Done means

- `dotnet test` green (needs Docker). Currently 324 unit + 146 integration —
  this task should only add tests, never break existing ones.
- `npm run build --prefix console` green (should be untouched by this task,
  but the CI job needs to prove it works, so verify locally too).
- **A real, verified-green GitHub Actions run** — see the landmine above. This
  is the actual deliverable of half this task; "the YAML looks right" does
  not satisfy it.
- OpenAPI: this task adds no endpoints, so the snapshot should not change. If
  `OpenApiDocumentTests` reports drift, something else moved — investigate,
  do not regenerate blindly to make the test pass.
- `git status` clean, conventional commits, on a new branch off `main`.
- `docs/self-host.md` documents `Praxy:Retention:*` with its defaults.
- State in your final summary: the retention windows you chose and why, the
  actual CI run's status (link or description), and confirmation the events
  table's "only delete if both dispatchers claimed it" rule has a passing
  test proving the unclaimed case is *not* deleted.

## Deploying (only if the owner asks)

`praxycore.dev`, procedure in `docs/self-host.md`'s Upgrading section — backup
first, `git pull origin main`, then
`docker compose -f deploy/docker-compose.yml --profile https up -d --build`.
Needs an SSH key that lives on the owner's own machine, so it cannot run from
a cloud session. This feature adds a new background service but no schema
migration (unless you add an index to make the retention query itself
efficient — if you do, say so and treat it like any other migration, backup
first). Do not deploy unless asked. CI landing does not change the deploy
procedure — it still runs by hand.
