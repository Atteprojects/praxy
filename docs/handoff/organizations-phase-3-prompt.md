# Session task — Organizations, Phase 3: operator OAuth

## Why this exists

Phases 1 and 2 (`docs/handoff/organizations-phase-1-report.md`,
`docs/handoff/organizations-phase-2-report.md`) built the whole organization lifecycle and
membership model on top of email+password: an operator claims the instance, or is invited into an
organization and accepts with a password. `CLAUDE.md`'s fixed decision defers operator OAuth to
"future multitenancy work" — this is that work, and the last phase in the Organizations sequence
(`docs/research/organizations.md`'s phasing). It is separable and last because nothing about it is a
blocker: an invited colleague can already accept with email+password today, so this phase is
convenience and managed-signup polish, not a missing capability.

Read `docs/research/organizations.md` in full (especially its own note that this phase "needs its own
design pass — operator OAuth is not app-user OAuth, and the existing Google provider code is written
for the latter"), `docs/handoff/organizations-phase-2-report.md` in full, then `CLAUDE.md`.
Work on a new branch off `main`.

## The one thing to understand before writing code

**The existing OAuth code is shaped for app users, and that shape doesn't fit operators.** Read
`src/Praxy.Auth/OAuth/OAuthService.cs` before assuming otherwise:

- `OAuthService.StartAsync`/`HandleCallbackAsync` take a `Project` and pull the provider's client
  id/secret from *that project's own* `ProjectAuthSettings` (`RequireProviderSettings` —
  `OAuthService.cs`'s own code, not a paraphrase). Operators have no project to hang credentials off
  of — the reserved `console` project exists as a row (`CatalogMigrator.cs`) but has no
  `auth-settings` screen and was never meant to gain one; per-*instance* Google credentials (env
  vars/`appsettings`, the same shape `SmtpOptions`/`QuotaOptions` already use) are the better fit, but
  that is a real design decision this phase has to make, not inherit.
- `StartAsync` validates `successUrl`/`failureUrl` against the project's registered `Platform`s
  (`auth.ValidateRedirectUrlAsync`). The console has no platform allowlist concept; whatever replaces
  it for operator OAuth needs its own redirect-safety story, not a borrowed one.
- `ResolveUserAsync` creates/links an app-user `User`+`Identity` pair scoped to `project.Id`. The
  operator equivalent is a `User` scoped to `Ids.ConsoleProjectId` (a `ConsoleAccount`, per
  `ConsoleAuthService`) — same `User` table, different project id, and `Identity` rows would need the
  same scoping if reused.
- The thing that *is* directly reusable: `IOAuthProvider`/`GoogleOAuthProvider`
  (`src/Praxy.Auth/OAuth/GoogleOAuthProvider.cs`) — `BuildAuthorizeUrl`/`ExchangeCodeAsync`/
  `FetchProfileAsync` don't know or care whether the resulting account is an app user or an operator.
  Reuse the provider; do not reuse `OAuthService` as-is.

Decide and write down: where operator-OAuth credentials live (instance config vs. some new per-console
setting — there is no console "project" screen for this today), what the operator-side redirect
allowlist looks like (or whether a fixed, self-hosted-console-origin assumption is good enough, given
there is exactly one console origin per self-hosted instance), and how `ConsoleAccount`
creation/linking maps onto `ResolveUserAsync`'s identity-first-then-email-linking shape.

## Scope

1. **Operator Google sign-in** — `CLAUDE.md`'s fixed decision: "app users get email+password and
   Google OAuth only" already exists for app users; this phase gives console operators the same
   choice, not a different provider. No new provider beyond Google.
2. **Claim and invite-accept both need an OAuth path.** Today `ConsoleAuthService.ClaimAsync` requires
   a password; the very first operator should be able to claim the instance via Google too.
   Organizations-phase-2's accept flow requires a password only when the invited account is still
   passwordless (`OrganizationsService.AcceptInviteAsync`) — that path needs an OAuth equivalent:
   accepting via Google should link the Google identity instead of asking for a password, without
   reopening Phase 2's own invite/accept design (the invite row, the secret, the uniform error shape
   all stay as they are).
3. **Console**: a "Sign in with Google" option on `LoginPage`'s `LoginForm`/`ClaimForm`, and on
   `AcceptOrganizationInvitePage` — the same login-vs-claim-vs-accept split the console already
   renders, each gaining an OAuth option alongside its existing password one, not replacing it
   (`CLAUDE.md`: "minimal options everywhere," not "OAuth only").
4. **Self-host configuration**: whatever knob shape you land on for the instance-level Google
   credentials, document it in `docs/self-host.md` and this file's own Commands section, the same way
   every other instance-level credential (SMTP, the GitHub App) already is.

## Explicitly *not* this phase

Per-project operator roles, organization billing or plans, and moving a project between
organizations stay out of the whole Organizations sequence (`docs/research/organizations.md`) — this
was true for Phases 1 and 2 and doesn't change now. A second OAuth provider for operators is also out
of scope; `CLAUDE.md`'s fixed decision names Google specifically, for both app users and (as of this
phase) operators.

## Landmines

- **`ProjectAuthSettings`/`RequireProviderSettings` are app-user-project-scoped by construction** —
  read the whole call chain (`OAuthService.StartAsync` → `RequireProviderSettings` →
  `ProjectAuthSettings.Parse(project.Settings)`) before assuming any part of it generalizes to
  operators without change. It doesn't; see "The one thing to understand" above.
- **`Identity.ProjectId` and the `(ProjectId, Provider, ProviderUid)` lookup in
  `OAuthService.ResolveUserAsync`** assume a per-project identity space (the same Google account can
  legitimately link to a different app user in each of a developer's projects). An operator's Google
  identity should almost certainly resolve within the single `console` project id's own identity
  space — reason about whether reusing the `Identities` table with `ProjectId = Ids.ConsoleProjectId`
  is correct, or whether operator identities need their own table, before picking one.
- **An invite created by organizations-phase-2 still creates a passwordless `User` row immediately**
  (`OrganizationsService.InviteAsync`, resolves-or-creates eagerly so `OrganizationMember.UserId` is
  never nullable). If this phase's OAuth-accept path links a Google identity to *that* row rather than
  creating a second one, the email-matching has to go through the same
  "unverified provider email never links to an existing account" rule `OAuthService.ResolveUserAsync`
  already enforces for app users (accepting an invite by email already proves nothing about who
  controls the Google account at that address) — read that method's own comment before reusing or
  reimplementing the logic.
- **Console response-format consistency, learned the hard way this phase**: `ConsoleAccount.Id`
  serializes as a plain (dashed) `Guid` — never `Ids.Wire`-encoded — while every other uuid-keyed
  response in the console API is. `organizations-phase-2-report.md`'s "Two bugs found by actually
  clicking through it" section #1 is the concrete story: a new response field that didn't match
  `ConsoleAccount.Id`'s format broke a client-side identity comparison silently, with no compiler
  error to catch it, because both are typed `string` on the wire. Any new operator-identity-bearing
  response this phase adds (an OAuth identity list, a linked-provider indicator on the account) should
  decide its id format deliberately and check it against whatever the console compares it to, not
  assume `Ids.Wire` is automatically right.
- **Same-tab identity-swap cache staleness, also learned the hard way this phase**: logging in as a
  different operator without a full page reload can render one render's worth of the *previous*
  operator's cached `["organizations"]` data before a background refetch corrects it
  (`organizations-phase-2-report.md`'s bug #2, fixed there by invalidating `["organizations"]` in
  `useSessionMutation`'s and `useLogout`'s `onSuccess`). An OAuth callback completing is a third way an
  operator's session can change client-side (alongside login and accept-invite, which both already
  route through `useSessionMutation`) — if the OAuth callback's own session-establishing call doesn't
  go through that same helper, it needs the same `["organizations"]` (and `["account"]`,
  `["capabilities"]`, `["projects"]`) invalidation, not just a `setQueryData(["account"], ...)`.
- **Leaving your only organization is allowed and reachable** (same report, "A landmine this phase
  itself created"). `HomeRedirect` now renders `NoOrganizationsCard` instead of throwing when an
  operator has zero organizations. Nothing about OAuth changes that state directly, but if this
  phase's claim-via-OAuth path creates a *first* operator account without also creating their
  "Personal" organization (unlike `ConsoleAuthService.ClaimAsync`, which does both atomically), a
  Google-claimed operator would land in that exact zero-organizations state immediately after
  claiming — make sure whatever claims-via-OAuth looks like still creates "Personal" in the same
  transaction, the same way the password path does.

## Tests

Assert the property, not the status code, same standard the last two phases used:

- Claiming the instance via Google creates the operator account, links the Google identity, and
  creates "Personal" with them as its confirmed owner — same as the password claim path, different
  entry point.
- A second claim attempt (via either method) is still refused once the instance is claimed — the
  "closed forever after" property doesn't get a second door.
- Signing in via Google resolves to the same operator account signing in with a password does, when
  both are the same verified email — and does *not* silently link when the provider email is
  unverified and already belongs to a different account (mirror `OAuthService.ResolveUserAsync`'s own
  existing test coverage for this on the app-user side, if any exists — check `tests/` for an
  `OAuthFlowTests.cs`-shaped precedent).
- Accepting an organization invite via Google links the identity to the invited (passwordless)
  account without ever asking for a password, and the resulting membership is confirmed exactly once
  — same "accepting twice is rejected" property Phase 2 already tests, exercised through the OAuth
  door instead of the password one.
- The console's login/claim/accept screens each render a working "Sign in with Google" option
  alongside the existing password form, and using it does not remove or disable the password option.

## Done means

- `dotnet test` green; console build clean; `docs/openapi/v1.json` regenerated if any route shape
  changed (`OpenApiDocumentTests`' committed-snapshot test catches a forgotten regen).
- **Owner test, actually run**: claim a fresh instance via Google (or, if reusing the shared local dev
  instance, invite a new operator and accept via Google instead of a password), confirm they land with
  a working "Personal" organization, sign out, sign back in via Google, then invite a second operator
  by email the ordinary way and confirm *they* can still accept with a password — both doors stay
  open, per `CLAUDE.md`'s "minimal options everywhere."
- `git status` clean, conventional commits, branch off `main`.
- Write `docs/handoff/organizations-phase-3-report.md` — this is the last phase in the Organizations
  sequence, so its report should also update `docs/roadmap.md`'s Organizations section to mark all
  three phases shipped, and note plainly that the sequence is complete (per
  `docs/research/organizations.md`'s own phasing) rather than opening a `phase-4-prompt.md`. If a
  follow-up naturally suggests itself (a security-review-style pass over the new OAuth surface, the
  way Sites and Storage each got one after their own sequences completed — see `CLAUDE.md`'s Commands
  section for those), name it as a candidate next initiative rather than assuming it's wanted.
- Update `CLAUDE.md`'s Commands section with whatever configuration this phase adds — an instance-level
  Google client id/secret pair, at minimum.
