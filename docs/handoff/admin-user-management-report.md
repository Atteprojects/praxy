# Admin user management — report

**Status: complete.** Branch `feat/admin-user-management` off `main`, four commits, `git status`
clean. 470 .NET tests green (324 unit + 146 integration, up from 324 + 132). Console
`tsc -b && vite build` clean. OpenAPI snapshot regenerated — an additive-only diff, 8 new paths.
Click-tested end to end against an isolated throwaway instance, driving the real console UI.

This closes item #2 of the post-v0.1.0 gap analysis. It is a feature, not a numbered phase, so
there is no follow-on prompt; the prompt itself said no report was needed, and this one exists
because the owner asked for it afterwards.

The problem it removes is concrete. A user who mistyped their address at signup was permanently
stuck: they could not verify (the mail goes to an address that isn't theirs), could not recover
(recovery mails the same wrong address), and no operator could fix it. Deleting the account and
losing everything attached to it was the only remedy.

## What shipped

**Service layer** (`src/Praxy.Auth/AppAuthService.cs:379-447`) — three new methods under an
`operator-initiated updates` heading, deliberately separate from the account-side methods above
them:

- `AdminUpdateEmailAsync` — validates and normalizes, resets `EmailVerified`, catches `23505` and
  rethrows as `409 user_already_exists`, publishes `users.<id>.update.email`.
- `AdminResetPasswordAsync` — validates against the project's `PasswordMinLength`, sets the hash,
  calls `DeleteAllSessionsAsync`, publishes `users.<id>.update.password`.
- `AdminSetEmailVerifiedAsync` — sets the flag, publishes `users.<id>.update.verification`.

Renaming reuses the existing `UpdateNameAsync`; resending reuses the existing
`SendVerificationAsync` unchanged, allowlist validation and all.

**Console admin routes** (`src/Praxy.Api/Endpoints/ConsoleAuthAdminEndpoints.cs:67-72`) — five
endpoints on the existing `RequireOperatorFilter` + `ConsoleProjectFilter` chain:

| Route | Audit action |
| --- | --- |
| `PATCH .../users/{userId}/email` | `users.email.update` |
| `PATCH .../users/{userId}/name` | `users.name.update` |
| `PATCH .../users/{userId}/password` | `users.password.reset` |
| `PATCH .../users/{userId}/verification` | `users.verification.grant` / `users.verification.revoke` |
| `POST .../users/{userId}/verification` | `users.verification.send` |

**Server API** (`src/Praxy.Api/Endpoints/UsersServerEndpoints.cs`) — the four state-changing
`PATCH`es mirrored at `/v1/users/{userId}/…` under the existing `users.write` scope. No server-side
resend.

**Console** (`console/src/screens/UserDetailPage.tsx`, `console/src/api/auth.ts`) — the
user-detail overview tab extended with three cards; no new screen. `ProfileCard:193` (email and
name editable in place), `VerificationCard:280` (settle verified-ness, or resend), `PasswordCard:387`
(type or generate, reveal once). Five hooks following the `useUpdateUserStatus` shape.

## The three decisions the prompt asked to be stated

**Session revocation on an operator password reset — yes, it revokes all.** An operator resets
because an account is locked out or compromised, and in the second reading the live sessions are
precisely what an attacker is holding. `ConfirmRecoveryAsync` already takes that stance on the
self-service arm of the same situation, so this is one behaviour rather than two that have to be
remembered separately. `DeleteAllSessionsAsync` does the work, so Phase 1's `sessions.delete` event
closes live realtime sockets for free. The console states it in the confirm dialog — before the
click, not in a toast after it. The server-side endpoint revokes identically; one implementation,
one behaviour.

**Verification URL — a request field, checked against the existing platform allowlist.** The
alternative (a per-project default in `ProjectAuthSettings`) would put a redirect URL in a second
place, where it goes stale silently: an app that moves its verification route leaves a broken
default nobody notices until an operator clicks resend. It would also need re-validating at send
time regardless, since the allowlist is editable, so it buys no validation simplification — only a
settings field and an auth-settings screen change the task did not scope. The console softens the
cost of typing it: the registered hostnames are named under the input (linking to Platforms when
there are none), and the last URL that worked is remembered per project in `localStorage`.

**Server-side `/v1/users/…` equivalents — added, minus the resend.** The symmetry argument won
because the asymmetry is actively harmful rather than untidy: `/v1/users/{id}/status` already
exists, so a backend script automating user administration hits precisely the wall this feature
removes from the console. The service methods already did the work, so it came to about sixty
lines. `POST .../verification` has no server counterpart — it needs a redirect URL, which is an
app-facing concern that `POST /v1/account/verification` already covers for a user's own session,
and nothing outside the console asks for the operator variant.

## The landmines, and what happened to each

- **`UpdatePasswordAsync` is untouched.** Its `oldPassword` check is load-bearing for the app-user
  flow. The operator paths are separate methods that are honest about having no old password to
  verify, rather than a parameter that quietly relaxes the existing one.
- **Email uniqueness.** `(project_id, email)` collisions surface as `409 user_already_exists`, using
  `FunctionsService.CreateAsync`'s `DbUpdateException`/`PostgresException` catch pattern. The
  rejected write leaves nothing behind — the entity is detached and the original address still logs
  in, which the tests assert rather than assume.
- **Changing an email resets `EmailVerified`.** `users/verified` is a permission role the query
  compiler and realtime fan-out both honour, so it may only sit on an address someone has actually
  proved they own. One wrinkle the prompt did not name: re-submitting the address a user *already*
  has would otherwise strip their verified status silently, so that case short-circuits as a no-op.
- **OAuth identities.** `Identity.ProviderEmail` is untouched by an email change — they are
  different facts. The console says so explicitly in a note under the identities list, so the UI
  does not imply otherwise.
- **Audit actions.** Each change gets its own action string rather than folding into a generic
  `users.update`, following the precedent `functions.execute.update` set. Marking verified and
  un-verifying are separate actions too, since the first grants data access.
- **The audit log is still write-only.** These entries are unreadable through any surface today, as
  expected; gap #3 is untouched.

## Tests

`tests/Praxy.Tests.Integration/ConsoleUserManagementTests.cs` — 14 cases, all of the prompt's list
plus four the code invited:

- Email moves, the old address 401s, `emailVerified` is back to false, and the still-live session
  loses `users/verified` on its very next request.
- Setting the same address leaves verified alone.
- A collision inside the project is `409 user_already_exists`, and the original address still works.
- An invalid address is a `400` with an `email` field error.
- Rename sticks, and reads back through `GET`.
- Operator-set password: old password rejected, new one logs in, the pre-existing session 401s
  through the warm cache, and the sessions list is empty — the revocation decision asserted
  explicitly rather than implied.
- The project's `passwordMinLength` still applies to an operator-set password.
- Marking verified puts `users/verified` and `user:<id>/verified` in the resolved roles and writes
  `users.verification.grant`; un-verifying reverses both.
- `users.email.update`, `users.password.reset` and `users.name.update` are distinguishable in the
  audit log.
- Resend: an off-allowlist URL is a `400` with a `url` field error and sends nothing; an
  allowlisted one mails a link that completes the real `PUT /v1/account/verification` flow; a
  second attempt once verified is refused.
- A second operator gets `404 project_not_found` on **every** one of the five routes, and so does
  the reserved `console` project — both boundaries walked route by route rather than spot-checked.
- The `/v1/users` mirror does the same four things, and refuses all four without `users.write`.

`CreateSecondOperatorAsync` moved from `OrganizationApiTests` up to `ApiTestBase` so the
cross-project boundary test could use it without copying thirty lines of SQL.

## Owner-test transcript

Run against a **throwaway** stack, not the persistent dev one: a fresh `postgres:17-alpine`
container on port 55432 and a second API instance on port 5099, with the console build served from
that instance's own `wwwroot` so the click-test ran single-origin and needed no proxy edit. The dev
API on 5090 was never touched. (One false start: the first `dotnet run` bind-failed against 5090
and I fetched *its* stale OpenAPI document before noticing — the snapshot in the commit is from the
5099 instance, and matches what `OpenApiDocumentTests` generates.)

Claimed the instance, created project `Acme`, signed up `typo@exmaple.com` through the data plane
— the mistyped-address case, reproduced rather than simulated. Then, in the console:

1. **Changed the email** to `ada@example.com`. The confirm dialog stated the address move, the
   verified reset with its permission consequence, and that sessions are *not* revoked. Afterwards:
   `POST /v1/account/sessions/email` with the new address returned `201`, with the old address
   `401 user_invalid_credentials`.
2. **Marked verified.** `GET /v1/account/roles` for that user returned `users/verified` and
   `user:<id>/verified`.
3. **Generated and set a password.** The confirm dialog led with the revocation in coral before the
   button. Afterwards: the session opened in step 2 returned `401 general_unauthorized`, the old
   password `401 user_invalid_credentials`, the generated one `201`. The reveal-once panel showed
   the value with a Copy button and the "only time it is shown" note.
4. **Registered platform** `app.example.com`, marked the user unverified again, and **resent
   verification**. `https://evil.example.net/verify` rendered "Hostname is not on the project's
   platform allowlist." under the input and mailed nothing. `https://app.example.com/verify`
   toasted success; the mailed link's `userId`/`secret` completed `PUT /v1/account/verification`,
   returning `emailVerified: true`.
5. **Renamed** to `Ada Lovelace`; the page header updated with it, and the resend section correctly
   disappeared now that the user was verified again.

All eight audit rows landed with distinguishable actions and `admin:<id>` actors. Browser console
carried no JS errors — the only two entries were the expected pre-claim `401` and the deliberate
off-allowlist `400`.

## Deliberately not built

Everything on the prompt's non-goals list: no self-service `PATCH /v1/account/email` (it needs a
confirm-to-the-new-address flow to not be an account-takeover vector, and is its own gap), no
GDPR deletion/export, no MFA or magic-url or OTP, no bulk operations or CSV import, no
password-policy expansion beyond the existing `PasswordMinLength`. No audit-log read surface —
that is gap #3.

Section headings stayed inline rather than moving to `console/src/strings.ts`. The sibling headings
in that same component — "Profile", "Labels", "Identities", "Danger zone" — are all inline, and
`STR` holds nav-level domain nouns; splitting them half-and-half would have been worse than either
choice on its own.

## Commits

```
22150c4 feat(console): manage a user's email, name, password and verification
c0c6ce6 test(api): cover the operator's user-management surface
a29cf5a feat(auth): operator-initiated email, name, password and verification changes
d381b8d refactor(test): share the second-operator helper from ApiTestBase
```

Ordered so every commit is green on its own: the OpenAPI snapshot ships with the endpoints that
generate it, and the tests land after the surface they exercise.

## Not deployed

`praxycore.dev` is untouched. The procedure is in `docs/self-host.md`'s Upgrading section and needs
an SSH key that lives on the owner's own machine.

One local side effect worth knowing: `src/Praxy.Api/wwwroot` (gitignored console build output) was
rebuilt from this branch for the click-test, so a still-running dev API on 5090 serves the new
console against its older binary. Restarting it clears that.
