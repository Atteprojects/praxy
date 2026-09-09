# Organization lifecycle — design

## Context

`docs/research/multitenancy.md` identified the org lifecycle as the one piece of managed-hosting work
required under **either** infrastructure fork, buildable now, committing to neither. This doc scopes
it.

It is also worth having on its own terms: an operator today gets exactly one organization, created
for them at signup and named "Personal", with no way to make another, rename it, or let a colleague
in. That's a real limitation for anyone using self-hosted Praxy with more than one person, quite apart
from anything managed.

Grounded in direct reading of the current code — every "today" below was checked, not remembered.

## What exists today

- **The model has been there since Phase 0.** `Organization` (id, name, jsonb `Limits`) and
  `OrganizationMember` (organization id, user id, `Role`, created-at). Projects carry
  `OrganizationId`.
- **It is load-bearing.** Org-level quotas are enforced (`QuotaService.GetOrgLimitsAsync`), and
  authorization joins through `OrganizationMembers` in `ConsoleOrganizationEndpoints`,
  `ProjectEndpoints` and `RealtimeEndpoints`. This is not a vestigial table.
- **Exactly one org is created per operator**, at console signup:
  `new Organization { Name = "Personal" }` in `ConsoleAuthService`, with the creator added as
  `Role = "owner"`.
- **Only two endpoints exist**: `GET /organizations` and `GET /organizations/{id}`. No create, rename,
  delete, or membership management. The whole file is 72 lines.
- **`OrganizationMember.Role` is written once and never read.** A repo-wide search for owner-role
  checks finds them only in `TeamsService` — a different feature (below). **Every member is
  effectively an owner today**, because nothing distinguishes them.
- **The console assumes exactly one.** `HomeRedirect`'s own comment: *"list orgs, take the first —
  there is exactly one."* `/organization/$organizationId` renders that org's project list.

## The distinction that must not blur: Organizations vs Teams

Praxy has **two** membership systems with `owner`/`member` roles, at different layers, and a future
session will conflate them if this isn't stated plainly:

| | **Organizations** | **Teams** |
|---|---|---|
| Who belongs | console **operators** (`ConsoleAccount`) | **app users** (`User`) — the developer's own end users |
| Scope | owns projects | lives inside one project |
| Auth | `praxy_session_console` cookie / `X-Praxy-Session` | app-user sessions/JWTs |
| Purpose | who can administer Praxy | the developer's app's own permission groups |

They share a shape and nothing else. Security-review Phase 3's Finding D was a membership
information leak in *Teams*; the org work below must not "reuse" team code paths on the strength of
the resemblance. What it *should* reuse is the **invite mechanism's proven shape** —
`SecretHash`/`InvitedAt`/`Confirmed` with `Secrets.Generate()`/`Secrets.HashEquals` and an accept
endpoint that verifies the secret — which is a pattern, not shared code.

## Design decisions

**Role enforcement is a behaviour change, and must be treated as one.** Today every member of an org
can do everything, because `Role` is never read. Turning it on means some existing member could lose
an ability they currently have. There is exactly one org per operator today and its single member is
`owner`, so no live instance can actually be affected — but the migration must be reasoned about
explicitly rather than assumed benign, and the phase that enables it says so in its report.

**Two roles only: `owner` and `member`.** The column already says so. `owner` manages the org itself
(rename, delete, invite, change roles, remove members); `member` uses the projects inside it. No
custom roles, no per-project operator roles — that is a much larger permission model and Praxy
deliberately has one role resolver already.

**The last owner cannot be removed or demoted.** The classic lockout, and cheap to prevent. Enforced
server-side, not by hiding the button.

**Deleting an organization requires it to be empty.** Cascading a delete through projects — and
therefore databases, buckets, functions and sites — is exactly the kind of implicit destruction the
engine refuses everywhere else (`ColumnsService.DeleteAsync`'s `force` gate, `TablesService`'s
`relationship_dependency`). An org with projects returns `409`; the operator deletes the projects
first. No `force` escape hatch: unlike a schema change, there is no case where deleting an
organization *and* everything in it is the obvious intent.

**An operator always has at least one organization.** Signup keeps creating "Personal". Deleting your
last org is refused for the same reason as removing the last owner — it produces an account that can
do nothing and has no path back.

**Invites carry a secret, mirroring Teams' proven shape.** A row created with `SecretHash` and
`InvitedAt` but not `Confirmed`, and an accept endpoint that verifies the secret. Two properties the
security review's own findings argue for: the accept path must not distinguish "no such invite" from
"wrong secret" (Phase 3 Finding D was exactly that leak, in team memberships), and an invite must be
scoped by org id in the query rather than looked up by id and checked afterwards (Phase 3's
cross-project id-confusion theme).

**Console switching replaces an assumption, not a screen.** `HomeRedirect`'s "take the first — there
is exactly one" is the single load-bearing line; once more than one org can exist it needs a real
choice (remembered last org, else a picker). The org page itself already renders a project list and
mostly stands.

## Phasing

Three phases, each independently useful and shippable:

- **Phase 1 — the org itself.** Create, rename, delete (empty-only), and multi-org switching in the
  console. Makes "exactly one org" false, which is the assumption everything else is built on, so it
  goes first. No membership changes at all — every org still has exactly one member, its creator.
  Immediately useful standalone: an operator can separate client work from personal projects.
- **Phase 2 — members and roles.** Invite by email, accept, remove, change role, and **enforce
  `owner` vs `member`** for the first time. This is where the dead column comes alive and where
  authorization actually changes; it is also the phase that most needs the security review's habits,
  since it adds a whole new authorization surface.
- **Phase 3 — operator OAuth.** Shipped 2026-09-06, **removed 2026-09-08**
  (`docs/handoff/console-oauth-removal-report.md`). This entry called it "convenience and
  managed-signup polish rather than a blocker" before it was built, and that read turned out to be
  the whole story: console SSO is nearly free to offer in a *managed* service (one central OAuth
  client) and genuinely annoying per *self-hosted* instance (every installation registers its own,
  because the redirect URI is per-domain). Self-hosters want email+password to get started. The
  phasing judgment was right; what it should have concluded is "not yet, and not here" rather than
  "last". Worth revisiting when managed hosting exists — and worth designing *for* that context
  then, not carrying a self-host-shaped implementation forward.

**Explicitly out of scope for the whole sequence**: per-project operator roles (a much larger
permission model), organization-level billing or plans (a business decision with no code shape yet),
and transferring a project between organizations (real, wanted eventually, and its own design problem
— every id in a project is scoped by `project_id`, not org, so the move is mostly a metadata update,
but "mostly" is doing work there and it deserves its own pass).

## Verification

This session's own deliverables are the three planning artifacts; the implementing sessions own their
own verification, to the standard the security review established: assert the property (a `member`
cannot rename the org; the last owner cannot be removed; an org with projects cannot be deleted), not
that an endpoint returns 200. Phase 2 in particular should be reviewed against Phase 3's Finding D
before it ships, not after.
