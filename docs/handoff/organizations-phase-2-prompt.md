# Session task — Organizations, Phase 2: members and roles

## Why this exists

Phase 1 (`docs/handoff/organizations-phase-1-report.md`) made "exactly one organization" false: an
operator can create, rename, switch between and delete organizations, but every org still has
exactly one member — its creator. This phase is where a second person actually joins one, and where
`OrganizationMember.Role` — written once at signup or create, never read by anything — comes alive
for the first time.

Read `docs/research/organizations.md` (the "Design decisions" and "Phase 2" sections specifically),
`docs/handoff/organizations-phase-1-report.md` in full, then `CLAUDE.md`. Work on a new branch off
`main`.

## The one thing to understand before writing code

**Turning on `Role` is a behaviour change, not a new feature**, and the design doc says so plainly:
today every member of an org can do everything, because nothing reads the column. The migration is
safe only because every org that exists right now — every one Phase 1 could have created — has
exactly one member, and that member is always `owner` (both `ConsoleAuthService.ClaimAsync` and
`ConsoleOrganizationEndpoints.Create` write `Role = "owner"` for the creator, unconditionally). So
turning the check on cannot silently strip an ability from anyone on a live instance *today* — but
reason about that explicitly in the report rather than repeating this paragraph as an assumption.
Once this phase adds a second member, that stops being true by construction, which is the point.

## Scope

1. **Invite a member by email.** Mirror Teams' proven shape as a *pattern*, not shared code —
   `SecretHash`/`InvitedAt`/`Confirmed` via `Secrets.Generate()`/`Secrets.HashEquals`, an email with
   an accept link, an accept endpoint that verifies the secret. Decide the storage shape yourself
   (a pending row on `OrganizationMember` with `Confirmed = false`, or a separate invite table) and
   say which you picked and why in the report — the design doc deliberately leaves this open.
2. **Accept an invite.** No session required to view/accept — same posture as a Teams membership
   accept — but see the landmine below on what the accept endpoint must and must not reveal.
3. **Remove a member.** `owner` only, once role enforcement lands. Removing yourself is allowed
   (leaving an org) except where the last-owner rule below blocks it.
4. **Change a member's role.** `owner`/`member` only — no custom roles, this is a fixed decision, not
   an oversight to revisit.
5. **Enforce `owner` vs `member` for the first time.** `owner` manages the org itself (rename,
   delete, invite, remove, change roles); `member` uses the projects inside it (Phase 1's
   `ConsoleOrganizationEndpoints.Update`/`Delete` currently allow *any* member — that's exactly where
   the new check goes). Project-level operations (`ProjectEndpoints`) stay open to every member;
   per-project operator roles are explicitly out of scope for the whole Organizations sequence.
6. **The last owner cannot be removed or demoted.** Same shape as Phase 1's "last organization"
   guard — enforced server-side, checked by counting `owner`-role rows on the target org, not by
   hiding a button.
7. **Console**: an org members screen (list, invite form, role change, remove), and the org page
   picks up whatever new affordances a `member` (vs `owner`) viewer needs — most likely hiding
   rename/delete/invite controls for a `member`, though the client-side hide is cosmetic; the two
   409s from item 6 and the 403 from item 5 are the actual gate either way, matching Phase 1's own
   danger-zone convention of explaining rather than just disabling.

## Explicitly *not* this phase

Per-project operator roles, organization billing or plans, and moving a project between
organizations stay out of the whole Organizations sequence (`docs/research/organizations.md`).
Operator OAuth is Phase 3 — an invited colleague accepts with email+password, same as today's login.

## Landmines

- **`MembershipNotFound`/`MembershipAlreadyExists`/`MembershipAlreadyConfirmed` already exist** —
  for Teams (`Praxy.Persistence.Entities.Membership`, app-user team membership, a completely
  different resource). **Do not reuse them for organization membership.** Reusing an error `type`
  string across two unrelated resources means an SDK's `error.type === "membership_not_found"`
  switch can no longer tell which kind of membership failed to resolve — mint distinct names (e.g.
  `OrganizationMembershipNotFound`/`OrganizationInviteInvalid`, or whatever shape you land on) even
  though the two features rhyme. This is the design doc's own "trap with precedent" warning
  (security-review Phase 3's Finding D was a Teams membership leak) showing up concretely in the
  error registry, not just in code you might have copied.
- **Finding D itself, restated so it doesn't have to be re-derived**: the accept endpoint must not
  let a caller distinguish "no such invite" from "wrong secret" — both get the same error. Look up
  the invite scoped by organization id *in the query* (`WHERE organization_id = @id AND ...`), never
  by invite id alone with an org check applied afterward — that ordering is Phase 3's own
  cross-project id-confusion theme, applied here to invites.
- **Phase 1's `ConsoleOrganizationEndpoints.Update`/`Delete` currently trust membership alone.**
  Both need an owner check added. Decide where that check lives — a new endpoint filter alongside
  `RequireOperatorFilter`, or inline — but put it in exactly one place so it can be got wrong in only
  one place, the same principle `BucketAccess.cs` was built around.
- **`AccessibleOrganizations`/`AccessibleProjects`/`RealtimeEndpoints`'s inline join all check
  membership existence, not role**, and Phase 1 confirmed all three are correct for *that* — reading
  a project, seeing an org in a list, or an operator's realtime bypass stay membership-only by
  design (every member "uses the projects inside it" per the design doc). Don't add a role check to
  any of the three; the new check is additive, on the owner-only actions listed in Scope item 5, not
  a tightening of what member-level access already means.
- **Phase 1's `ProjectEndpoints.Create` ambiguity fix stays correct as written** — it resolves an
  omitted `organizationId` only when the operator has exactly one membership, and fails loudly
  otherwise. A second *member* of an org (not just a second org) doesn't change this: the ambiguity
  is about which org, not who else is in it. No change expected here, but re-read
  `ResolveOrganizationIdAsync` before assuming.
- **The console's `OrganizationPage` fresh-instance fast path** (`organizations.total === 1 &&
  projects.total === 0` → bare `CreateProjectCard`, no chrome) was a real bug Phase 1 found and fixed
  late — an org that's empty but not literally the operator's first is a deliberate state, not a
  first run, and needs its full chrome. Membership additions don't touch this condition, but it's
  the kind of "screen assumes something that stopped being true" bug this whole initiative keeps
  surfacing — read the check before adding a new early return near it.
- **The organization quota counts memberships, and this phase is what makes that wrong.**
  `QuotaService.EnsureOrganizationQuotaAsync` (added in review of Phase 1 — organizations were the
  only creatable resource in Praxy without a cap, and the one every other quota is scoped *to*) caps
  an operator by counting their `OrganizationMembers` rows. In Phase 1 that is exactly "organizations
  this operator owns", because every member is its creator. **The moment you can be invited into
  someone else's organization, that count is wrong** — accepting an invite would silently consume
  your own creation allowance, so an operator invited to five orgs could no longer make their own.
  Make the count owner-scoped as part of enabling role enforcement, and test it: accept an invite,
  then confirm you can still create an organization.

- **`OrganizationMember`'s FK onto `Organization` is `ON DELETE CASCADE`** (`PraxyDb.cs`) — Phase 1
  leaned on this for org deletion needing no explicit membership cleanup. This phase deletes
  individual membership rows directly instead (removing one member, not the org), so that cascade
  isn't the relevant one here — don't reach for it by habit.

## Tests

Assert the property, not the status code:

- A `member` cannot rename or delete the organization, invite, remove, or change a role — 403 (pick
  and register the specific error type), and nothing about the org or its membership changed.
- An `owner` can do all of the above.
- The last `owner` cannot be removed or demoted — including the case where they're demoting/removing
  *themselves* as the sole owner.
- An invite's accept endpoint returns the same error for a wrong secret and a nonexistent invite
  (timing included, if practical — `ConsoleAuthService.LoginAsync`'s dummy-hash-burn pattern is the
  precedent).
- Accepting an invite makes the invitee a real member with the invited role, confirmed exactly once
  (accepting twice is rejected, not silently re-confirmed).

## Done means

- `dotnet test` green; console build clean; `docs/openapi/v1.json` regenerated if any route shape
  changed (`OpenApiDocumentTests`' committed-snapshot test catches a forgotten regen).
- **Owner test, actually run**: invite a second person into an organization, accept the invite as
  them, confirm they can use projects but not rename/delete/invite/manage members, promote them to
  owner, confirm they now can, then remove the original owner and confirm the org still works under
  the new owner alone.
- `git status` clean, conventional commits, branch off `main`.
- Write `docs/handoff/organizations-phase-2-report.md` and
  `docs/handoff/organizations-phase-3-prompt.md`, update `CLAUDE.md`'s Commands section if anything
  configurable changed (a new `IEmailSender` call for invites likely doesn't add a *setting*, but
  check), and print the next prompt for the owner.
- **In the Phase 3 prompt, carry forward whatever this phase learned the hard way**, the same way
  this prompt carries Phase 1's lessons forward — that accumulation is worth more than a summary of
  what shipped.
