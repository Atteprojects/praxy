# Session task — project rename/delete, database rename, membership role edit

## Why this exists

Three console operations exist half-finished — the read path works, the write
path for a specific field never got built:

- **Projects can be created and read, never renamed or deleted.**
  `ProjectEndpoints.cs` is `GET`/`POST`/`GET one`/`GET quotas`. A project
  created by mistake, or one that outlives its purpose, occupies an
  organization's project quota slot forever. There is no way to reclaim it.
- **Databases can be created, listed and deleted, never renamed.** Tables
  already support a name-only `PATCH` (`TablesService.UpdateAsync`); databases
  do not have the equivalent.
- **Team membership roles can be granted and revoked, never edited in the
  console.** The client-facing surface already has
  `PATCH /v1/teams/{teamId}/memberships/{membershipId}` — an app user's own
  team management can do this. The console admin surface can add and remove a
  membership but not change its roles, so an operator's only path to "promote
  this member" is delete-then-re-add, which briefly removes them from the team
  and loses whatever else the membership carried.

This is item #5 of the post-v0.1.0 gap analysis. Items #1–#3 are merged
(function permissions, admin user management) or written up
(`audit-log-read-surface-prompt.md`). Item #4 (CI + retention) may or may not
have landed by the time this runs — nothing here depends on it.

Work on a new branch off `main`. Read `CLAUDE.md` first. This is a single
post-Phase-9 feature, not a numbered phase — do not re-plan the roadmap or pull
work forward.

## Non-goals — do not build these

- **No project *key* rename.** `Project.Id` is the wire-visible id, chosen at
  creation (custom or generated) — the same "physical identifier never
  changes, only the display name does" rule `TableDef`/`Database` already
  follow. Rename touches `Name` only.
- **No organization rename/create/delete.** Explicitly out of scope per
  `docs/handoff/org-scoped-console-prompt.md` — orgs stay single-per-instance
  and unmanaged beyond read.
- **No bulk delete, no project archiving/soft-delete.** A hard, confirmed
  delete is what the gap analysis asked for.
- **No membership role *picker* UI upgrade.** The existing comma-separated
  text input for roles (`TeamsPage.tsx`) was called out as console-polish in
  the Phase 1 report and is still an acceptable input for a free-form list —
  reuse its exact shape for editing, do not redesign it here.

## Scope

1. **`DELETE /v1/console/projects/{projectId}`** and
   **`PATCH /v1/console/projects/{projectId}`** (name only).
2. **`PATCH /v1/console/projects/{projectId}/databases/{databaseId}`**
   (name only) — and its data-plane equivalent
   `PATCH /v1/databases/{databaseId}` for symmetry with every other console/
   data-plane pair (see how `DatabaseEndpoints.cs`/`ConsoleDatabaseEndpoints.cs`
   already both exist for every other database operation).
3. **`PATCH /v1/console/projects/{projectId}/teams/{teamId}/memberships/{membershipId}`**
   on the console admin surface, mirroring the client-facing one already at
   `TeamEndpoints.cs`.
4. Console UI for all three: rename inputs, a typed-name-confirm delete for
   projects, an editable roles cell on the membership row.
5. Integration tests.

## Landmines — read before writing code

Verified against current `main`, not recalled.

- **Deleting a project's physical database schemas needs the existing
  per-database delete path, not a bare row delete.** Every entity with a
  `ProjectId` FK cascades at the Postgres level
  (`OnDelete(DeleteBehavior.Cascade)`, checked across all of them in
  `PraxyDb.cs`) — so `db.Projects.Remove(project)` *will* cascade-delete every
  user, key, team, webhook, messaging config and platform automatically. **It
  will not drop a single physical `px_<id>` schema.** `Database` rows are
  metadata; the actual Postgres schema (`CREATE SCHEMA px_...`) is DDL with no
  FK relationship to anything, so a cascading metadata delete leaves every
  schema orphaned on disk forever, silently.

  The correct sequence, which `DatabasesService.DeleteAsync`
  (`src/Praxy.Tables/DatabasesService.cs:67`) already implements correctly for
  one database — **reuse it, in a loop, before removing the project row**:
  list every database in the project (`DatabasesService.ListAsync`), call
  `DeleteAsync(database, force: true, ct)` on each (this does the
  `DROP SCHEMA ... CASCADE` and invalidates the catalog cache correctly), *then*
  delete the project itself and let the FK cascade take the rest.

- **Function containers need the same treatment.**
  `FunctionsService.DeleteAsync` (`src/Praxy.Functions/FunctionsService.cs`)
  already evicts a function's warm-pool container before removing its row.
  Loop over every function in the project and call it, for the same reason as
  above: a bare cascade delete would leave running containers with no database
  row to ever clean them up.

- **Do this all in one transaction, or accept and document that you can't.**
  `SchemaDdl.InTransactionAsync` is the existing wrapper
  (`DatabasesService.DeleteAsync` uses it for a single database). Multiple
  `DROP SCHEMA` calls across multiple databases plus a project-row delete is a
  bigger unit of work than anything currently wrapped this way — decide
  whether it stays one transaction or several, and say which and why. A
  half-completed project delete (some schemas dropped, project row still
  present) is a state an operator needs to be able to retry from, not one that
  corrupts silently.

- **Project delete must require `force=true`**, the same convention
  `DatabasesService.DeleteAsync`/`TablesService.DeleteAsync` already use for
  destructive operations (`GeneralForceRequired`). This is the most
  destructive single operation in the console; do not make it a bare `DELETE`
  with no confirmation at the API layer, even though the console UI will also
  ask.

- **Project/database scoping must go through the same access check the read
  path already uses**, not a new one. `ProjectEndpoints.AccessibleProjects`
  (joins on `OrganizationMembers`) is what already keeps the reserved
  `console` project (`OrganizationId` is `NULL`) out of every operator's
  project list. Route rename/delete through the same helper rather than
  writing a second scoping query that might not exclude it the same way.

- **The membership-roles `PATCH` needs `ownerOnly: true`-equivalent
  authorization on the console admin path**, matching the client-facing
  handler's `RequireTeamAccessAsync(..., ownerOnly: true)`
  (`TeamEndpoints.cs`). Check what that means for the console admin surface —
  operators are already privileged relative to app users, so this may
  collapse to "any operator with `ConsoleProjectFilter` access," but confirm
  that deliberately rather than copying the app-user gate verbatim.

## Console

- **Project rename**: add to `ProjectOverviewPage.tsx`'s existing settings
  area if there is one, or a small inline-edit next to the project name in
  `PageHeader` — your call on where it reads best, but do not add a new route
  for a single text field.
- **Project delete**: a danger-zone section with typed-name confirmation,
  the same shape `TableSettingsPage.tsx` and `DatabasesPage.tsx`'s
  database-delete already use — a dedicated typed-name-confirm input, not the
  one-click `ConfirmButton` used for less destructive row-level actions
  (`DatabasesPage.tsx` has a comment explaining exactly this distinction; read
  it before choosing which pattern fits a project delete). State plainly what
  it removes — every database, every user, every function, every key —
  before the confirm input, not after.
- **Database rename**: `DatabaseLayout.tsx` or wherever the database's own
  settings live today — check whether a database-settings screen already
  exists (tables have one; confirm whether databases do) before adding a new
  one.
- **Membership role edit**: `TeamsPage.tsx`'s existing membership row. Turn
  the roles `Badge` list into an editable field on demand (click to edit, same
  comma-separated input the add-member form already uses), not a separate
  modal.

Available primitives: `PageHeader`, `IdChip`, `ConfirmButton`, `Field`,
`ErrorNote`, `Spinner`, `useToast` (`console/src/components/`). Hooks go
alongside the existing ones in `console/src/api/databases.ts` and
`console/src/api/auth.ts` — a new `console/src/api/projects.ts` if one does
not exist yet for project-level mutations (check first; `queries.ts` may
already cover project reads).

## Tests

`tests/Praxy.Tests.Integration/` — Testcontainers, `postgres:17-alpine`, shared
collection fixture. `ProjectApiTests.cs` and `OrganizationApiTests.cs` are the
closest neighbours for project-level tests; `SchemaEngineTests.cs` for the
database-delete precedent to mirror for rename.

- Renaming a project/database updates `Name`, leaves the physical id/schema
  name untouched, and reads back through `GET`.
- Deleting a project with a database drops that database's physical schema —
  assert this against `information_schema.schemata` or by attempting a query
  against the dropped schema and getting a clear failure, not just that the
  metadata row is gone.
- Deleting a project with a function evicts its warm-pool container if one
  exists (or, if you decide the test suite shouldn't spin up Docker for this
  one, assert the function row and its deployments are gone and note the
  container-eviction path is covered by unit-level reasoning, not an
  integration test — say which you chose).
- Deleting a project without `force=true` is a clean `400
  general_force_required`, not a silent no-op or a 500.
- A second operator cannot rename or delete another org's project — `404`,
  the existing `ConsoleProjectFilter`/`AccessibleProjects` boundary.
- Editing membership roles updates what `GET .../memberships` returns and
  what the affected user's resolved `team:<id>/<role>` roles look like
  (`GET /v1/account/roles` for that user's session).

## Done means

- `dotnet test` green (needs Docker). Currently 324 unit + 146 integration
  (more if item #4 landed first — check the actual count when you start).
- `npm run build --prefix console` green.
- OpenAPI snapshot regenerated — `OpenApiDocumentTests` fails the build if it
  drifts, if any new operation lacks `.Produces<T>()`, or if the error
  envelope is missing. `servers` stays pinned to `/`; a localhost URL in the
  diff means something regressed.
- `git status` clean, conventional commits, on a new branch off `main`.
- Click-tested against a **throwaway** stack, not the persistent dev one:
  rename a project, rename a database, edit a membership's roles, then delete
  a project that has a database and a function in it and confirm the schema
  is actually gone in Postgres, not just the console.
- State in your final summary: whether project delete runs as one transaction
  or several and why, and what `ownerOnly`-equivalent check you landed on for
  the console membership-roles edit.

## Deploying (only if the owner asks)

`praxycore.dev`, procedure in `docs/self-host.md`'s Upgrading section — backup
first, `git pull origin main`, then
`docker compose -f deploy/docker-compose.yml --profile https up -d --build`.
Needs an SSH key that lives on the owner's own machine, so it cannot run from
a cloud session. No schema migration expected from this feature (all three
operations use existing columns), but confirm that's still true before
deploying and back up regardless — project delete is destructive by nature.
Do not deploy unless asked.
