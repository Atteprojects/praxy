# Functions git integration — report

**Status: complete.** Every item in `docs/handoff/functions-git-integration-prompt.md`'s scope shipped.
`Praxy.Vcs` itself is untouched — zero changes to `GitHubAppJwt`, `GitHubAppService`, `GitHubClient`,
`GitHubWebhookSignature`, `GitHubPushEvent`/`GitHubPushEventParser`, `IGitRepositoryCloner`/
`GitCliRepositoryCloner`, `VcsOptions`, or the `vcs_installations` table — confirming Sites Phase 4's
own bet that this layer was already resource-agnostic. Full-repo `dotnet test` green —
**379/379 unit, 202/202 integration** (real Postgres via Testcontainers, real Docker
daemon throughout). Console `tsc -b && vite build` clean.

## What shipped

**`Praxy.Functions.csproj`** gained a `ProjectReference` to `Praxy.Vcs` — the only project-graph change;
`Praxy.Vcs` still has no reference back to `Praxy.Sites` or `Praxy.Functions` in either direction.

**Database** (migration `20260825173641_FunctionsGitIntegration`,
[`Entities/Functions.cs`](../../src/Praxy.Persistence/Entities/Functions.cs)): `functions` gains
`repository_full_name`/`production_branch` (both nullable, identical shape to `sites`'s own);
`function_deployments` gains `source` (`upload`|`git`, check-constrained, existing rows backfilled
`upload` — the same `""`→`"upload"` hand-fix Sites Phase 4's own migration needed, caught the same way
before ever running it), `commit_sha`, `commit_message`, `branch`.

**`Praxy.Functions`**
([`FunctionsService.cs`](../../src/Praxy.Functions/FunctionsService.cs), now `partial` for a
`[GeneratedRegex]` repository pattern and depending on `Praxy.Vcs`'s `GitHubAppService`):
`ConnectRepositoryAsync`/`DisconnectRepositoryAsync`/`CreateGitDeploymentAsync`/`HandleGitPushAsync` —
method-for-method mirrors of `SitesService`'s own git section, including the same validate-then-persist
shape and the same "a push for an unmatched repository is a no-op, not an error" rule.
`FunctionGitRepositoryInvalid` is the one new error type (`Praxy.Core.Errors.ErrorTypes`) — the
`VcsGithub*`/`VcsWebhook*` types are reused as-is from `Praxy.Vcs`, unmodified.

[`FunctionBuildWorker.cs`](../../src/Praxy.Functions/FunctionBuildWorker.cs): branches on
`deployment.Source` the same way `SiteBuildWorker` does — `"git"` mints an installation token, clones
the exact commit (`IGitRepositoryCloner`), and builds the Docker context directly from that directory
(`RuntimeTemplates.BuildContextFromDirectoryAsync`, a new sibling to the tar-based `BuildContextAsync`
sharing a factored-out `WriteGeneratedFilesAsync` helper for the Dockerfile+wrapper emission — the
per-runtime Dockerfile/wrapper generation itself is untouched). **The one behavior change to existing
logic**: auto-activation, previously unconditional on every successful build, now only fires for an
upload-sourced deployment or a git-sourced one whose branch matches the function's `ProductionBranch`.
Unlike Sites (which gates a whole `ActivateAsync` container-start call), Functions' auto-activate was
already two inline `ExecuteUpdateAsync` calls with no container to start — the gate here just moves
`ActivatedAt` out of the unconditional "mark ready" update into a guarded second block, so a
non-production git push finishes `ready` with `activatedAt` staying null, the exact state
`FunctionDeploymentsPage`'s existing `canActivate` check already needed.

**`Praxy.Api`**: [`FunctionEndpoints.cs`](../../src/Praxy.Api/Endpoints/FunctionEndpoints.cs) gained
`GET .../git/branches?repository=owner/repo`, `POST .../git` (connect), `DELETE .../git` (disconnect) on
the console admin group only (no data-plane/API-key equivalent — Sites never exposed git management
there either); `FunctionResponse` gained `repositoryFullName`/`productionBranch`,
`FunctionDeploymentResponse` gained `source`/`commitSha`/`commitMessage`/`branch`.
[`VcsEndpoints.cs`](../../src/Praxy.Api/Endpoints/VcsEndpoints.cs)'s `Webhook` handler now calls
`functions.HandleGitPushAsync(evt, ct)` alongside the existing `sites.HandleGitPushAsync(evt, ct)` —
both unconditional, sequential, neither wrapped in a swallowing try/catch (matching the existing,
already-unguarded posture the Sites call had on its own before this phase) — so a single push to a
repository connected to both a site and a function deploys both, independently.

**Console**: new "Git repository" card on `FunctionSettingsPage.tsx`, structurally identical to
`SiteSettingsPage.tsx`'s own (same three states, same `ConfirmButton` disconnect), placed as the last
functional section before "Danger zone". `FunctionDeploymentsPage.tsx` gained a `Source` column
(`branch @ shortSha` for git rows) and a matching git-info line in the deployment detail sheet.
`GitHubSettingsPage.tsx`'s copy updated to mention functions alongside sites (it was already
instance-wide in its data, only its copy needed the tweak — no new page, per the kickoff prompt's own
Scope #1).

**`deploy/Dockerfile`**: unchanged — `git` was already installed in Sites Phase 4's runtime image.

## Deviations & notes

- **No new DI registrations in `Program.cs`.** `GitHubAppService` and `IGitRepositoryCloner` were
  already registered from Sites Phase 4; ASP.NET Core DI resolves by type, not registration order, so
  `FunctionBuildWorker`/`FunctionsService` pick them up with zero additional wiring.
- **Webhook error isolation stays exactly as loose as it already was for Sites alone** — no new
  try/catch was added around either `HandleGitPushAsync` call. The kickoff prompt left this as a
  judgment call ("don't let a bug in one silently swallow the other's real errors"); the existing Sites
  call already had no error isolation (an exception there just fails the whole webhook request today),
  so adding the Functions call the same unguarded way satisfies "don't swallow" without inventing new
  machinery only this phase would need.
- **`FunctionDeploymentSources`' unconditional delete needed no change.** `FunctionBuildWorker` already
  deletes that row after every build regardless of source (a harmless no-op for a git-sourced row that
  never had one) — the exact same discipline `SiteBuildWorker` follows for its own sources table, so no
  new code was needed there, only confirmed by inspection.
- **`FunctionExecutionService.RunAsync`'s "no active deployment" path needed no change** — confirmed by
  reading it directly (`fn.ActiveDeploymentId is not { } deploymentId` already fails cleanly with "No
  active deployment.", a state a freshly created function could already reach with zero deployments).
  The kickoff prompt's own landmine flagged this as worth double-checking rather than assuming, since a
  `ready`-but-never-activated deployment becomes a routine state for the first time after this phase;
  the existing code already handled it correctly.

## Known gaps (deliberate, inherited from Sites Phase 4's own non-goals)

- No commit statuses or PR comments posted back to GitHub.
- No branch-pattern filters — one fixed production branch per function, everything else builds but
  doesn't activate.
- No build-command auto-detection — git-sourced and upload-sourced deployments go through the identical
  generated Dockerfile/wrapper per runtime.
- No git providers besides GitHub.
- Console tar upload is unchanged — still auto-activates every successful build unconditionally.
- One repository per function, at most.
- No preview-URL equivalent for a non-production git push — Functions has no per-deployment public URL
  at all (invoked only via `ActiveDeploymentId`), so an unactivated deployment is reachable only through
  the console's explicit Activate action, same as an unactivated upload already could be.
- No audit-log coverage for webhook-triggered deployments or instance-level Vcs actions — inherited gap,
  unchanged from Sites Phase 4.

## Tests

Same posture as Sites Phase 4: **everything in this phase's test suite runs against fakes — no real
GitHub App, installation, or network call happens anywhere in `dotnet test`.**

- **Integration** (`tests/Praxy.Tests.Integration/FunctionGitDeploymentTests.cs`, new): reuses
  `FakeGitHubClient` directly from `SiteGitDeploymentTests.cs` (fully generic — `Praxy.Vcs` interfaces
  only, no Sites types), and adds a new `FakeFunctionGitRepositoryCloner` writing a bare Node `index.js`
  fixture instead of the Sites fake's Next.js-shaped one (the two fakes can't share content, only the
  `IGitHubClient` fake is generic enough to reuse). Covers: a signed push to the production branch
  creates a deployment and the function actually becomes active on it (`WaitForFunctionActiveAsync`);
  a push to a non-production branch builds a `ready` deployment with `activatedAt` staying null and the
  function's active deployment untouched; an unsigned/wrong-secret payload is rejected with `401` before
  any deployment is created; a push for a repository no function references is a `204` no-op; and the one
  genuinely new cross-resource case — connecting the same repository to both a site and a function and
  confirming one push deploys both independently, exercising `VcsEndpoints.Webhook`'s new second dispatch
  call.
- No `DisposeAsync` override was needed, confirmed against `FunctionTests.cs` (no override there either)
  and the kickoff prompt's own note: Functions has no long-lived build-time container, so the
  leaked-preview-container class of cleanup `SiteGitDeploymentTests` needs doesn't apply here.
- No new unit tests — `Praxy.Vcs` itself is unchanged (its own Sites Phase 4 unit tests already cover
  the shared JWT/signature/parser layer); `FunctionGitRepositoryInvalid` is covered by the existing
  `ErrorTypesTests` reflection-based ratchet (`Registry_covers_every_declared_constant`), same as every
  prior error type addition.
- Full-repo `dotnet test`: **379/379 unit, 202/202 integration**, confirmed the same
  session these changes landed.

## Commands

No new config — this phase reuses `Praxy:Vcs:*` entirely as-is (see Sites Phase 4's own Commands section
and `docs/self-host.md`'s "Git integration" section, now updated to note it covers both resource types).

Everything from `docs/handoff/sites-phase-4-report.md`'s Commands section is unchanged.

## Owner-test checklist

**Not verified by me** — per the kickoff prompt's own instruction not to deploy to `praxycore.dev`
without being asked, and its "Deploying" section's identical posture to Sites Phase 4. The owner's
checklist, reusing the GitHub App installation Sites Phase 4's own owner-test already created (no second
App needed):

- Connect a real function to a real repository from its Settings page.
- Push to its production branch — watch the deployment build and auto-activate.
- Push to a different branch — confirm the deployment appears `ready` but doesn't touch the active one.
- Confirm an unrelated repository's push is correctly ignored (no deployment created).
- **The one genuinely new cross-resource check**: connect the same repository to both a site and a
  function, push once, confirm both deploy independently — proven locally against fakes in
  `FunctionGitDeploymentTests`, worth a real confirmation against the live GitHub webhook too.

## Next

No further prompt is written — this kickoff didn't imply a subsequent phase, and `Praxy.Vcs` now has
its two intended consumers wired up exactly as Sites Phase 4 designed it to support, with no changes to
that shared layer needed by either. Framework presets beyond Next.js (Sites) remain the owner's own
deferred call from 2026-08-22.
