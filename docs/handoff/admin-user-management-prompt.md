# Session task — let an operator actually manage a user (API + console)

## Why this exists

A console operator can today set an app user's `status` and `labels`, list and
revoke their sessions, and delete them. That is the whole surface
(`ConsoleAuthAdminEndpoints.cs:61-72`). There is no way to change a user's
email, name, or password, no way to mark them verified, and no way to resend a
verification email.

The consequence is concrete: **a user who mistypes their email at signup is
permanently stuck.** They cannot verify (the mail goes to an address that isn't
theirs), they cannot recover (recovery mails the same wrong address), and no
operator can fix it. The only remedy available today is deleting the account and
losing everything attached to it.

This is item #2 of the post-v0.1.0 gap analysis. Item #1 (function execute
permissions + data-plane rate limits) shipped on 2026-08-19 and is merged and
deployed — read `git log` from `b9a641a` for the house style if you want a
recent reference.

Work on a new branch off `main`. Read `CLAUDE.md` first. This is a single
post-Phase-9 feature, not a numbered phase — do not re-plan the roadmap or pull
work forward.

## Non-goals — do not build these

- **No self-service email change for app users.** `PATCH /v1/account/email` is a
  separate gap (it needs a confirm-to-the-new-address flow to not be an account
  takeover vector). This task is the *operator's* surface only.
- **No account deletion/export (GDPR) work.** Deletion already exists.
- **No MFA, no magic URL, no OTP.** Deferred per CLAUDE.md's fixed decisions.
- **No bulk user operations**, no CSV import.
- **No password-policy expansion.** `ProjectAuthSettings.PasswordMinLength`
  already exists and is enough; reuse it.

## Scope

On the console admin surface
(`/v1/console/projects/{projectId}/users/{userId}`, `RequireOperatorFilter` +
`ConsoleProjectFilter`, same chain as the existing handlers):

1. `PATCH .../users/{userId}/email` — change the address.
2. `PATCH .../users/{userId}/name` — rename.
3. `PATCH .../users/{userId}/password` — operator-set password.
4. `PATCH .../users/{userId}/verification` — mark verified / unverified directly.
5. `POST .../users/{userId}/verification` — resend the verification email.
   **Read the landmine about the redirect URL before designing this one.**

Plus the console screens for all five, and integration tests.

Decide as you go whether the equivalent server-side (`/v1/users/{userId}/…`, API
key + `users.write` scope) endpoints should exist too. `UsersServerEndpoints.cs`
mirrors the console surface for status/labels/sessions today, so the symmetry
argument is real — but so is "don't build what nothing asks for". State your
choice and reasoning in the final summary either way.

## Landmines — read before writing code

These are verified against the current code, not guesses.

- **`UpdatePasswordAsync` cannot be reused as-is.**
  `AppAuthService.cs:351` takes `oldPassword` and throws
  `401 user_invalid_credentials` unless `user.PasswordHash is null` (the
  OAuth-only case). An operator reset has no old password by definition. You need
  an explicit operator path — a separate method, or a parameter that is honest
  about what it does. Do **not** quietly relax the existing check; the app-user
  flow depends on it.

- **Changing a password does not revoke sessions today.** It publishes
  `users.<id>.update.password` and nothing consumes that to kill sessions. For an
  *operator-initiated* reset the usual reason is "this account is compromised or
  locked out", where leaving live sessions alive is arguably wrong. Make this an
  explicit decision, implement it, and say which way you went. The machinery
  exists — `DeleteUserSessions` already does exactly this, and Phase 1's
  `sessions.delete` event already closes live realtime sockets.

- **Email is uniquely indexed on `(project_id, email)`** (`PraxyDb.cs:105`).
  Changing an email to one already in the project raises Postgres `23505`. It must
  surface as `409 user_already_exists` — the existing type — not an unhandled 500.
  `FunctionsService.CreateAsync` shows the `DbUpdateException`/`PostgresException`
  catch pattern to copy.

- **Changing an email must reset `EmailVerified`.** Otherwise an operator can
  point a verified account at any address and it stays "verified". This matters
  more than it looks: `users/verified` is a *permission role* the query compiler
  and realtime fan-out both honour (`RoleResolver.cs`), so verified-ness grants
  data access. Same reason the mark-verified endpoint deserves an audit entry.

- **`SendVerificationAsync` needs a redirect URL, and the console does not have
  one.** `AppAuthService.cs:265` validates `url` against the project's platform
  allowlist and throws if the user is already verified. The account-side endpoint
  gets the URL from the client, which knows its own app. An operator clicking
  "resend" in the console does not. Options, none free: take the URL as a request
  field and let the operator pick from registered platforms; store a per-project
  default verification URL in `ProjectAuthSettings`; or drop the resend endpoint
  and rely on mark-verified. **Pick one deliberately and say why.** Do not invent
  an unvalidated URL — the allowlist is a security control (architecture.md's
  threat model).

- **A user may have linked OAuth identities.** `Identity.ProviderEmail` is
  separate from `User.Email` and is not affected by an email change. Decide
  whether that is fine (it probably is — they are different facts) and make sure
  the console does not imply otherwise.

- **Audit every one of these.** `AuditAsync` in `ConsoleAuthAdminEndpoints.cs`
  is the helper. Follow the precedent set on 2026-08-19: a security-relevant
  change gets its own action string rather than being folded into a generic one
  (`functions.execute.update` was the first). `users.password.reset` and
  `users.email.update` deserve to be distinguishable from `users.update`.

- **The audit log is still write-only** (gap #3). You are adding entries nobody
  can read yet. That is expected — do not build the read surface here.

## Console

`UserDetailPage.tsx` already has an overview/sessions/memberships tab shape with
an `OverviewTab` that edits labels and status inline. Extend it; do not add a
new screen.

Available primitives: `PageHeader`, `IdChip`, `Badge`, `Tabs`, `DataTable`,
`ConfirmButton`, `Modal`, `Field`, `Toggle`, `ErrorNote`, `useToast`
(`console/src/components/`). Hooks live in `console/src/api/auth.ts` — follow the
`useUpdateUserStatus`/`useUpdateUserLabels` shape. Terminology goes in
`console/src/strings.ts`.

Design notes:

- A password reset and an email change are destructive-ish. Use `ConfirmButton`
  and say plainly what happens — especially if you decide a reset revokes
  sessions, which the operator must be told *before* clicking, not after.
- The console shows the generated password once, or the operator types one.
  Either is fine; reveal-once matches the API-key precedent. Never round-trip a
  password back in a GET.
- If email change resets verified status, the UI has to say so up front.

## Tests

`tests/Praxy.Tests.Integration/` — Testcontainers, `postgres:17-alpine`, shared
collection fixture. `ConsoleAdminAuthTests.cs` is the closest neighbour;
`FunctionExecutePermissionTests.cs` is a recent example of the style. Cover:

- Operator changes an email → the user can log in with the new address, the old
  one is rejected, and `emailVerified` is back to false.
- Changing to an address already used in the project → `409 user_already_exists`,
  not a 500.
- Operator-set password → the user logs in with it; the old password fails; and
  whatever you decided about existing sessions is asserted explicitly.
- Mark verified → `users/verified` shows up in that user's resolved roles
  (`GET /v1/account/roles` is the existing debug endpoint) and the audit entry
  records it.
- A second project's operator cannot touch this project's user (the
  `ConsoleProjectFilter` boundary) — `ConsoleGuardTests.cs` shows the shape.
- The reserved `console` project still refuses the whole surface.

## Done means

- `dotnet test` green (needs Docker). Currently 324 unit + 132 integration.
- `npm run build --prefix console` green.
- OpenAPI snapshot regenerated — **and note that this is now enforced**:
  `OpenApiDocumentTests` fails if the committed snapshot drifts, if any operation
  lacks a documented response, or if the error envelope is missing. Your new
  endpoints need `.Produces<T>()` at the map call or the suite goes red.
  Regenerate per `docs/api-reference.md`.
- `git status` clean, conventional commits, on a new branch off `main`.
- You click-tested it yourself against a **throwaway** stack, not the persistent
  dev one: change an email, log in as that user with the new address, reset a
  password, mark verified, and confirm the audit rows landed.
- No `docs/handoff/` report needed (feature, not a numbered phase). Do state in
  your final summary: the session-revocation decision, the verification-URL
  decision, and whether you added the server-side `/v1/users/…` equivalents.

## Deploying (only if the owner asks)

`praxycore.dev` runs on a DigitalOcean droplet; the procedure is in
`docs/self-host.md`'s Upgrading section — backup first, `git pull origin main`,
then `docker compose -f deploy/docker-compose.yml --profile https up -d --build`.
The deploy needs an SSH key that lives on the owner's own machine, so it cannot
run from a cloud session. Do not deploy unless asked.
