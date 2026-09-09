# Removing operator OAuth — report

**2026-09-08.** Organizations Phase 3 shipped operator Google sign-in on 2026-09-06. It is removed
two days later, along with the security review that had been designed for it (PR #79) and a
half-finished implementation of that review.

## Why

Console SSO is **nearly free to offer in a managed service and genuinely annoying to offer per
self-hosted instance.** In a managed service you register one OAuth client centrally and every
tenant gets SSO with no setup. Self-hosted, the redirect URI is per-domain
(`https://<their-domain>/v1/console/sessions/oauth2/callback/google`), so **every single
installation** had to create a Google Cloud project, configure a consent screen, register a client,
and paste two secrets into `.env` before the feature did anything at all.

The owner's framing, which is the decisive one: most self-hosters don't want to configure OAuth just
to log in when email and password gets them started.

That asymmetry is also the likeliest reason Appwrite — the product this one is modelled on —
supports GitHub sign-in for the console on Appwrite Cloud and not on self-hosted, where an Appwrite
team member's answer to the direct question was "at the moment, you cannot (unless you modify the
source code)." What initially looked like a paywall to compete with is better read as an
architectural asymmetry to learn from.

`docs/research/organizations.md` had already half-seen this at design time — it called Phase 3
"convenience and managed-signup polish rather than a blocker." That read was right. What it should
have concluded is "not yet, and not here" rather than "last."

## What was removed

| | |
|---|---|
| Services | `ConsoleOAuthService`, `ConsoleOAuthOptions` (`src/Praxy.Auth/OAuth/`) |
| Endpoints | `ConsoleOAuthEndpoints` — `GET /v1/console/sessions/oauth2/{provider}` and its callback |
| Error types | `console_oauth_not_configured`, `console_oauth_account_not_found`, `console_oauth_email_mismatch`, `console_oauth_identity_already_linked` |
| Config | `Praxy:ConsoleAuth:Google:ClientId` / `ClientSecret` |
| Wire shape | `CapabilitiesResponse.googleOAuthEnabled` |
| Console | `src/api/oauth.tsx`; the "Continue with Google" / "Accept with Google" buttons and all `?oauthError=` handling on `/login` and `/accept-invite` |
| Tests | `ConsoleOAuthFlowTests`, `ConsoleOAuthSecurityTests`, `ConsoleOAuthDisabledTests`, `ConsoleOAuthClaimSetupTokenTests` |
| Docs | `docs/self-host.md`'s "Operator Google sign-in" section and its config-table row; the security-review design doc and prompt |

**Removing an error type is normally a breaking change** — `CLAUDE.md` calls error `type` strings
public API. It is safe here for a specific reason rather than by assumption: the feature shipped
**off by default** and no instance ever configured it, so no client has ever received one of these
four strings. Production was checked directly before any code was touched:
`select count(*) from praxy.identities where project_id = 'console'` returned **0**. Nothing to
migrate, no data lost.

## Two seams that existed only for this door

Both were extracted during Phase 3 specifically so the Google door could reuse them. With that door
gone, leaving them would have left dead generality that reads as intentional:

- **`ConsoleAuthService.ClaimResolvedUserAsync`** was "the atomic core every claim door shares",
  taking an `extraEntities` parameter whose only purpose was saving the Google `Identity` row inside
  the claim transaction. One caller remained and always passed `null`, so it is folded back into
  `ClaimAsync`. The advisory-lock behavior is unchanged — the body moved, nothing in it did.
- **`OrganizationsService.ValidateInviteSecretAsync`** was `internal` rather than `private`
  explicitly so the OAuth accept door could reuse it. It is `private` again. It stays a separate
  method rather than being inlined, because the property it exists to hold — a wrong secret and a
  nonexistent invite are indistinguishable in both error and timing (security-review-phase-3's
  Finding D) — is easier to keep true as one readable thing.

## What is unaffected

**The Organizations sequence itself.** Organizations, members, roles, invites, the last-owner rule,
the seat quota — all stand. The password invite door was always the primary one; accepting an
invitation still works exactly as it did. Only the Google door onto those things is gone.

App-user OAuth (`OAuthService`, `/v1/account/sessions/oauth2/...`, a project's own Google
credentials on its Auth Settings screen) is **entirely untouched** — a different flow, scoped to a
developer project. `console/src/screens/AuthSettingsPage.tsx` matched an early grep only because it
builds an app-user callback URL, and was correctly left alone.

## If this comes back

It probably does, when managed hosting exists — that is where the value was all along. Build it
*for* that context then: one centrally-configured client, and probably SAML/OIDC rather than Google
alone, since the customers who ask for console SSO are the ones paying for a managed tier. Don't
carry this implementation forward; it was shaped by the self-host constraint that made it not worth
having.

Recoverable from git:

- The implementation and its design: PR #75, and `docs/handoff/organizations-phase-3-report.md`
  (kept, with a superseded banner, as the record of what was built).
- The security review's design and prompt: PR #79.
- A half-finished implementation of that review — sign-in auditing, an email notification on OAuth
  login, and 460 lines of new security tests including attempted falsifications of
  `ConsoleOAuthService`'s own asserted properties — on the local branch `oauth-security-review-wip`,
  committed rather than discarded when the direction changed.

## Verification

- `dotnet build` clean; `npm run build --prefix console` clean.
- `docs/openapi/v1.json` regenerated from the running app, and
  `npm run check:api-types --prefix console` reproduces the committed generated types — the contract
  machinery from the console/API contract initiative catching its first real wire-shape removal
  (`googleOAuthEnabled`, 106 lines out of `schema.ts`).
- `dotnet test` — see below.
- A grep sweep for `ConsoleOAuth` / `console_oauth` / `googleOAuthEnabled` / `Praxy:ConsoleAuth:Google`
  across `src`, `console/src`, `tests`, `docs`, `deploy`, `CLAUDE.md` and `README.md` returns nothing
  outside this report and the superseded Phase 3 history. Note that `docs/roadmap.md` carried **two**
  copies of the Organizations phasing; the first sweep found only one, and the second copy was found
  by re-running the sweep rather than by assuming the first edit was complete.
