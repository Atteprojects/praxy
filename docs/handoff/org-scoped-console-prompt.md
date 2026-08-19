# Session task — show which organization owns the projects (API + console)

## Why this exists

Appwrite's console auto-selects the operator's organization, puts its id in the
URL (`/console/organization-<id>`), and renders the projects list *as the org's
page* — the org name is the page title, with Projects/Members/Usage/Billing
tabs under it. Praxy's console home (`/`) lists projects with no indication of
which organization owns them, and the org id never appears in the URL.

Work on a new branch off `main`.

Read `CLAUDE.md` first. This is a single post-Phase-9 feature, not a numbered
phase — do not re-plan the roadmap or pull work forward.

## Read this before you push back

`docs/architecture.md:11` says organizations are *"modeled fully from Phase 0
(owner/member only), **hidden in UI until needed**"*, and several XML doc
comments repeat it (`Organization`, `QuotaService.GetSnapshotAsync`,
`ProjectEndpoints.GetQuotas`, `ProjectOverviewPage.tsx:37`).

**The owner has decided it is now needed.** This task is the owner overriding
that line — it is not a settled decision you are being asked to re-litigate, and
it is not a mistake in the prompt. Implement it, and update
`docs/architecture.md:11` plus those stale "hidden in the UI" comments so the
docs stop contradicting the code. What stays deferred is *multi-org* (see
Non-goals).

## Non-goals — do not build these

- **No org switcher, no multi-org.** The instance is single-org by construction:
  `ProjectEndpoints.Create` picks `OrganizationMembers.OrderBy(CreatedAt).First()`
  for the operator, and `ConsoleAuthService.cs:57` creates exactly one
  `Organization { Name = "Personal" }` at claim time. Multitenancy is deferred
  per CLAUDE.md's fixed decisions.
- **No org create/delete/rename endpoints**, no members-management UI, no
  billing/usage tabs. Appwrite's screenshot has them; Praxy does not need them to
  answer "which org owns this project".
- **No new org concept in the data model.** Everything needed is already there.

## Scope

1. **Org read endpoints** on the console surface (`src/Praxy.Api/Endpoints/` —
   likely a new `ConsoleOrganizationEndpoints.cs`, mapped in `Program.cs`
   alongside the others):
   - `GET /v1/console/organizations` — orgs the operator belongs to (today:
     exactly one)
   - `GET /v1/console/organizations/{organizationId}` — 404s for an org the
     operator is not a member of
   Both behind `RequireOperatorFilter`, scoped through `OrganizationMembers`
   exactly as `ProjectEndpoints.AccessibleProjects` already does. A new
   `ErrorTypes.OrganizationNotFound` is needed — add it to the unit-tested `All`
   list in `src/Praxy.Core/Errors/ErrorTypes.cs`.
2. **Decide the id format question below**, then make the wire consistent.
3. Console: org-scoped home route + org identity in the UI.
4. Integration tests.
5. Regenerate the committed OpenAPI snapshot.

## Design decision to make first — the id format bug

`ProjectResponse` (`ProjectEndpoints.cs:13`) declares
`Guid? OrganizationId` and emits it raw. Every other id in Praxy goes over the
wire through `Ids.Wire()` as hex32. The live API today returns **both formats in
the same object**:

```json
{ "id": "01a015e3aadc7d89864a1b9f13ee6853",
  "organizationId": "01a015e3-6f3c-7f84-9cf5-a563b762e6aa" }
```

(Project ids are strings, so they are unaffected; `organizationId` is the odd one
out.) Before any of this reaches a URL, pick one:

- **(a) Wire-format it** — `Ids.Wire(p.OrganizationId)`, parse back with
  `Ids.TryParseWire`, matching every other id in the system. **Recommended.**
  It is a breaking change to a field that, as far as the repo shows, no client
  reads yet — grep to confirm before you rely on that.
- **(b) Leave it hyphenated** and make the new org routes accept that format.
  Cheaper now, but permanently inconsistent, and the org id is about to become a
  URL segment users see.

State your choice and reasoning in the report. Whichever you pick, the org
endpoints, `ProjectResponse`, and the console route must all agree on **one**
format — a mismatch here is a 404 that only reproduces on a real instance.

## Landmines — read before writing code

- **The `console` project has a NULL `organization_id`** (verified in the live
  dev catalog). `AccessibleProjects` joins on `OrganizationId`, which is
  precisely why the reserved console project can never leak into the operator's
  project list. Any org-scoped project query must preserve that. Do not make
  `organizationId` non-nullable.
- **`Organization.Name` is hardcoded `"Personal"`** at claim
  (`ConsoleAuthService.cs:57`) and there is no rename path. Appwrite shows
  "Personal projects". Either display the stored name as-is or change the seed
  string, but **do not build a rename UI** and do not render it as an editable
  field — that implies an endpoint that does not exist.
- **The empty-instance onboarding must not break.** `ProjectListPage` returns
  `<CreateProjectCard standalone />` when `total === 0` — deliberately "no
  chrome, minus the org ceremony". A redirect to an org page that renders a
  heading and tabs would regress that first-run screen. Decide what an
  org-scoped route does with zero projects and keep the bare card.
- **`/` is the post-login target**, hardcoded twice in
  `console/src/screens/LoginPage.tsx` (lines 73 and 112), and `projectListRoute`
  is `path: "/"` under `shellRoute` in `router.tsx`. If the home route moves,
  both redirects and any bookmark to `/` must still land somewhere sane —
  prefer keeping `/` and redirecting it to the resolved org, so no existing link
  breaks.
- **The org id is not in the session.** There is no "current org" on the
  operator's session or token, so the console has to resolve it (list orgs, take
  the first) before it can build the URL. That resolution is a loading state on
  the very first screen after login — do not let it flash the project list at a
  bare `/` and then jump.
- **Do not duplicate quota vocabulary.** `ProjectOverviewPage` already renders
  `"Projects (organization)"` from the existing `/quotas` snapshot
  (`projectsUsed` / `projectsMax` are org-level). Reuse that wording rather than
  inventing a second one, and note that `QuotaService.GetSnapshotAsync` resolves
  the org from a *project* id — it has no org-id entry point today.
- `ProjectResponse` already carries `organizationId`, and
  `console/src/api/types.ts:34` already types it as `organizationId: string | null`.
  The data is on the wire; the console simply ignores it. Check what you actually
  need before adding fields.

## Console

Recommended shape, matching what the owner asked for:

- Keep `/` as a resolver that redirects to `/organization/$organizationId`
  (or Appwrite's literal `organization-<id>` — your call, but say which and why).
- That route renders the org name as the page title with the projects grid
  under it, so the answer to "which org owns these?" is on screen and in the URL.
- No tab bar unless the tabs lead somewhere real. A single dead "Projects" tab
  is worse than none.

Available primitives: `PageHeader`, `IdChip`, `FullPageSpinner`, `EmptyState`,
`useToast`, `ConfirmButton` (`console/src/components/`). Terminology goes in
`console/src/strings.ts` — `STR.organization` already exists (lowercase
`"organization"`); add what you need there rather than hardcoding nouns.

## Tests

Integration tests (`tests/Praxy.Tests.Integration/`, Testcontainers,
`postgres:17-alpine`, shared collection fixture — `ProjectApiTests.cs` is the
closest neighbour). Cover:

- The list returns exactly the operator's org, with the claim-time name.
- A second operator cannot read the first's org → 404
  `organization_not_found`. (`ClaimFlowTests.cs` shows how to get a second
  account; note the instance can only be claimed once.)
- Every project returned by `/v1/console/projects` carries an `organizationId`
  that matches the org endpoint's id **byte for byte in the same format** —
  this is the regression test for the decision above.
- The reserved `console` project still never appears in either surface.

## Done means

- `dotnet test` green (needs Docker).
- `npm run build --prefix console` green.
- OpenAPI snapshot regenerated with the dev API running the new code:
  `curl -sS http://localhost:5090/openapi/v1.json -o docs/openapi/v1.json`
- `docs/architecture.md:11` and the stale "hidden in the UI" comments updated.
- `git status` clean, conventional commits, on a new branch off `main`.
- You click-tested it yourself: log in, land on the org-scoped URL without a
  flash of the wrong screen, see the org name above the projects, and confirm a
  fresh/empty instance still gets the bare create-project card.
- No `docs/handoff/` report needed (feature, not a numbered phase), but do state
  the id-format decision and its reasoning in your final summary. Update
  `CLAUDE.md`'s Commands section only if something there actually changed.
