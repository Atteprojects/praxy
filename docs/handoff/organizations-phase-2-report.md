# Organizations, Phase 2 (members and roles) — report

**Status: complete.** Every item in `docs/handoff/organizations-phase-2-prompt.md`'s scope shipped:
invite by email, accept, remove, change role, and `OrganizationMember.Role` is finally read. Every
landmine the prompt named was checked and, where it applied, fixed. Two more were found the same way
Phase 1 found its own — by actually clicking through the console — and are written up below in full,
because both are the kind of thing this whole initiative keeps surfacing.

## The migration-safety reasoning the prompt asked for

The prompt's own instruction: don't just repeat "no live instance can be affected" as an assumption —
reason about it. Doing so: `Role` was written but never read anywhere before this phase (confirmed by
the same repo-wide search Phase 1's design doc describes). Every `OrganizationMember` row that exists
on any instance running Phase 1 was created by exactly two code paths —
`ConsoleAuthService.ClaimAsync` and `ConsoleOrganizationEndpoints.Create` — both of which
unconditionally write `Role = "owner"` for the creator, and neither path could ever produce a second
member. So the set of rows this migration's behavior change reaches is, provably, "one `owner` row
per organization" — turning the check on cannot revoke an ability from anyone, because there is no row
today holding any role *other than* `owner`. That stops being true the instant this phase's own invite
path creates the first `member` row, which is the point.

## Storage shape: a pending row on `OrganizationMember`, not a separate table

The prompt left this open. Chose the same entity Phase 1 already has, extended with `Confirmed`,
`SecretHash`, `InvitedAt` — exactly `Membership`'s three invite fields, mirrored as a *pattern*
(`src/Praxy.Persistence/Entities/Organizations.cs`). Reasons over a separate `OrganizationInvite`
table:

- The composite PK `(OrganizationId, UserId)` already gives "at most one row per person per org" for
  free, which is exactly right for an invite too — a second invite to someone already invited (or
  already a member) is `organization_membership_already_exists`, not a second pending row to reconcile
  against the first.
- Every access-control query already joins `OrganizationMembers` (`AccessibleOrganizations`,
  `AccessibleProjects`, `ConsoleProjectFilter`, `RealtimeEndpoints`) — adding `Confirmed` to the
  existing join is one extra predicate at each of six call sites (all fixed, listed below), not a new
  join added to any of them.
- The last-owner and quota-owner-scoping logic both need to distinguish real members from pending
  ones by role *and* org in one place; one table with one `Confirmed` flag is simpler to reason about
  than a real-membership table plus an invite table that must never disagree with it.

## Where the owner-role check lives

Exactly one place, per the prompt's own ask: `OrganizationsService.RequireOwnerAsync`
(`src/Praxy.Auth/OrganizationsService.cs`) — resolves the caller's confirmed role for the org and
throws `403 organization_owner_required` if it isn't `owner`. Called from `Update`, `Delete`,
`InviteMember`, `UpdateMemberRole`, and `RemoveMember` (only when the target isn't the caller — see
below) in `ConsoleOrganizationEndpoints.cs`. Nothing else re-derives ownership; a future owner-only
action calls this method or it hasn't actually adopted the check.

**Self-removal is unconditional on role.** `RemoveMember` lets `targetUserId == op.Account.Id` through
without calling `RequireOwnerAsync` at all — leaving is not an owner-only action, matching the
prompt's "removing yourself is allowed (leaving an org)". The last-owner guard (below) still applies
regardless of who initiated the removal.

## The last-owner guard

`OrganizationsService`'s `ConfirmedOwnerCountAsync` counts `Role == "owner" && Confirmed` rows for the
org; `RemoveMemberAsync`/`ChangeRoleAsync` both check it before mutating, using the same `409
organization_last_owner` either way. Two things worth being explicit about:

- **A pending owner-role invite never counts.** It might never be accepted, so it can never be the
  thing standing between the org and zero owners — `ChangeRoleAsync`'s guard is additionally gated on
  `member.Confirmed` for the *target* row, so demoting/removing an unconfirmed invite is never
  blocked by a count it was never part of.
- **The count includes the row being acted on.** Removing/demoting an owner when the count is exactly
  1 always means *that row* is the one being counted — no separate "count everyone else" query needed.

Tested including the sole owner demoting or removing *themselves* (the prompt's own edge case) —
`The_last_owner_cannot_be_removed_or_demoted_even_by_themselves`.

## Accept: the uniform-error and ordering choices, and why

`OrganizationsService.AcceptInviteAsync` looks up `WHERE organization_id = @id AND user_id = @id` in
one query — never an invite id alone with an org check applied after, per the prompt's restatement of
security-review Phase 3's Finding D. Two states get compared, in this order:

1. **`Confirmed` is checked before the secret.** Mirrors `TeamsService.AcceptInvitationAsync` exactly
   (it does the same thing, in the same order) — knowing an organizationId+userId pair is *already
   confirmed* isn't the enumeration leak Finding D was about, because both ids are unguessable
   128-/256-bit values already; the property Finding D actually protects is "wrong secret" vs
   "no such invite," which is the next check.
2. **Wrong secret and no invite at all return the identical `401 organization_invite_invalid`.**
   The hash comparison always runs — against a per-process `DummyHash` when there's no real row to
   compare against — so the "no row" path doesn't return measurably faster than the "wrong secret"
   path (`ConsoleAuthService.LoginAsync`'s own dummy-hash-burn pattern, reused as a pattern, not
   code). Tested for type/code equality
   (`Accepting_with_a_wrong_secret_and_a_nonexistent_invite_return_the_same_error`); an actual
   wall-clock timing assertion was judged not worth the CI flakiness it would add, given `dotnet
   test`'s own container-start jitter would swamp a millisecond-scale signal.

**Password is required only when the account the invite targets is still passwordless.** Inviting an
email with no existing console account creates one immediately (mirroring
`TeamsService.ResolveTargetUserAsync`'s "invite an unknown email, create the user" shape) so
`OrganizationMember.UserId` is never nullable — but that account has no password until accept sets
one, since operator auth is email+password only until Phase 3. Inviting an *existing* operator into a
second organization skips the password requirement entirely; they already have one. One
`SaveChangesAsync` covers the (possibly new) user row and the membership row together, so a failure
never leaves an orphaned passwordless account with no invite attached to it — an improvement over
`TeamsService`'s own two-save shape, made possible by not needing `AppAuthService.CreateUserAsync`'s
separate transaction boundary.

## `EnsureOrganizationQuotaAsync` is now owner-scoped

Exactly the fix Phase 1's report told this phase to make. Counts `Role == "owner" && Confirmed` rows
for the operator, not every membership — being invited into (or merely pending an invite into)
someone else's organization no longer touches this operator's own creation allowance. Test:
`Being_invited_into_someone_elses_organization_does_not_consume_the_operators_own_creation_allowance`
— a second operator, already owning one org, accepts a `member`-role invite into a first operator's
org (now belongs to two orgs, owns one), and still creates a fresh org of their own afterward.

## The six places `Confirmed` had to be threaded through

An unconfirmed row must not grant anything a real membership grants. Checked every
`OrganizationMembers` reference in the repo (there were exactly these) and added `&& m.Confirmed`
everywhere access, not audit-only counting, is decided:

- `ConsoleOrganizationEndpoints.AccessibleOrganizations` — an org a pending invite hasn't been
  accepted into no longer appears in the operator's own list/read surface.
- `ConsoleOrganizationEndpoints.Delete`'s `memberOf` count — a pending invite elsewhere is not "a
  fallback home," so it can't be used to justify deleting your one real organization.
- `ProjectEndpoints.ResolveOrganizationIdAsync`'s `memberships` — an invited-but-not-accepted operator
  can't create a project in the org they haven't actually joined yet.
- `ProjectEndpoints.AccessibleProjects` — same reasoning, for reading/acting on projects.
- `ConsoleProjectFilter`'s inline join — same, for `/v1/console/projects/{projectId}/...`.
- `RealtimeEndpoints`'s operator-bypass join — an unconfirmed invite doesn't grant the realtime
  firehose bypass either.

None of these six needed (or got) a *role* check — the prompt's landmine about
`AccessibleOrganizations`/`AccessibleProjects`/`RealtimeEndpoints` staying membership-only, not
owner-only, holds exactly as stated; `Confirmed` is an orthogonal axis from `Role`, and every one of
these six is still `member`-or-`owner`-agnostic.

## New error types

Five, all distinct from Teams' `Membership*`/`TeamInvalidSecret` (the landmine named explicitly, and
checked: a grep for the string `"membership_not_found"` etc. across `ErrorTypes.cs` shows these are
never the same constant as Teams' equivalents):

- `organization_owner_required` (403) — the one owner-only-action gate.
- `organization_membership_not_found` (404) — a role-change/remove target that doesn't exist.
- `organization_membership_already_exists` (409) — invite target already a member or already invited.
- `organization_invite_invalid` (401) — wrong secret or no such invite, uniformly.
- `organization_invite_already_accepted` (409) — accepting twice.
- `organization_last_owner` (409) — new; distinct from Phase 1's `organization_last_one`, which is
  about an *operator's* last organization (the delete-org guard), not an *org's* last owner (this
  guard) — different invariant, different resource, deliberately not reused.

## Console

- **`OrganizationMembersPage`** (`console/src/screens/OrganizationMembersPage.tsx`) — list (with an
  "invited" vs "member" badge), an invite form (owner only), an editable role `<select>` per row
  (owner only), and a Leave/Remove `ConfirmButton` (self, or owner-on-others). A member sees the same
  table read-only except their own Leave button — the 403 an owner-only mutation would return is the
  actual gate; hiding the controls is cosmetic, matching `OrganizationPage`'s own danger-zone
  convention.
- **`AcceptOrganizationInvitePage`** (`console/src/screens/AcceptOrganizationInvitePage.tsx`) — public
  route `/accept-invite`, reads `organizationId`/`userId`/`secret` straight off the query string (no
  route-param schema needed for a page nothing else links to), asks for a password only when the
  server actually requires one (surfaced the same way every other field error is: `error.fieldErrors
  ("password")`).
- **`OrganizationPage`** picks up the caller's own role (see below) to hide the inline-editable title
  and the danger zone for a `member`; a new "Members" link is visible to everyone, since reading the
  roster is membership-only, not owner-only.
- **Invite emails point at the console's own origin**, not a developer project's redirect allowlist —
  there is no "platform" concept for organization membership to validate against, and the caller
  invoking `InviteMember` already had to hold the org's owner role to reach it, the same trust
  boundary Teams' own session-invite `url` field relies on.

## Two bugs found by actually clicking through it (not in the prompt's landmine list)

**1. `OrganizationMemberResponse.UserId` used `Ids.Wire`, but `ConsoleAccount.Id` never has.**
`OrganizationMembersPage` computes `isSelf` by comparing `member.userId === account.data?.id` — and
until this was caught, the two were in different formats (`Ids.Wire`'s 32-char hex vs. `ConsoleAccount`'s
default dashed `Guid` serialization), so the comparison never matched. Found by inviting a real second
account through the running console and watching its own membership row never render a "Leave"
button. Fixed by making `OrganizationMemberResponse.UserId` match `ConsoleAccount.Id`'s existing
(unconverted) format instead of wire-encoding it — the smaller, more contained change, since
`ConsoleAccount` is an already-shipped response type used by `/console/account`, `/console/claim` and
`/console/sessions`, and changing *its* format would be the breaking direction.  `Ids.TryParseWire`
already accepts both dashed and undashed forms, so every server-side route parameter that consumes a
member's `userId` (`PATCH`/`DELETE .../members/{userId}`, the accept endpoint) needed no change.

**2. Logging in as a different operator, in the same tab, could briefly show the previous operator's
organization role.** `useOrganizations`/`useOrganization` carry a `staleTime`; neither `useLogin`
(via `useSessionMutation`) nor `useLogout` invalidated the `["organizations"]` query family, so a
login swap without a full page reload could render `OrganizationMembersPage`'s owner-only controls
using the *previous* account's cached role for one render before a background refetch corrected it —
in the reproduction, it looked like `isOwner` was simply wrong. This bug shape existed structurally
since Phase 1, but Phase 1 had no way for two different real operator accounts to ever exist via the
UI (no invite, no admin-created operator), so a same-tab identity swap between two *different* real
operators was unreachable before this phase made it a normal flow (an owner inviting a colleague who
might accept in the same browser). Fixed by invalidating `["organizations"]` alongside
`["capabilities"]`/`["projects"]` in `useSessionMutation`'s `onSuccess` (covers login, claim, *and*
accept-invite, which all share that helper) and in `useLogout`'s.

## A landmine this phase itself created: leaving your only organization is now possible

Neither the prompt nor the design doc names this, but it followed directly from the prompt's own
instruction ("removing yourself is allowed \[...] except where the last-owner rule below blocks it")
and its own owner-test script (promote a second person, then *remove the original owner* — which, on
a freshly-claimed instance, **is** that operator's only organization). Phase 1's design doc states "an
operator always has at least one organization" as a general invariant, enforced there only via the
delete-org path (`organization_last_one`) — because Phase 1 had no other way to lose your last one.
Phase 2's "leave" is a second way, and it does not carry the same guard: the prompt is explicit that
self-removal is blocked *only* by the last-owner rule, not a last-organization one, and blocking it
the way delete does would make the prompt's own owner-test script unrunnable on a single-org operator.

So this is deliberate, not an oversight — but it means `list.length === 0` becomes a real, reachable
state on the operator's own account for the first time, and `HomeRedirect` used to `throw new
Error("...instance claim did not complete")` for it, which is both the wrong message now (nothing
about the claim is broken) and a dead end (an `errorComponent` with only a "Reload" button, which
reloads into the exact same state forever). Reproduced live against the shared local dev instance
while running the owner-test checklist below, then fixed: `HomeRedirect` renders a small
`NoOrganizationsCard` (the same create-organization form as `CreateOrganizationModal`, without the
overlay chrome) instead of throwing, so the account has an immediate way out rather than a permanent
error screen. Recorded here in enough detail that Phase 3 doesn't have to re-derive it.

## Tests

`tests/Praxy.Tests.Integration/OrganizationMembershipApiTests.cs` — seven new `[Fact]`s extending
`AuthTestBase` (for its capturing email sender — invite links are read straight out of the sent
message, never a real SMTP send), asserting the property, not the status code:

- `An_owner_can_invite_a_new_colleague_who_accepts_and_uses_the_organization` — full round trip:
  invite, unconfirmed in the roster, accept with a fresh password, confirmed afterward, a real
  session that can create a project but gets `organization_owner_required` renaming the org.
- `A_member_cannot_manage_the_organization_and_nothing_changes` — rename, delete, invite, change-role,
  and remove-the-owner all return `403 organization_owner_required`; org name and membership roster
  are asserted unchanged afterward.
- `An_owner_can_rename_the_organization_promote_and_remove_a_member` — the positive mirror.
- `The_last_owner_cannot_be_removed_or_demoted_even_by_themselves` — including the self-targeting
  case, and the positive case once a second owner exists.
- `Accepting_with_a_wrong_secret_and_a_nonexistent_invite_return_the_same_error` — type and code
  equality between the two.
- `Accepting_an_invite_twice_is_rejected_not_silently_reconfirmed`.
- `Being_invited_into_someone_elses_organization_does_not_consume_the_operators_own_creation_allowance`
  — the quota fix's own test, described above.

`ErrorTypesTests` picks up the five new types automatically (registry/declared-constant equality),
same as Phase 1's two.

**Full-repo `dotnet test`: 626/626 unit, 354/354 integration** (real Postgres via Testcontainers, real
Docker daemon throughout, final run). One earlier full run in this session hit a single failure —
`StorageStreamingTests.Files_round_trip_exactly_and_memory_does_not_grow_with_their_size`, the exact
same pre-existing peak-heap memory heuristic Phase 1's report already flagged as flaky and unrelated
(no Storage file touched this phase either) — and passed on its own immediately after; the final
full-suite run above shows it green along with everything else. `docs/openapi/v1.json` regenerated (`GET`/`POST /v1/console/organizations/{id}/members`,
`PATCH`/`DELETE .../members/{userId}`, `POST .../members/{userId}/accept` added) —
`OpenApiDocumentTests`' committed-snapshot test would otherwise fail on this branch.

Console: `tsc -b && vite build` clean.

## Owner-test checklist

Done live against the shared local dev instance (`owner@test.local`, the canonical single-owner
setup every session reuses — see `[[praxy-local-dev-instance]]`), api/console restarted first to pick
up this branch:

- Invited `colleague@test.local` into "Personal" as `member` from the Members screen; email logged
  (SMTP unconfigured in dev) with the accept link pointing at the console's own origin.
- Opened the link in a fresh tab: set a password, landed signed in as the invitee, saw "Atte's
  project" (member-level project access confirmed), and the org's title rendered non-editable with no
  danger zone (member-level UI gating confirmed).
- Promoted the colleague to `owner` from the Members screen as the original owner; confirmed the role
  dropdown and badge updated.
- Removed (left as) the original owner; confirmed the toast, and confirmed the organization kept
  working under the new owner alone — project list, Members screen (now showing the new owner with
  full owner controls), everything intact.
- **This is exactly where the last-organization gap above was found**: the operator being removed
  (`owner@test.local`) had no other organization, so leaving genuinely dropped them to zero — which
  is real production behavior per this phase's own design, not a test artifact — and the
  `HomeRedirect` fix above was written and verified against this exact state before being reverted for
  cleanup.
- **Recovery, since the removed operator was the shared dev instance's real canonical account with
  real accumulated data** (a project holding actual databases/sites/buckets/11.6MB of files from many
  prior phases' click-testing, not throwaway): invited `owner@test.local` back into the same
  organization as `owner`, accepted server-side (no password needed — the account already had one),
  removed `colleague@test.local`, and deleted the duplicate empty organization/project the "zero
  organizations" reproduction had created for `owner@test.local` in the meantime. Verified via the API
  directly that the instance is byte-for-byte back to its one organization / one project / one owner
  shape afterward, then confirmed the same visually in the console. `colleague@test.local` remains as
  a harmless orphaned test account (zero organizations, a password nobody else knows) — noted here so
  a future session isn't puzzled by a `SELECT * FROM praxy.users` turning it up.

## Commands

Nothing configurable changed. The one new outbound-email call this phase adds (an invite) goes
through the existing instance-wide `IEmailSender` singleton (`Praxy:Smtp:*`, already documented) —
deliberately *not* through `IAuthEmailSender`'s per-project template system, since an organization
invite isn't scoped to any developer project and has no natural per-project customization screen to
live on. No new settings, no new environment variables. `CLAUDE.md`'s Commands section is unchanged
beyond recording that this phase shipped.

## Next

`docs/handoff/organizations-phase-3-prompt.md` — operator OAuth, the last phase in the sequence.
