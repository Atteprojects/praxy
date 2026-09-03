# Session task — make the audit log readable (API + console)

> **Status: shipped.** Verified 2026-09-03 against the code, not assumed — see `console/src/screens/AuditLogPage.tsx` and its API surface.
> No `audit-log-read-surface-report.md` was written at the time, so this prompt used to look like
> outstanding work when scanning `docs/handoff/` for prompts without reports.

## Why this exists

Praxy writes audit entries from eight endpoint files and **64 distinct action
strings**, and nothing anywhere reads them. There is no endpoint, no console
screen, and one integration test that only asserts a row was written. The live
instance has ~77 rows nobody can see without `psql`.

Phase 9's roadmap line asked for "audit log (admin actions distinguished from
user actions)". The distinguishing shipped — `AuditLogEntry.Actor` is
deliberately `admin:<id>` rather than `user:<id>`, with a comment explaining why
— but the half that makes any of it useful did not.

This is item #3 of the post-v0.1.0 gap analysis. Items #1 (function execute
permissions, data-plane rate limits) and #2 (operator user management) are both
merged and deployed; `docs/handoff/admin-user-management-report.md` is the most
recent worked example of the house style.

Work on a new branch off `main`. Read `CLAUDE.md` first. This is a single
post-Phase-9 feature, not a numbered phase — do not re-plan the roadmap or pull
work forward.

## Non-goals — do not build these

- **No retention or cleanup job.** That is gap #4, and it covers
  `praxy.events` and `webhook_delivery_attempts` too — doing one table in
  isolation here would prejudge that design. Build the read surface so it stays
  fast on a large table; do not delete anything.
- **No diff/detail column.** See the landmine below. Recording *what changed*
  is a real feature with a real schema cost; this task surfaces what is already
  recorded.
- **No export (CSV/JSON download), no alerting, no retention policy UI.**
- **No app-user-facing activity log.** `GET /v1/account/logs` (Appwrite has one)
  is a different feature for a different audience.

## Scope

1. **`GET /v1/console/projects/{projectId}/audit`** — the project's entries,
   newest first, filterable and paginated. Behind `RequireOperatorFilter` +
   `ConsoleProjectFilter` like every other console admin route.
2. **Decide where instance-level entries surface.** See the landmine — they have
   a NULL `project_id` and are invisible to any project-scoped query.
3. **Console screen.** Project-level, under the `Manage` section of the nav
   alongside API keys and Platforms.
4. **Extend the actor vocabulary to `key:<id>` and audit the server surface.**
   Reasoning below — this is in scope deliberately, not scope creep.
5. **An index that makes the query sane**, with its migration.

### Why (4) is in scope

The server API (`/v1/users/…`, API key + scope) writes **no** audit entries, and
never has — every writer is a console surface. That was defensible when a key
could only set a user's status and labels. Since item #2 shipped, **a key with
`users.write` can reset any user's password and change any user's email**, and
none of it leaves a trace.

A read surface that silently omits that is worse than no read surface: an
operator reading "nobody changed that password" would be wrong, and would have no
way to know. Either audit the server surface, or make the screen state plainly
that it covers console actions only. **The first is the right answer** — the
second is a permanent asterisk on a security feature — but if the scope turns out
larger than it looks, say so and take the second rather than shipping a log that
lies by omission.

`AuditLogEntry.Actor`'s XML doc defines the vocabulary (`admin:<id>`, reserved
`user:<id>`, `system`). Extend it there, in the same comment, so the next reader
sees one list.

## Landmines — read before writing code

Verified against current `main`, not recalled.

- **The only index is `project_id`** (`PraxyDb.cs:234`). There is nothing on
  `created_at`, so "newest first" — the entire point of a log view — sorts
  unindexed. You need `(project_id, created_at DESC)` and a migration for it.
  Filters on actor/action want covering too; decide how far to go, but do not
  ship the screen on a plain sequential scan.

- **`ProjectId` is nullable, and instance-level entries use it.**
  `instance.claim` (`ConsoleAuthEndpoints.cs`) writes an entry with no project.
  A project-scoped query will never show it. Options: a separate
  `GET /v1/console/audit` for instance-level entries; include NULLs in every
  project's view (wrong — they are not that project's events); or leave them
  unreachable and say so. Pick deliberately.

- **There is no FK from `audit_log` to `projects`.** Deleting a project leaves
  its audit rows behind with a dangling `project_id`. That is arguably correct —
  an audit trail that a delete can erase is not much of an audit trail — but the
  read surface has to tolerate rows whose project no longer exists, and you
  should not "fix" it by adding a cascade.

- **`Action` and `Resource` are all there is. There is no record of what
  changed.** `users.email.update` on `user/<id>` does not say from what, to what.
  Do not invent a jsonb detail column here (non-goal), but make sure the UI never
  implies it can answer "what did it change to" — the honest framing is "who did
  what, to which resource, when".

- **The log does NOT cover data-plane activity, and the action names actively
  mislead about this.** `rows.create` / `rows.update` / `rows.delete` come from
  `ConsoleRowEndpoints.cs` — the console's own row editor — *only*. A row created
  through the data plane by an app user or an API key writes nothing here. An
  operator seeing `rows.create` in a log labelled "audit" will reasonably assume
  it covers all row writes. It does not. The screen must say what it covers.

- **`ListParams` is copy-pasted** in `FunctionEndpoints.cs:425`,
  `MessagingEndpoints.cs:412` and `WebhookEndpoints.cs` (limit clamped 1–100,
  default 25; offset ≥ 0). Console list endpoints are offset-paginated, unlike
  the data plane's keyset pagination — follow the console convention here. If you
  want to hoist the helper somewhere shared, that is a welcome small cleanup, not
  a requirement.

- **`admin:<id>` cannot be turned into a name with what exists today.** There is
  no endpoint to look up a console operator by id — `GET /v1/console/account`
  returns only the *caller*. And do not reach for `RoleLabel`/`useUser` from
  `RolePicker.tsx`: those resolve **app users inside a project**
  (`/console/projects/{projectId}/users/{userId}`), a different table scope
  entirely from console operators (`project_id = 'console'`), so an actor id will
  simply 404 there.

  The saving grace is that the instance is single-operator by construction: claim
  is one-shot (`ConsoleAuthService.IsClaimedAsync`) and there is no operator-invite
  endpoint, so every `admin:<id>` in the log today is the signed-in operator. The
  cheap honest move is to render "you" when the id matches
  `GET /v1/console/account` and the raw id otherwise. Adding an operator-lookup
  endpoint is a defensible alternative — say so if you take it — but do not
  quietly assume one exists.

- **The 64 action strings are an unmanaged vocabulary.** They are string
  literals at each call site with no central list, unlike `ErrorTypes.All` which
  is registered and unit-tested. A filter UI needs to know what values exist. You
  may want to do for actions what `ErrorTypes` does for error types — a central
  list plus a test asserting the shape — but weigh it: it touches every writer.

## Console

Screen lives at `/project/$projectId/audit`, in the `Manage` nav group
(`console/src/router.tsx`, and the nav in `ProjectLayout.tsx`).

Use `<DataGrid />`, not `<DataTable />` — it is the primitive the other log-like
screens use (`WebhookDeliveriesPage`, `FunctionExecutionsPage`,
`RealtimeInspectorPage`) and it brings virtualization, which is what a
long-lived table needs.

Available primitives: `PageHeader`, `IdChip`, `Badge`, `EmptyState`, `Sheet`,
`Spinner`, `timeAgo` (`console/src/components/ui.tsx`). Hooks go in a new
`console/src/api/audit.ts` following the shape of `console/src/api/webhooks.ts`.

Design notes:

- Actor ids are opaque and **not resolvable through any existing endpoint** —
  see the landmine below. Whatever you show, keep the raw value visible for
  copying.
- Filters worth having: action, actor, resource, and a date range. Put them in
  the URL like the row browser's filters do, so a filtered view is linkable.
- Timestamps are ISO-8601 UTC end-to-end (a cross-phase rule). Render local, but
  never round-trip a local string.
- Empty state: a project with no entries is normal, not an error.

## Tests

`tests/Praxy.Tests.Integration/` — Testcontainers, `postgres:17-alpine`, shared
collection fixture. `ConsoleUserManagementTests.cs` is the most recent example of
the style; `AuditLogTests.cs` is the existing one-test file to grow. Cover:

- Entries come back newest-first, and paginate — assert the second page is not
  the first, which offset pagination gets wrong more often than you would think.
- Each filter narrows correctly, and filters compose.
- A second operator gets `404 project_not_found` (the `ConsoleProjectFilter`
  boundary) and the reserved `console` project is refused — `ConsoleGuardTests.cs`
  shows the shape, and `ApiTestBase.CreateSecondOperatorAsync` now exists for the
  first half.
- Whatever you decided about instance-level (NULL-project) entries, asserted.
- If you audit the server surface: a key-driven password reset writes an entry
  with a `key:<id>` actor, and it shows up in the read surface.
- An entry whose project has since been deleted does not break the query.

## Done means

- `dotnet test` green (needs Docker). Currently 324 unit + 146 integration.
- `npm run build --prefix console` green.
- **OpenAPI: this is enforced now.** `OpenApiDocumentTests` fails the build if
  the committed snapshot drifts, if any operation lacks a documented response, or
  if the error envelope is missing. New endpoints need `.Produces<T>()` at the
  map call. Regenerate per `docs/api-reference.md` — and note `servers` is pinned
  to a relative `/`, so the snapshot is host-independent; if you see a localhost
  URL in the diff, something regressed.
- `git status` clean, conventional commits, on a new branch off `main`.
- Click-tested against a **throwaway** stack, not the persistent dev one on 5090.
  Generate real entries by doing real things in the console (create a database,
  reset a user's password, revoke a key), then read them back through the screen.
- State in your final summary: where instance-level entries ended up, whether you
  audited the server surface, and what you did about the action vocabulary.

## Deploying (only if the owner asks)

`praxycore.dev`, procedure in `docs/self-host.md`'s Upgrading section — backup
first, `git pull origin main`, then
`docker compose -f deploy/docker-compose.yml --profile https up -d --build`.
Needs an SSH key that lives on the owner's own machine, so it cannot run from a
cloud session. This feature adds a migration (the index), so the backup matters.
Do not deploy unless asked.
