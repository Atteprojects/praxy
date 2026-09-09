# Organizations, Phase 3 (operator OAuth) — report


> **Superseded 2026-09-08.** Operator Google sign-in was removed — see
> `docs/handoff/console-oauth-removal-report.md` for why. Kept as the record of what was built.

**Status: complete.** Every item in `docs/handoff/organizations-phase-3-prompt.md`'s scope shipped:
operator Google sign-in on claim, login, and organization-invite accept, each gaining the option
alongside its existing password door, never in place of it. This is the last phase in the
Organizations sequence (`docs/research/organizations.md`'s own phasing) — see "Sequence complete"
below rather than a `phase-4-prompt.md`.

## The design decision the prompt asked for: where credentials and identities live

The prompt's own "one thing to understand" section named this as the real design work, not
something to inherit from `OAuthService`. Decided:

- **Credentials**: a new instance-wide `Praxy:ConsoleAuth:Google:ClientId`/`ClientSecret`
  (`ConsoleOAuthOptions`, `src/Praxy.Auth/OAuth/ConsoleOAuthOptions.cs`) — same plain-record shape as
  `Praxy.Vcs.GitHubAppOptions`. Operators have no project to hang `ProjectAuthSettings`-style
  per-project credentials off of, so this is startup config, not console-editable, exactly like the
  GitHub App. Unset (either half empty) means the feature is off — `GoogleConfigured` is the one
  place that's decided, and both the start and callback endpoints check it before doing anything.
- **Redirect safety**: no caller-supplied success/failure URL at all, unlike the app-user flow. The
  console is always served from the same origin a browser navigates to the start endpoint from
  (`Program.cs`'s SPA fallback serves it at the API's own root — confirmed by reading that file, not
  assumed), so the callback always redirects to a fixed, same-origin relative path: `/` on success,
  `/login` (claim/login) or `/accept-invite?organizationId=...&userId=...&secret=...` (invite, so
  the invitee doesn't lose their invite link's context on a failure) with `?oauthError=<type>` on
  failure. This sidesteps the landmine's open question entirely — there's no allowlist to design
  because there's nothing to allow beyond "the origin we're already on."
- **Identity space**: reused the existing `Identities` table, scoped by `ProjectId ==
  Ids.ConsoleProjectId` rather than a new table — the landmine's own suggestion, and correct: the
  existing `(ProjectId, Provider, ProviderUid)` unique index already gives "at most one console
  identity per Google account" for free, and every other Identity-consuming code path (`ResolveUserAsync`'s
  shape, the encrypted-token fields) generalizes unchanged. No new table, no migration.

## Not `OAuthService` — what `ConsoleOAuthService` actually reuses

Read `OAuthService.cs`'s whole call chain first, per the prompt's instruction, before writing
anything. Confirmed unreusable as-is: `StartAsync`/`HandleCallbackAsync` take a `Project` and pull
credentials from `ProjectAuthSettings.Parse(project.Settings)`; `ValidateRedirectUrlAsync` checks a
project's registered `Platform`s; `ResolveUserAsync` creates/links a `User`+`Identity` scoped to
`project.Id` and auto-creates a new app user when nothing matches. None of that fits an operator.

What's directly reusable, and reused as-is: `IOAuthProvider`/`GoogleOAuthProvider`
(`BuildAuthorizeUrl`/`ExchangeCodeAsync`/`FetchProfileAsync` don't know or care whether the result is
an app user or an operator), `IOAuthProviderRegistry`, `CompactJwt` (the signed state cookie),
`Secrets`, and `InstanceKey` (token encryption). `ConsoleOAuthService`
(`src/Praxy.Auth/OAuth/ConsoleOAuthService.cs`) is a new, separate class built from those pieces —
`OAuthService` itself is untouched by this phase.

## The property with no app-user equivalent: a claimed instance never auto-creates an operator

This is the biggest behavioral divergence from the app-user flow, and worth stating plainly because
it's easy to miss by analogy: app users get open signup via Google (no matching account → create
one). Operators do not — there has never been an operator-signup endpoint at all (only `/claim`, and
an organization invite), so a claimed instance's ordinary Google login only ever *resolves* an
existing operator:

1. An `Identity` already links this Google account → use it.
2. No identity, but a **confirmed** operator account already exists at this (verified) email → link
   the identity to it (first-time link — the same operator who signed up with a password now also
   has Google).
3. Neither → `console_oauth_account_not_found` (401). Never a new `User` row.

Case 2 is also where an unverified provider email or a still-*pending* (unaccepted) invite both fall
into the same refusal, deliberately: an unverified email proves nothing about who controls the
Google account, and a pending invite's own row has no password yet either, so letting a plain Google
login stand in for it would bypass the invite-accept door's own secret check entirely. Both are
tested (`Unverified_google_email_does_not_silently_link_to_a_different_existing_account`,
`An_unaccepted_invite_does_not_let_a_plain_google_login_stand_in_for_the_accept_door`).

## The atomic claim core, now shared by both doors

`ConsoleAuthService.ClaimAsync` (password) used to inline its own transaction + advisory-lock +
"Personal" org creation. Extracted into `ClaimResolvedUserAsync(User, extraEntities, ...)` — the
password door builds its own `User` and calls it with no extra entities; the Google door
(`ConsoleOAuthService`, a different project — it cannot inject `Praxy.Api`'s `SetupTokenService`
itself, see below) builds a passwordless `User` plus the new `Identity` and passes the identity in
`extraEntities`, so a claim can never succeed without its linked identity or vice versa — one
`SaveChangesAsync`, one commit. The lock key, the re-check-under-lock, and the "closed forever after"
property are now genuinely one implementation, not two copies that could drift.

**The setup-token check crosses a project boundary via a delegate, not a shared type.**
`Praxy.Auth` (where `ConsoleOAuthService` lives) doesn't and shouldn't reference `Praxy.Api` (where
`SetupTokenService` lives — checked the `.csproj` references before assuming otherwise).
`HandleCallbackAsync` takes `Func<string?, bool> validateSetupToken`; `ConsoleOAuthEndpoints.Callback`
passes `setupTokens.Validate` as that delegate. The setup token itself rides the signed state cookie
(alongside `verifier`/`state`/`provider`, exactly like the app-user flow's `success`/`failure`) since
the browser-navigated round trip through Google has no other way to carry it from the start request
to the callback.

## Accepting an invite via Google: the extra check the password door doesn't need

`OrganizationsService.AcceptInviteAsync`'s secret-validation block was extracted into
`ValidateInviteSecretAsync` (now `internal`, same assembly) — the *exact* same property either door
gets: `Confirmed` checked before the secret, a wrong secret and a nonexistent invite share the
identical error and dummy-hash timing. `ConsoleOAuthService.AcceptInviteAsync` (a same-named but
distinct private method, on the OAuth service) calls that shared core, then adds what the prompt's
own landmine asked for: the linked Google account's own **verified** email must equal the invited
row's fixed email address, or the door refuses with the new `console_oauth_email_mismatch`. Reasoned
about why this is additive, not a weakening of the password door's trust model: possession of the
invite secret is already the proof of receipt (identical to the password door — a stolen secret can
set *any* password too), so this isn't closing a hole the password door has; it's the OAuth-specific
guard against linking the wrong Google account to a fixed-email row. A second, independent check:
if the resolving Google `uid` is *already* linked (via `Identity`) to a **different** console user
than the one the invite targets, the door refuses with the new
`console_oauth_identity_already_linked` (409) rather than silently reassigning that Google account.

## New error types

Four, all distinct from `UserOauth2ProviderError` (kept for the provider-level failures shared with
the app-user flow — bad code, no usable email, an unverified email that already belongs to an
account — since those are genuinely the same class of problem regardless of caller type):

- `console_oauth_not_configured` (400) — the instance hasn't set `Praxy:ConsoleAuth:Google:*`.
- `console_oauth_account_not_found` (401) — a claimed instance's Google login resolved nothing; the
  one rule with no app-user equivalent (see above).
- `console_oauth_email_mismatch` (401) — invite-accept, the Google account's own email doesn't match
  the invited address (or isn't verified).
- `console_oauth_identity_already_linked` (409) — invite-accept, the Google account is already tied
  to a different operator.

## Console

- **`LoginPage`** (`console/src/screens/LoginPage.tsx`) — both `LoginForm` and `ClaimForm` gain a
  "Continue with Google" link (a plain `<a href>`, not a mutation — clicking it leaves the console
  for Google entirely) below an "or" divider, gated on the new `capabilities.googleOAuthEnabled`
  flag. `ClaimForm`'s setup-token field became controlled state (previously read only via
  `FormData` at submit) so the Google link can carry its current value in the query string it
  navigates to.
- **`AcceptOrganizationInvitePage`** — an "Accept with Google" link carrying the same
  `organizationId`/`userId`/`secret` the page already reads off its own URL, gated the same way.
- **`useFormError`** (shared by both `LoginForm` and `ClaimForm`) now also seeds from a failed
  Google redirect's `?oauthError=<type>` — `console/src/api/oauth.ts` maps known error types to a
  human message, with a generic fallback for anything unmapped, and exposes the URL-builder
  (`googleSignInUrl`) both screens use.
- **New `btn-secondary` utility** (`console/src/styles.css`) — an outlined button, since neither the
  filled `btn-primary` nor the borderless `btn-ghost` fit a "secondary action, still a real button"
  slot; this is the first thing in the console that needed one.
- **No client-side token exchange.** Unlike the app-user OAuth flow (which lands on the app's own
  `success` URL with `userId`+a wrapped secret, then calls `POST /v1/account/sessions/token` to
  exchange it), the console's callback sets the `praxy_session_console` cookie directly and redirects
  to `/` — a full page load, so `HomeRedirect` and every query on the new page mount fresh. This also
  means Phase 2's own same-tab identity-swap cache-staleness landmine (its bug #2:
  `["organizations"]` needing invalidation on `useSessionMutation`'s `onSuccess`) **cannot recur
  here** — there is no `useSessionMutation` call in the OAuth path to forget it on, because the
  browser physically reloads the page.

## New capability flag

`GET /v1/console/capabilities` gained `googleOAuthEnabled` (a sibling of the existing
`setupTokenRequired`, not nested under `features` — like that flag, it reflects instance
*configuration*, not a shipped-or-not module). `docs/openapi/v1.json` regenerated; `OpenApiDocumentTests`
green.

## Tests

`tests/Praxy.Tests.Integration/ConsoleOAuthFlowTests.cs` (ten `[Fact]`s, `FakeOAuthProvider` reused
exactly as `OAuthFlowTests` already established it) plus two small dedicated classes for settings
that would otherwise leak into every test in the main class:

- `Claiming_the_instance_via_google_creates_the_operator_links_identity_and_creates_personal` — the
  full round trip on a fresh, unclaimed instance.
- `A_second_claim_attempt_via_google_is_refused_once_the_instance_is_claimed` — a second, wholly
  unrelated Google identity lands on the ordinary-login branch (`console_oauth_account_not_found`,
  not a second claim), *and* the password door's own `/v1/console/claim` still returns
  `instance_already_claimed` afterward, *and* exactly one organization exists in the database — the
  "closed forever after" property proven from both doors and the database, not just one endpoint's
  response code.
- `Signing_in_via_google_resolves_to_the_same_operator_a_password_login_does` — claims via password,
  signs in via Google with a matching verified email, asserts the same account id and exactly one
  `User`/`Identity` row (no duplicate).
- `Google_login_reuses_the_linked_identity_on_a_later_attempt_instead_of_relinking` — claim, then a
  second ordinary login with the same `uid`; still one user, one identity row.
- `Unverified_google_email_does_not_silently_link_to_a_different_existing_account` — an unverified
  Google profile claiming a password-claimed operator's email is refused
  (`user_oauth2_provider_error`), no session, no identity row created.
- `An_unaccepted_invite_does_not_let_a_plain_google_login_stand_in_for_the_accept_door` — the
  no-password-yet gap named above.
- `Accepting_an_organization_invite_via_google_links_identity_without_a_password_and_confirms_once` —
  the full accept round trip: no password ever set on the row, membership confirmed, and a replay of
  the exact same invite door returns `organization_invite_already_accepted` — "accepting twice is
  rejected," Phase 2's own property, exercised through the OAuth door per the prompt's own ask.
- `Accepting_an_invite_via_google_refuses_a_mismatched_email` — the extra check named above.
- `ConsoleOAuthDisabledTests` — default settings configure no Google client; capabilities reports
  `false` and the start endpoint refuses with `console_oauth_not_configured`.
- `ConsoleOAuthClaimSetupTokenTests` — `PRAXY_PUBLIC_URL` set, mirroring `SetupTokenTests`' own
  password-door test: no token, a wrong token, then the real one (read from
  `Factory.Services.GetRequiredService<SetupTokenService>().Token`, same as that test) — the last one
  succeeds via Google.

Existing suites re-run unchanged after the `ConsoleAuthService`/`OrganizationsService` refactors:
`OAuthFlowTests` (app users), `OrganizationApiTests`, `OrganizationMembershipApiTests`,
`OrganizationSeatQuotaTests`, `ClaimFlowTests`, `SetupTokenTests` — 43/43 green, confirming the
extracted `ClaimResolvedUserAsync`/`ValidateInviteSecretAsync` cores didn't change the password
doors' own behavior.

**Full-repo `dotnet test`: 626/626 unit, 364/365 integration.** The one failure —
`StorageStreamingTests.Files_round_trip_exactly_and_memory_does_not_grow_with_their_size` — is the
exact same pre-existing peak-heap memory heuristic Phase 1's and Phase 2's reports already flagged as
flaky and unrelated (no Storage file touched this phase either); it passed on its own immediately
after, re-run in isolation.

Console: `tsc -b && vite build` clean.

## Owner test, actually run

Done live against the shared local dev instance (`owner@test.local`, already claimed — see
`[[praxy-local-dev-instance]]`), api restarted first to pick up this branch, plus a temporary
dev-only `Praxy:ConsoleAuth:Google:ClientId`/`ClientSecret` in `appsettings.Development.json`
(reverted before finishing — never committed):

- `/login` (signed out): "Continue with Google" renders below the password form, with `href="/v1/console/sessions/oauth2/google"`
  (no extra params for an ordinary login). Clicking it round-tripped through the console → API `Start`
  endpoint → a real `https://accounts.google.com/...` authorize URL carrying the configured
  (placeholder) `client_id` — Google itself correctly rejected the unregistered client, which is the
  expected result without a real Google Cloud OAuth client; it proves the whole redirect chain is
  wired correctly up to the point only a real client id could get past.
- Invited a fresh test address from the Members screen, opened its accept-invite link: "Accept with
  Google" renders below the password field, `href` carries the exact
  `organizationId`/`userId`/`secret` from the emailed link.
- Cleaned up afterward: removed the test invite, reverted `appsettings.Development.json`, restarted
  `api` once more and confirmed `googleOAuthEnabled: false` again — the shared instance is back to
  its normal (Google-disabled) configuration, matching how Phase 2's own owner-test left it.
- The deeper flow properties (claim via Google, login resolving the same account, invite-accept
  linking without a password, the refusal cases) are exercised by the integration tests above against
  a `FakeOAuthProvider` rather than a real Google account, since this session has no real Google
  Cloud OAuth client to test against — the same tradeoff `OAuthFlowTests` already made for app users.

## Commands

New instance-level knob, documented in `docs/self-host.md`'s Configuration table and its own new
"Operator Google sign-in" section (setup walkthrough: create an OAuth client in Google Cloud
Console, register the one fixed redirect URI, set the two config values, restart):

- `Praxy:ConsoleAuth:Google:ClientId` / `ClientSecret` — unset means the feature is off. Redirect URI
  to register with Google: `<origin>/v1/console/sessions/oauth2/callback/google` — exactly one, since
  the console is always served from the same origin as these endpoints.

No other configuration changed.

## Sequence complete

This closes `docs/research/organizations.md`'s three-phase plan (the org itself → members and roles →
operator OAuth). `docs/roadmap.md`'s Organizations section is updated to mark all three phases
shipped. A natural next initiative, not yet scheduled: a security-review-style pass over this new
OAuth surface, the way Sites and Storage each got one after their own sequences completed — the
review would want to specifically re-verify the "claimed instance never auto-creates an operator"
property and the invite-accept email-match guard against a live instance, not just re-read this
report.
