# Platform credentials for schedule/event-triggered functions — report

**Status: complete.** Every item in
`docs/handoff/functions-scheduled-credentials-prompt.md`'s scope shipped. Full-repo `dotnet test`
green — **380/380 unit, 208/208 integration** (real Postgres via Testcontainers, real Docker daemon
throughout, including two new Docker-based tests for this feature). Console `tsc -b && vite build`
clean.

## Mechanism chosen: (a) persisted, function-owned API key

The prompt asked to pick between (a) a persisted, function-owned `ApiKey` or (b) a short-lived,
service-scoped JWT minted fresh per execution. **(a) was chosen**, for a reason that only became
clear from reading the authorization call sites, not from reading `ApiKeyService`/`AccountJwtService`
in isolation:

Every place in `Praxy.Api` that enforces `ApiKeyScopes` today is written in terms of the concrete
`RequestPrincipal.Key(ApiKey)` case — `RowEndpoints.RequireScopeIfKey` (`if (Current(http) is
RequestPrincipal.Key) RequireScope(...)`), the `BypassRowPermissions` check next to it, `RoleResolver`'s
switch (`case RequestPrincipal.Key: return ["any"]`), `FunctionEndpoints.CallerIdentity`'s
`RequestPrincipal.Key(var apiKey) => $"key:{...}"` case, and more. Option (b) would have needed a new
`RequestPrincipal` case (a JWT carrying scopes but no `userId` is a new shape `JwtUser` can't
represent), which means updating *every one* of those call sites to also recognize it — otherwise a
service JWT either silently skips the scope check entirely (`RequireScopeIfKey` only fires `if
principal is Key`) or crashes `RoleResolver`'s switch outright. That is exactly the "parallel
authorization check that could drift from the real one" the prompt's own landmine warns against, just
distributed across several files instead of concentrated in one.

Backing the credential with a literal `ApiKey` row instead means every one of those call sites — today
and any added later — just works, unchanged. The cost is real but narrow and already well-precedented
in this codebase: the secret has to be recoverable (not hash-only like a normal API key) so it can be
re-injected on every matching execution, so it's encrypted at rest with `InstanceKey` — the exact
mechanism `FunctionEnvVar.ProtectedValue` already uses for the same "container needs the plaintext
back" reason.

## What shipped

**Database** (migration `20260831063726_FunctionPlatformCredentials`,
[`Entities/Functions.cs`](../../src/Praxy.Persistence/Entities/Functions.cs)): `functions` gains three
columns — `platform_scopes` (`text[]`, defaults `{}`), `platform_api_key_id` (nullable `uuid`, no DB-level
FK — see below), `platform_api_key_secret_protected` (nullable, `InstanceKey`-encrypted). No new table:
the prompt's "or a small related table" alternative wasn't needed since a function holds exactly one
flat scope list, not a growing collection.

**`Praxy.Functions`**
([`FunctionsService.cs`](../../src/Praxy.Functions/FunctionsService.cs), now depending on
`Praxy.Auth`'s `ApiKeyService`): `UpdateAsync` gained a `platformScopes` parameter (`null` = unchanged,
same convention as `execute`/`events`) validated against `ApiKeyScopes.All` exactly like
`ApiKeyService.CreateAsync` already does, then applied by a new `ApplyPlatformScopesAsync`:
- Empty scopes → revoke the underlying key (if one exists) and clear all three columns.
- First non-empty grant → `apiKeys.CreateAsync(fn.ProjectId, "function:<key>", scopes, ...)`, the exact
  same call `ConsoleAuthAdminEndpoints.CreateKey` makes for an operator-created key, and encrypt the
  returned secret.
- Scopes changed on an existing key → `ExecuteUpdateAsync` the `Scopes` column in place — no secret
  rotation, since this key is never shown to anyone to begin with.
- **Self-healing**: if that in-place update affects zero rows (an operator revoked this function-owned
  key directly from the project's own API keys page — `db.ApiKeys.Where(...).ExecuteDeleteAsync` there
  has no idea a function references it, matching the prompt's landmine about deletion/disable), it's
  treated exactly like "no key yet" and a replacement is minted transparently on the next scope save.
  No DB-level FK/cascade was added for this (deliberately — see Deviations), so this in-application
  reconciliation is what actually keeps `PlatformApiKeyId` from pointing at a dead row indefinitely.

`DeleteAsync` now also revokes the platform key (`ExecuteDeleteAsync` against `db.ApiKeys`) before
removing the function row — answering the prompt's "confirm what happens on delete" landmine directly:
before this change nothing referenced the key at all, so nothing would have cleaned it up.

[`FunctionExecutionService.cs`](../../src/Praxy.Functions/FunctionExecutionService.cs)'s
`BuildEnvAsync` gained the `else` branch the prompt's scope described: when `TriggeredBy` is exactly
`"schedule"` or starts with `"event:"` (deliberately *not* a catch-all `else` — `"console"`,
`"key:<id>"`, and `"guest"` triggers already have their own caller identity and are untouched, matching
the prompt's "no changes to the user-triggered path" instruction read strictly) *and* the function has
at least one granted scope, decrypt `PlatformApiKeySecretProtected` and inject it as
`PRAXY_FUNCTION_API_KEY`. Zero scopes → the branch is skipped entirely, identical to today.

**`Praxy.Api`**: `UpdateFunctionRequest`/`FunctionResponse` gained `PlatformScopes`; both
`FunctionEndpoints.UpdateFunction` (console) and `ServerUpdateFunction` (data-plane, `functions.write`
key) thread it through to the same `FunctionsService.UpdateAsync`, with a new `functions.platform_scopes.update`
audit action taking priority over `functions.execute.update` when a request touches both (mirrors the
existing priority `UpdateFunction` already gave `execute` over a plain `functions.update`).

**Console**: `ApiKeysPage.tsx`'s inline scope checkbox grid was extracted verbatim into
[`components/ScopePicker.tsx`](../../console/src/components/ScopePicker.tsx) (`ScopeGrid` +
`ALL_API_KEY_SCOPES`) — no behavior or markup change there, just relocated so a second screen could
reuse it, per the prompt's explicit "no new picker component if the existing grid can be extracted"
instruction. `FunctionSettingsPage.tsx` gained a "Platform access" section between Triggers and
Schedule using that same `ScopeGrid`, auto-applying each toggle immediately via `update.mutate({
platformScopes })` — matching this same file's existing Execute-access/Triggers sections' pattern
(immediate apply, no separate Save button), not the staged-value-plus-Save pattern the plain text
fields above it use.

## Deviations & notes

- **No DB-level FK from `functions.platform_api_key_id` to `api_keys.id`.** This intentionally follows
  the same precedent `ActiveDeploymentId` → `FunctionDeployment` already sets in this exact file: a
  function-owned reference to another row it manages is kept as a plain app-managed `Guid?`, with
  consistency enforced in code (`ApplyPlatformScopesAsync`'s self-heal, `DeleteAsync`'s explicit
  revoke) rather than a cascade. A `SetNull`-on-delete FK was considered and rejected — it would only
  null the FK column itself, not the now-stale `PlatformScopes`/`PlatformApiKeySecretProtected` next to
  it, so the self-healing application logic was needed regardless; adding the FK on top would have been
  two half-solutions instead of one real one.
- **The function-owned key is not hidden from the project's own API keys page.** It appears there named
  `function:<key>`, revocable like any other key. Hiding it was considered (avoid operator confusion)
  and rejected as unnecessary scope: this codebase's existing posture is "the operator can see and
  manage everything in their project," and hiding it would have meant either a new "system key" flag on
  `ApiKey` or a filtered query — real surface for a cosmetic concern the self-healing logic already
  makes safe to ignore.
- **Platform scopes are not settable at function creation**, only from the Settings tab's `PATCH`, per
  the prompt's own framing of this as a Settings-tab feature. `CreateAsync` always starts a function at
  `PlatformScopes: []`, same deny-by-default posture `Execute` already has at creation.
- **No changes to `ApiKeyService`, `AppPrincipalFilter`, `RoleResolver`, or `RowEndpoints`.** This was
  the entire point of choosing option (a) — confirmed by the fact that the two new integration tests
  exercise real Tables reads through the injected key without touching any of those files.

## Known gaps (out of scope, noted for whoever picks them up)

- **Functions have no way to discover their own API's base URL.** `PRAXY_FUNCTION_API_KEY` (and the
  pre-existing `PRAXY_FUNCTION_JWT`) are only useful for a function that knows what host to send them
  to, and nothing injects one today — not a gap this task introduced, but this task is the first to
  make it concretely blocking (the natural "call back into Praxy" flow now has a credential but nowhere
  documented to send it). The two new tests route around this by having the test process itself — which
  does have a real path to the API — use the key the function echoed back, rather than having the
  function dial out. Worth its own follow-up.
- **A function's platform key can be granted any `ApiKeyScopes` its owning project could ever grant a
  regular key**, including scopes broader than what the operator granting it might personally hold via
  their own console session (there's no "you can't grant what you don't have" check, matching how
  `execute` roles already work — an operator's own console session isn't scope-limited in the first
  place, so this isn't a new asymmetry, just noting it wasn't specifically re-examined here).
- **`bypassRowPermissions` was deliberately left unreachable for a platform key** — `ApplyPlatformScopesAsync`
  always creates with `bypassRowPermissions: false`. A function's platform key still needs the target
  table to actually grant its resolved role (`"any"`) a permission, same as any other scoped key. This
  wasn't in the prompt's scope and adding it would be a second, separate escalation surface.

## Tests

- **Integration** (`tests/Praxy.Tests.Integration/FunctionScheduledCredentialsTests.cs`, new, real
  Docker like `FunctionTests.cs`): a schedule-triggered function with zero granted scopes completes
  normally with `PRAXY_FUNCTION_API_KEY` absent from its own environment, and a plain unauthenticated
  read against a table nothing was granted on returns zero rows through the ordinary row-permission
  filter (not a function-specific error); the same setup with `databases.read` granted before the first
  scheduled fire gets a real, working key back, used by the test itself as `X-Praxy-Key` to read a
  seeded row through the normal Tables endpoint; a third function, event-triggered off a row-create
  pattern with the same scope granted, gets identical treatment. Two genuine test-only races were found
  and fixed while getting these green under load (see below) — neither was a bug in the feature code
  itself.
- Full-repo `dotnet test`: **380/380 unit, 208/208 integration**, confirmed the same session these
  changes landed, including one full-suite run under heavy concurrent Docker load on this machine (a
  large pre-existing Appwrite comparison stack plus leftover containers from other sessions) where an
  *unrelated*, untouched test (`SiteTests.cs`) also intermittently times out — a pre-existing
  environmental characteristic of this dev machine under load, not something this change introduced.

### Test-only races found and fixed (not feature bugs)

- `FunctionBuildWorker` flips a deployment to `"ready"` and activates it on the function
  (`ActiveDeploymentId`) in two separate `ExecuteUpdateAsync` statements, not one transaction. Polling
  only the deployment's own status (the existing `WaitForDeploymentStatusAsync` pattern `FunctionTests.cs`
  already uses) leaves a narrow window where a trigger fired immediately after seeing `"ready"` still
  hits "No active deployment." The new test's `UploadAndWaitReadyAsync` additionally polls the
  function's own `activeDeploymentId` before proceeding.
- The event test originally seeded one row *before* creating the function under test. `FunctionEventDispatcher`
  matches a queued event against whichever functions are `Enabled` *at the time it gets around to
  dispatching that event* — not at the time the row was written — and `Enabled` defaults `true` from the
  moment a function is created, well before it has a deployment. Under load, that pre-existing row's
  event could end up dispatched to the new function after it was created but before it was deployed,
  producing a spurious, fast-failing `"No active deployment"` execution racing the real one. Fixed by
  creating no rows at all until after the function is confirmed ready and active.

## Commands

No new commands or config knobs. `dotnet ef migrations add <Name>` from `src/Praxy.Persistence` (as
already documented in `CLAUDE.md`) is how `20260831063726_FunctionPlatformCredentials` was generated;
nothing else changed about the dev/self-host workflow.

## Owner-test checklist

- Create a function, give it a schedule (e.g. `* * * * *`) and leave "Platform access" untouched (no
  scopes) — confirm scheduled runs complete with no way to reach Tables data.
- Grant it `databases.read` (and/or `databases.write`) under "Platform access", confirm the change
  persists (reload the page), then let it fire on schedule and confirm — via the function's own logs/
  response, or a quick test function that echoes `PRAXY_FUNCTION_API_KEY` — that the value it received
  is non-empty.
- Revoke the scope back to none and confirm the next run has nothing injected again.
- Repeat with an event trigger (row-create preset) instead of a schedule.
- On the project's own API keys page, confirm a key named `function:<key>` appears for any function
  with scopes granted, and that revoking it there doesn't break the function permanently — the next
  scope save on the function's Settings tab should transparently issue a replacement (self-heal,
  verified above by inspection/design, not by an owner-facing test scenario since it requires reaching
  into the API keys page specifically to break it first).

## Next

No further prompt is written — this was a standalone gap-closing task, not part of a numbered phase.
The one concrete, separately-scoped follow-up worth writing a kickoff for: giving functions a way to
discover their own API's base URL (a `PRAXY_ENDPOINT`-shaped env var, self-host-vs-dev-aware), which
would let a function *actually* dial back into Praxy using either `PRAXY_FUNCTION_JWT` or the new
`PRAXY_FUNCTION_API_KEY` for real, rather than the credential just sitting in its environment unused.
