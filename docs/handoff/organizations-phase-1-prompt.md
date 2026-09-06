# Session task — Organizations, Phase 1: the organization itself

> **Status: shipped.** 2026-09-06 — see `docs/handoff/organizations-phase-1-report.md`. One
> addition made during review before merge: organizations were the only creatable resource in
> Praxy without a quota, and the one every other quota is scoped *to* — see the report's
> "Found in review before merge" section and `Praxy:Quotas:MaxOrganizationsPerOperator`.

## Why this exists

An operator today gets exactly one organization, created for them at signup and named "Personal",
with no way to make another, rename it, or delete it. This phase fixes that and makes the console
handle more than one.

Read `docs/research/organizations.md` first (the design, and the Organizations-vs-Teams comparison
table you need before touching anything), then `docs/research/multitenancy.md`'s "What already
exists" section for why this is the piece worth building before any infrastructure decision, then
`CLAUDE.md`. Work on a new branch off `main`.

## The one thing to understand before writing code

**`Organization`/`OrganizationMember` have existed since Phase 0 and are load-bearing.** Org-level
quotas are enforced through them (`QuotaService.GetOrgLimitsAsync`), and `ConsoleOrganizationEndpoints`,
`ProjectEndpoints` and `RealtimeEndpoints` all join through `OrganizationMembers` for authorization
right now. You are adding a lifecycle to a live model, not building a new one — every change has to
keep those three paths working.

**Organizations are not Teams.** Praxy has two membership systems with `owner`/`member` roles at
different layers: organizations hold console *operators* and own projects; teams hold *app users* and
live inside a project. They share a shape and nothing else. Do not reuse `TeamsService` code paths on
the strength of the resemblance — security-review Phase 3's Finding D was a membership information
leak in Teams, so this is a trap with precedent.

## Scope

1. **Create an organization.** `POST /v1/console/organizations`, name required. The creator becomes
   its `owner` member, same as signup already does.
2. **Rename an organization.** `PATCH /v1/console/organizations/{id}`.
3. **Delete an organization** — **only when it holds no projects**. An org with projects returns
   `409`; the operator deletes those first. **No `force` escape hatch**, deliberately: cascading
   through projects into databases, buckets, functions and sites is exactly the implicit destruction
   the engine refuses everywhere else, and unlike a schema change there is no case where "delete this
   org and everything in it" is the obvious intent. New error type for the blocked case.
4. **An operator always keeps at least one organization.** Deleting your last one is refused — it
   produces an account that can do nothing and has no way back.
5. **Console: multi-org switching.** `HomeRedirect`'s own comment states the assumption you are
   invalidating — *"list orgs, take the first — there is exactly one."* Replace it with a real choice:
   remember the last-used org, fall back to a picker when there's no remembered one and more than one
   exists. The org page itself already renders a project list and mostly stands.
6. **Console: create/rename/delete UI**, wherever it sits naturally alongside the existing
   organization page.

## Explicitly *not* this phase

**No membership changes at all.** No invites, no adding or removing members, no role enforcement.
Every org still has exactly one member — its creator. That is Phase 2, and it is separated precisely
because it is the phase where authorization actually changes.

In particular: **do not start enforcing `OrganizationMember.Role`.** It is written once at signup and
never read today, so every member is effectively an owner. Turning that on is a behaviour change that
belongs with the phase that adds a second member to have a different role *from*. Leave it dead.

Also out of scope for the whole sequence: per-project operator roles, org billing or plans, and moving
a project between organizations.

## Landmines

- **Every existing org-scoped authorization path assumes membership means access.** With one member
  per org that stays true this phase — but check each of the three join sites still behaves when an
  operator belongs to several orgs, which is newly possible.
- **The `Limits` jsonb** carries org-level quotas. A newly created org gets `{}`, meaning instance
  defaults — confirm that's what `QuotaService` actually does with an empty value rather than
  assuming, since a new org silently getting *no* quota would be a real problem.
- **Signup still creates "Personal".** Don't remove that — an operator with zero orgs is the state
  scope item 4 exists to prevent.
- **Project creation silently picks an org, and says so.** `ProjectEndpoints`:

  ```csharp
  // Single-org world for now: every project lands in the operator's silently created org.
  var orgId = await db.OrganizationMembers
      .Where(m => m.UserId == op.Account.Id)
      .OrderBy(m => m.CreatedAt)
      .Select(m => (Guid?)m.OrganizationId)
      .FirstOrDefaultAsync(ct)
  ```

  That is the *second* explicit single-org assumption in the codebase (the other is `HomeRedirect`'s),
  and it silently takes the **oldest** membership. Once an operator can belong to several orgs, "which
  org does this project go in" becomes a real input, not a lookup — and a `POST /projects` that keeps
  guessing will quietly put projects in the wrong place rather than fail. Both assumptions are
  commented in the source; grep for others before assuming these are the only two.

## Tests

Assert the property, not the status code — the standard the security review established across three
phases:

- An org holding a project cannot be deleted (assert the project still exists afterwards, not just
  the 409).
- An operator's last organization cannot be deleted.
- An operator who belongs to two orgs sees exactly their own two, and cannot read or rename a third
  they don't belong to — by id, directly, not through the UI.
- Creating an org gives its creator `owner` and a working project list.

## Done means

- `dotnet test` green; console build clean.
- **Owner test, actually run**: create a second organization in the console, switch between them,
  create a project in each, rename one, fail to delete a non-empty one, delete an emptied one, and
  confirm the last remaining org cannot be deleted.
- `git status` clean, conventional commits, branch off `main`.
- Write `docs/handoff/organizations-phase-1-report.md` and
  `docs/handoff/organizations-phase-2-prompt.md`, update `CLAUDE.md`'s Commands section if anything
  configurable changed, and print the next prompt for the owner.
- **In the Phase 2 prompt, carry forward whatever this phase learned the hard way**, the way the
  security-review prompts did — that accumulation is why those phases got progressively better, and
  it is worth more than a summary of what shipped.
