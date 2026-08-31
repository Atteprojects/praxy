# Session task — platform credentials for schedule/event-triggered functions

## Why this exists

A self-host comparison against Appwrite (2026-08-30, informal research session) found Appwrite's
function templates pre-grant fine-grained platform scopes (`databases.read/write`, `rows.read/write`,
etc.) to the function itself, independent of any calling user. Digging into why Praxy has no equivalent
found a real, narrow gap rather than a missing subsystem: `FunctionExecutionService.BuildEnvAsync`
(`src/Praxy.Functions/FunctionExecutionService.cs`, ~lines 82–103) only mints the `PRAXY_FUNCTION_JWT`
env var when `execution.TriggeredBy` starts with `"user:"`. A schedule-triggered execution
(`TriggeredBy = "schedule"`, set in `FunctionScheduler.cs`) or an event-triggered one
(`TriggeredBy = $"event:{claimed.Type}"`, set in `FunctionEventDispatcher.cs`) gets neither that JWT nor
any other platform credential — only its own stored `FunctionEnvVars` plus `PRAXY_FUNCTION_ID`/
`PRAXY_PROJECT_ID`. A nightly cleanup job or an event-driven write with no calling user has no built-in
way to authenticate to Praxy's own API today.

Importantly, **the fix is not a new permission system** — Praxy already has one, `src/Praxy.Auth/
ApiKeyService.cs`'s `ApiKeyScopes` (`UsersRead/Write`, `TeamsRead/Write`, `DatabasesRead/Write`,
`FunctionsRead/Write`, `ExecutionRead/Write`), essentially the same taxonomy Appwrite's per-template
scope picker showed, plus a console UI already built for it (`ApiKeysPage.tsx`'s scope checkbox grid).
This task wires a function to that existing primitive for the case where it has no calling user to
inherit a JWT from — nothing more.

Read `FunctionExecutionService.BuildEnvAsync`, `src/Praxy.Auth/ApiKeyService.cs` in full, and
`ApiKeysPage.tsx`'s scope-picker UI before writing anything. Work on a new branch off `main`. Read
`CLAUDE.md` first.

## Non-goals — do not build these

- **No new scope taxonomy.** Reuse `ApiKeyScopes` as-is unless you find a genuine, concrete gap while
  implementing — don't add scopes speculatively.
- **No default access.** Deny-by-default per `CLAUDE.md`'s cross-phase rule: every existing and new
  function starts with zero granted scopes and zero platform credential injected for non-user-triggered
  executions, exactly like today. An operator must explicitly grant scopes per function.
- **No changes to the user-triggered path.** `PRAXY_FUNCTION_JWT`'s existing behavior for
  `TriggeredBy` starting with `"user:"` is unchanged — this task only fills in the gap for `"schedule"`
  and `"event:*"`.
- **No new console scope-picker component** if `ApiKeysPage.tsx`'s existing checkbox grid can be
  extracted/reused directly — check before building a second one.

## Scope

1. **Investigate and choose one credential-delivery mechanism** — both are legitimate; pick based on
   what you find once you're actually looking at `AccountJwtService`/`ApiKeyService`, and record the
   choice and why in the report:
   - **(a) Persisted, function-owned API key.** A function gets an optional API key created through the
     existing `ApiKeyService.CreateAsync`, with operator-chosen scopes, injected as e.g.
     `PRAXY_FUNCTION_API_KEY` into every non-user-triggered execution's env. Simplest, reuses 100% of
     existing infra, but is a long-lived secret persisted like any other credential — needs to be
     revocable, and needs deciding whether deleting/disabling the function should cascade to revoking it.
   - **(b) Short-lived scoped service JWT.** Extend `AccountJwtService` with a variant not tied to a
     `userId` — minted fresh per execution, matching the security posture of the existing
     `PRAXY_FUNCTION_JWT` pattern more closely (nothing to revoke, nothing at rest). More work: touches
     shared auth infrastructure other things may depend on, so verify nothing assumes every minted JWT
     has a real `userId` before making this change.
2. **Per-function scope grant**: a `functions` column (or a small related table if a function might one
   day hold more than a flat scope list) storing the operator-chosen `ApiKeyScopes` subset. Console:
   a "Platform access" section on `FunctionSettingsPage.tsx` reusing `ApiKeysPage.tsx`'s scope checkbox
   grid, defaulting to none selected.
3. **Wire `FunctionExecutionService.BuildEnvAsync`**: in the branch that currently does nothing for
   non-`"user:"`-prefixed `TriggeredBy` values, inject the chosen credential (option a or b) — but only
   when the function has at least one granted scope. A function with zero granted scopes gets no
   credential at all, same as today.

## Landmines — read before writing code

- **Whichever mechanism you choose, its authorization check must be the exact one already used
  elsewhere** — find where `ApiKeyService`-backed keys (or `AccountJwtService`-minted JWTs) are validated
  today and reuse that path for the new credential. Don't write a parallel, function-specific
  authorization check that could drift from the real one.
- **If you choose (a),** confirm what happens today when a function is deleted or disabled while holding
  a live API key, and make sure that key doesn't outlive the function unintentionally.
- **If you choose (b),** grep for anywhere that assumes a JWT minted by `AccountJwtService` always has a
  real `userId` behind it (claims parsing, audit logging, anything user-attribution-related) — a
  service-scoped JWT with no user is a new shape for that type, and something downstream may not expect
  it.

## Tests

`tests/Praxy.Tests.Integration/` — a new `FunctionScheduledCredentialsTests.cs`: a schedule-triggered
execution against a function granted `databases.read`/`rows.read`-equivalent scopes can actually read
Tables data through the injected credential; the same function with zero granted scopes gets no
credential and a Tables call inside it fails through the normal auth path (not a function-specific
special-cased rejection); an event-triggered execution behaves the same way as schedule-triggered for
this purpose.

## Done means

- `dotnet test` green.
- `tsc -b && vite build` clean.
- Owner click-tests granting a schedule-triggered function a scope in the console and confirms it can
  read/write Tables data on a real scheduled run; confirms a function with no granted scopes still can't.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/functions-scheduled-credentials-report.md`, stating which mechanism (a or b) was
  chosen and why.
