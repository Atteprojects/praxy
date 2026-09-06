# Organizations, Phase 1 (the organization itself) — report

**Status: complete.** Every item in `docs/handoff/organizations-phase-1-prompt.md`'s scope shipped,
and every non-goal stayed out — no membership changes, `OrganizationMember.Role` still unread.

## The shape of it

`ConsoleOrganizationEndpoints` grew from a 72-line read-only surface (list/get) into the full
lifecycle, on the same membership join it already had. Nothing about *how* an operator reaches an
organization changed — `AccessibleOrganizations` is the same join as before — what changed is that
there can now be more than one, and three places in the codebase that quietly assumed otherwise had
to be found and fixed.

## What shipped

**API** ([`ConsoleOrganizationEndpoints.cs`](../../src/Praxy.Api/Endpoints/ConsoleOrganizationEndpoints.cs)):

- `POST /v1/console/organizations` — name required (1-128 chars, same validation shape as a
  project's name), creator becomes `owner`, audited as `organizations.create`.
- `PATCH /v1/console/organizations/{id}` — name only, same unguessable-404 scoping `Get` already had.
- `DELETE /v1/console/organizations/{id}` — **no `force`, deliberately.** Two independent 409s, both
  new error types:
  - `organization_has_projects` — the org still owns a project. The operator deletes those first;
    there is no cascade path, matching how the engine refuses every other implicit-destruction
    shortcut (`ColumnsService`'s `force` gate, `TablesService`'s `relationship_dependency`).
  - `organization_last_one` — this is the operator's only organization. Checked by counting
    `OrganizationMembers` rows for the caller, not by inspecting the target org — deleting your
    *last* org is refused even though the org itself might be a fine candidate for deletion in
    isolation.

  Deleting an eligible org needs no explicit membership cleanup — `OrganizationMember`'s FK onto
  `Organization` is already `ON DELETE CASCADE` (`PraxyDb.cs`), so `db.Organizations.Remove(...)` is
  the whole operation.

**The landmine, fixed** ([`ProjectEndpoints.cs`](../../src/Praxy.Api/Endpoints/ProjectEndpoints.cs)):
`Create` used to silently pick the operator's *oldest* membership — harmless when one org is the
only org that could ever exist, wrong the moment a second one does. `CreateProjectRequest` gained an
optional `organizationId`. `ResolveOrganizationIdAsync` now:

- validates an explicit id against the operator's own memberships (same not-a-member-gets-404 rule
  `ConsoleOrganizationEndpoints` uses — an org id you don't belong to is indistinguishable from one
  that doesn't exist);
- when omitted, resolves it **only while unambiguous** — exactly one membership, the single-org case
  every existing deployment and test is in today, so nothing there changes behavior;
- when omitted **and** ambiguous (two or more memberships), fails with `400
  general_argument_invalid` on `fields.organizationId` rather than guessing. This is the one behavior
  change in the whole phase that a single-org self-hoster could theoretically notice, and only once
  they've created a second organization.

**Console** ([`OrganizationPage.tsx`](../../console/src/screens/OrganizationPage.tsx)):

- **`HomeRedirect`** no longer takes `organizations[0]`. It checks `localStorage`
  (`praxy.lastOrganizationId`) for a remembered org that's still one of the operator's own, falls
  back to the operator's only org when there's exactly one, and otherwise renders an
  `OrganizationPicker` — a plain list, click one to enter it.
- **Switching**: an org-page `<select>` (shown only once a second org exists — no point cluttering
  the single-org screen every deployment starts in) navigates between organizations. Remembering the
  current org lives in one place — a `useEffect` in `OrganizationPage` itself — so the picker, the
  switcher, and a bare deep link all update the same `localStorage` key by construction rather than
  three call sites needing to agree.
- **Rename**: `InlineEditableTitle`, the same click-to-edit heading `ProjectOverviewPage` already
  uses for a project's name. No new component.
- **Create**: a `Modal` form (`CreateOrganizationModal`), the same shape as `DatabasesPage`'s
  `CreateDatabaseModal` — `Field`, `ErrorNote`, `Spinner`.
- **Delete**: a danger-zone section, typed-name confirmation like `ProjectOverviewPage`'s — but
  unlike a project delete, the two blocked states (non-empty, only-org) never render a confirm form
  at all. The server is the actual gate either way; the UI just doesn't invite a click that's always
  going to 409.
- **Project creation** now always sends the owning `organizationId` explicitly (`CreateProjectCard`
  takes it as a required prop) — the console never relies on the server's single-org inference, since
  every place it creates a project already knows which organization's page it's on.

## A landmine the prompt didn't name, found while building the UI

`OrganizationPage`'s empty-instance fast path — bare `CreateProjectCard`, no chrome — used to fire
whenever *this org* held zero projects. That's right for a genuinely fresh instance (one org, one
operator, nothing created yet) and wrong for anything else: an operator who empties a *second* org's
last project (exactly the state Scope item 3 asks them to reach, on the way to deleting that org)
would have landed on a chrome-less "create your first project" screen with no switcher, no rename, no
delete — stranded, unable to do the very thing they were about to do. Fixed by gating the fast path on
`organizations.total === 1 && projects.total === 0` (both, not just this org's own count) — a
genuinely fresh instance, not merely an org that happens to be empty right now.

## Landmines from the prompt, checked and confirmed rather than assumed

- **The three membership joins.** `ConsoleOrganizationEndpoints.AccessibleOrganizations`,
  `ProjectEndpoints.AccessibleProjects`, and the inline join in `RealtimeEndpoints`'s operator-bypass
  branch all already join through `OrganizationMembers` rather than taking a first/single result —
  read, traced, and left untouched. `ConsoleProjectFilter` is the same shape and same conclusion.
  None of the four needed a change for multi-org membership; `ProjectEndpoints.Create`'s
  single-membership *lookup* (not a join — see above) was the only one that did.
- **The `Limits` jsonb on a new org.** `OrganizationLimits.Parse("{}")` returns
  `OrganizationLimits.Unlimited` — every field `null` — and `QuotaService` treats a `null` dimension
  as "fall through to `QuotaOptions`'s instance default," not zero. Confirmed by reading
  `OrganizationLimits.cs`'s own doc comment plus every `?? defaults.X` call site in
  `QuotaService.cs`, not assumed: a freshly created second organization gets the same quotas as the
  first, not none.
- **Signup still creates "Personal."** `ConsoleAuthService.ClaimAsync` is untouched.

## Found in review before merge: organizations shipped without a quota

Every creatable resource in Praxy has a cap — `QuotaOptions` bounds projects, databases, tables,
columns, indexes, sites, preview containers, buckets, file size and total storage bytes. After this
phase, **organizations were the only one that didn't**, and `POST /v1/console/organizations` sat
behind `RequireOperatorFilter` alone: no quota, no rate limit.

That matters more than "an unbounded row count" because of *which* resource it is. `MaxProjects`
(default 100) is scoped **per organization** — `QuotaService.EnsureProjectQuotaAsync` resolves the
org, then counts that org's projects. So an operator at their project ceiling could create another
organization and receive a fresh allowance, and repeat. **Every per-org quota was advisory**, since
the boundary they are scoped to was free to duplicate.

Under today's single-operator model that is harmless — the operator owns the instance and the quotas
exist to protect them from themselves. It stops being harmless the moment an organization is the
boundary a paying customer is metered by, which is precisely what `docs/research/multitenancy.md` has
it becoming, and why this initiative exists at all. It is also the exact shape the security review
kept finding: correct while one trusted operator owns everything, wrong as soon as they don't.

It is also, more simply, against `CLAUDE.md`'s own cross-phase rule — *"every limit is configurable
and loud when tripped"* — for a newly creatable resource to have no limit at all.

**The fix.** `QuotaOptions.MaxOrganizationsPerOperator` (default 10 — generous enough that a
consultancy running an org per client never notices) and
`QuotaService.EnsureOrganizationQuotaAsync`, called from `Create`. It reuses `Exceeded`'s existing
shape, so it trips as `400 general_resource_limit_exceeded` like every other dimension — no new error
type, nothing reworded.

Unlike every other dimension it is scoped to the **operator**, not an organization, so there is no
per-org override to consult: an organization cannot meaningfully raise the limit on how many
organizations may exist alongside it. Instance default only.

**A note Phase 2 must not miss.** The check counts `OrganizationMembers` rows for the caller, which in
Phase 1 is exactly "organizations this operator owns" because every member is its creator. Once Phase
2 lets an operator be *invited* into someone else's organization, being a member of it should not
consume their own creation allowance — the count needs to become owner-scoped at that point. Recorded
in the method's own remarks as well as here.

**Test.** `OrganizationApiTests.An_operator_cannot_create_unlimited_organizations` — reaches the cap
(set to 3 for the class, so the production default proves the same property after nine more rows),
asserts the refusal, and then asserts the operator still has exactly the cap, so a refused create
writes nothing rather than merely returning an error.

## Tests

`tests/Praxy.Tests.Integration/OrganizationApiTests.cs` — seven new `[Fact]`s, asserting the
property per the prompt's own standard, not the status code:

- `Creating_an_organization_makes_its_creator_the_owner_with_a_working_project_list` — reads
  `organization_members.role` directly (no membership-list endpoint exists until Phase 2), then
  creates and lists a project through it.
- `Renaming_an_organization_updates_the_name_and_leaves_the_id_untouched`
- `An_organization_holding_a_project_cannot_be_deleted` — asserts the project *and* the org both
  still exist after the blocked attempt, not just the 409.
- `An_operators_last_organization_cannot_be_deleted` — including the positive case: creating a
  second org and deleting the now-empty first one succeeds, and the guard reapplies to whichever org
  is left.
- `An_operator_in_two_organizations_sees_exactly_those_two_and_cannot_touch_a_third` — by id,
  directly: read, rename and delete all 404 against a third operator's own org.
- `Creating_a_project_with_no_organization_id_fails_once_ambiguous_but_still_works_when_not` — the
  landmine fix, both halves.
- `Creating_a_project_in_an_organization_the_operator_does_not_belong_to_is_rejected` — and confirms
  no row was written.

`ErrorTypesTests` picks up the two new types automatically (registry/declared-constant equality).

Full-repo `dotnet test`: **626/626 unit, 346/346 integration** (real Postgres via Testcontainers,
real Docker daemon throughout). One transient failure along the way —
`StorageStreamingTests.Files_round_trip_exactly_and_memory_does_not_grow_with_their_size`, a
peak-heap memory heuristic unrelated to this phase (no Storage file was touched) — reproduced as a
pass in isolation immediately after; not a regression. `docs/openapi/v1.json` regenerated
(`POST`/`PATCH`/`DELETE /v1/console/organizations` added) — `OpenApiDocumentTests`' committed-snapshot
test would otherwise fail on this branch.

Console: `tsc -b && vite build` clean.

## Owner-test checklist

Done live against the local dev instance (`api`/`console` launch configs, `owner@test.local`) —
its two dev processes were stopped and restarted on this branch first (see `[[praxy-project]]`'s note
on checking for a stale binary before click-testing):

- Created a second organization ("Acme Inc.") from the existing "Personal" org's page.
- Switched between them via the header `<select>`, and separately by clearing `localStorage` and
  confirming `/` renders the picker with both orgs listed.
- Created a project in each ("Atte's project" pre-existing in Personal, "Acme App" newly created in
  Acme Inc.) — each project's quota card showed `1/100` against its *own* organization, not a shared
  count.
- Renamed "Acme Inc." to "Acme Corp" inline.
- Attempted deletion of "Acme Corp" while it held a project — danger zone explained why rather than
  offering a confirm form.
- Deleted "Acme Corp"'s project, then deleted the now-empty organization — succeeded, toast
  confirmed, navigated back to "Personal."
- Confirmed "Personal" — now the operator's only organization again — shows the last-org guard
  instead of a delete form.

## Commands

Nothing configurable changed — no new settings, no new environment variables. `CLAUDE.md`'s Commands
section is unchanged.

## Next

`docs/handoff/organizations-phase-2-prompt.md` — members and roles: invite by email, accept, remove,
change role, and enforcing `owner`/`member` for the first time.
