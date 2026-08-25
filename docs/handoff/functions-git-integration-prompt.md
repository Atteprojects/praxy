# Session task — Functions git integration

## Why this exists

Sites Phase 4 (shipped 2026-08-24, report: `docs/handoff/sites-phase-4-report.md`) built push-to-deploy
for Sites and, on the owner's explicit request before that phase started, built the GitHub App/webhook/
token layer as its own resource-agnostic project, `Praxy.Vcs` — deliberately with zero references to
`Praxy.Sites`, specifically so a second consumer could plug in later without any retrofit. That phase's
own roadmap entry says as much: "a future Functions git-integration phase can be a small addition to
`Praxy.Vcs`'s consumers, not a rebuild of the GitHub App/token/signature layer." This session is that
phase.

Read `docs/handoff/sites-phase-4-prompt.md` and `docs/handoff/sites-phase-4-report.md` in full before
writing any code — this prompt assumes you have, and doesn't re-explain what's already settled there
(the two-step JWT → installation-token exchange, why GitHub's webhook signature scheme is a different
wire format from `Praxy.Webhooks.WebhookSignature`, the raw-body-before-model-binding requirement).
Everything in `src/Praxy.Vcs/` — `GitHubAppJwt`, `GitHubAppService`, `GitHubClient`, `IGitHubClient`,
`GitHubWebhookSignature`, `GitHubPushEvent`/`GitHubPushEventParser`, `IGitRepositoryCloner`/
`GitCliRepositoryCloner`, `VcsOptions`, the `vcs_installations` table — is finished, generic, and gets
consumed as-is, not modified for Functions-specific needs. If you find yourself wanting to change
`Praxy.Vcs` itself rather than just calling into it from `Praxy.Functions`, stop and reconsider — that's
very likely a sign the design intent from Phase 4 is being violated, not a genuine gap in it.

**This phase is smaller than Sites Phase 4** — no new shared project, no new instance-level console page
(`GitHubSettingsPage.tsx` is already instance-wide, not Sites-specific — see Scope #1), no preview-URL
serving infrastructure to build (Functions doesn't have per-deployment public URLs; a non-production-branch
push just produces a `ready`, non-activated deployment, exactly the state a manual upload can already be
in via the existing rollback mechanism — nothing new to serve). What's actually needed is: the same two
nullable columns on `functions` Sites Phase 4 added to `sites`, the same four columns on
`function_deployments` it added to `site_deployments`, a second dispatch query in the webhook handler, the
same `isGit`-branching in the build worker `SiteBuildWorker` already has, a directory-based build-context
sibling to `RuntimeTemplates.BuildContextAsync` (mirroring `SiteRuntimeTemplates.BuildContextFromDirectoryAsync`),
and the console-side mirrors of what Sites already shipped. Work on a new branch off `main`. Read
`CLAUDE.md` first.

## Non-goals — do not build these

- **No changes to `Praxy.Vcs` itself.** Every piece it owns (JWT signing, installation tokens, webhook
  signature verification, push-event parsing, the clone abstraction) is resource-agnostic already and
  needs zero Functions-specific additions. If a Functions need genuinely can't be met by the existing
  public surface, that's worth a second look at whether it's actually a Functions need or a
  misunderstanding of what's already there — this is the whole point of Phase 4 having built it this way.
- **No branch-pattern filters, no commit statuses/PR comments, no git providers besides GitHub** — same
  reasoning Sites Phase 4 already settled; this phase inherits those calls rather than re-litigating them.
- **No preview-URL-equivalent for Functions.** A function has no per-deployment public URL today (it's
  invoked via `POST /v1/functions/{id}/executions` against whichever deployment is `ActiveDeploymentId`),
  and this phase doesn't invent one. A git-sourced push to a non-production branch builds successfully,
  goes `ready`, and sits there reachable only via the console's existing explicit "Activate" action — the
  exact state a manual upload that hasn't been activated could already be in, except today's
  `FunctionBuildWorker` auto-activates every upload unconditionally, so this state is new in practice even
  though the mechanism (`FunctionsService.ActivateAsync`) already exists.
- **No changes to console tar upload.** It keeps auto-activating every successful build unconditionally,
  exactly as today — git integration is an additional deployment source alongside it, not a replacement,
  same posture Sites Phase 4 took.
- **No multi-repository-per-function or multi-function-per-repository complexity.** One function connects
  to at most one repository at a time, matching Sites' own constraint.
- **No unification of `SitesService.HandleGitPushAsync`/`FunctionsService`'s new equivalent into a shared
  abstraction.** Sites Phase 4's own webhook handler comment says so explicitly: "a future Functions git
  integration adds its own parallel query here rather than a shared interface invented on spec." Add the
  second query; don't refactor the first one into something more abstract to accommodate it.

## Scope

1. **`GitHubSettingsPage.tsx` needs no new page** — it's already instance-wide GitHub App install/status,
   not Sites-scoped in its data (only in its current copy: "any project's sites can connect a repository to
   push-to-deploy" — update that sentence to mention functions too, and check `SiteSettingsPage.tsx`'s own
   "Git repository" card copy for the same Sites-only wording that might need a matching Functions-side
   tweak once both exist).
2. **`functions` gains `repository_full_name`, `production_branch`** (both nullable, same shape as
   `sites`). **`function_deployments` gains `source` (`upload`|`git`), `commit_sha`, `commit_message`,
   `branch`** (same shape as `site_deployments`). New EF migration from `src/Praxy.Persistence`.
3. **`FunctionsService` gains the Sites-mirroring surface**: `GitConnection`, `ConnectRepositoryAsync`
   (validate `owner/repo` shape, confirm the instance's GitHub App installation covers it via
   `GitHubAppService.EnsureRepositoryAccessibleAsync`, confirm the chosen production branch is real via
   `ListBranchesForRepositoryAsync`), `DisconnectRepositoryAsync`, `CreateGitDeploymentAsync` (no
   `FunctionDeploymentSource` row — nothing's been uploaded, `FunctionBuildWorker` clones fresh), and
   `HandleGitPushAsync(GitHubPushEvent evt, ...)` — a direct `db.Functions.Where(f =>
   f.RepositoryFullName == evt.RepositoryFullName)` query, matching `SitesService.HandleGitPushAsync`'s
   own shape exactly (see Non-goals above for why this stays a second parallel query, not a shared one).
4. **`POST /v1/vcs/github/webhook` (`VcsEndpoints.Webhook`, in `Praxy.Api`) calls both.** Today it only
   calls `sites.HandleGitPushAsync(evt, ct)` after signature verification and push-event parsing; add
   `functions.HandleGitPushAsync(evt, ct)` alongside it, unconditionally — a single push to a monorepo that
   both a site and a function have separately connected (different production branches even) should
   trigger both, independently. Neither call should be able to fail the other; if you want per-consumer
   error isolation beyond what already exists, that's a judgment call, but don't let a bug in one silently
   swallow the other's real errors either.
5. **`RuntimeTemplates` gains a directory-based `BuildContextAsync` sibling** (name it consistently with
   `SiteRuntimeTemplates.BuildContextFromDirectoryAsync` — same rationale: a shallow clone lands on real
   disk, not a `MemoryStream`, so the build-context assembly needs a directory-reading variant). The actual
   Dockerfile-generation logic (`RuntimeTemplates`'s per-runtime template selection) doesn't need to change
   at all, same as Sites Phase 4 found for `SiteRuntimeTemplates`.
6. **`FunctionBuildWorker.BuildAsync` gains the same `isGit` branch `SiteBuildWorker.BuildAsync` has**:
   claim query selects the new columns too; a git-sourced deployment needs `fn.RepositoryFullName` and
   `deployment.CommitSha` instead of a `FunctionDeploymentSource` row (missing either fails the build with
   a clear error, mirroring Sites' exact wording pattern); clone via a short-lived installation token
   (`GitHubAppService.GetInstallationTokenForRepositoryAsync` + `IGitRepositoryCloner.CloneAsync`) before
   building. **The auto-activate gate changes shape**: today every successful build activates
   unconditionally; after this phase, an **upload-sourced** build still auto-activates unconditionally
   (unchanged), but a **git-sourced** build only auto-activates when `deployment.Branch ==
   fn.ProductionBranch` — otherwise it finishes `ready` and stops, exactly mirroring
   `SiteBuildWorker`'s own `if (isGit && deployment.Branch != site.ProductionBranch) return;` gate.
7. **`FunctionEndpoints.cs` gains the git routes** mirroring `SiteEndpoints.cs`'s exactly: `GET
   /{functionId}/git/branches?repository=`, `POST /{functionId}/git` (connect), `DELETE /{functionId}/git`
   (disconnect) — same request/response shapes as `ConnectSiteGitRequest`/`SiteGitBranchesResponse`, a
   Functions-named equivalent. Add `FunctionGitRepositoryInvalid` to `ErrorTypes.cs` (the `VcsGithub*`/
   `VcsWebhook*` error types are already resource-agnostic and get reused as-is, no new ones needed there).
8. **Console**: a "Git repository" card on `FunctionSettingsPage.tsx`, structurally identical to
   `SiteSettingsPage.tsx`'s own (connect/pick-branch/show-connected/disconnect). `FunctionDeploymentsPage.tsx`'s
   deployment list gains `source`/`commit_sha`/`branch` columns for git-sourced rows, mirroring
   `SiteDeploymentsPage.tsx`'s own display of the same fields.

## Landmines — read before writing code

- **Don't let `Praxy.Functions` end up needing anything `Praxy.Vcs` doesn't already expose.** Walk through
  `Praxy.Vcs`'s actual public types (`GitHubAppService`, `IGitHubClient`, `IGitRepositoryCloner`,
  `GitHubPushEvent`) before writing `FunctionsService`'s new methods — everything Sites Phase 4 needed from
  this layer is resource-agnostic by construction, so Functions needs the exact same surface, not a
  superset.
- **The auto-activate default is changing for the first time since Functions shipped.** Every function
  build today activates unconditionally the moment it succeeds — there has never been a "successful build,
  intentionally not yet active" state reachable through normal use before this phase. Double-check
  `FunctionExecutionService`'s invoke path (and anything else that assumes "a function with any successful
  deployment has an active one") doesn't implicitly assume every `ready` deployment is also the active one
  — it almost certainly already handles `ActiveDeploymentId` being null correctly (a freshly created
  function with zero deployments is already exactly this state), but verify rather than assume, since this
  phase is the first time a `ready`-but-never-activated deployment becomes a routine, not edge-case, state.
- **A git clone needs real disk, same requirement `SiteRuntimeTemplates`/`SiteBuildWorker` already solved**
  — reuse that same temp-directory-lifecycle discipline (cleaned up on both success and failure/cancellation)
  rather than inventing a second pattern for it.
- **Self-hosted instances still need to be internet-reachable for the webhook to arrive at all** — same
  constraint Sites Phase 4 hit, unchanged here since it's the same webhook endpoint. The real owner-test
  targets `praxycore.dev`, not local dev.
- **One GitHub App installation now serves two resource types.** A repository accessible to the instance's
  installation can be connected by a site *and* a function simultaneously (or by neither) — there's no
  reason to add a check preventing that, and Scope #4 already covers a webhook correctly notifying both.

## Tests

`tests/Praxy.Tests.Integration/` — a new `FunctionGitDeploymentTests.cs`, structurally mirroring
`SiteGitDeploymentTests.cs` exactly: a signed `push` webhook fixture to the production branch creates and
auto-activates a function deployment; the same to a non-production branch creates a `ready`,
non-activated deployment; an unsigned/badly-signed payload is rejected before any deployment is created;
a payload for a repository no connected function references is a no-op. Reuse `SiteGitDeploymentTests`'
own `FakeGitHubClient`/`FakeGitRepositoryCloner` pattern (check whether they're already generic enough to
share directly, given `IGitHubClient`/`IGitRepositoryCloner` are `Praxy.Vcs` interfaces with no Sites
dependency — if so, don't duplicate them into a second fake). Remember the container-cleanup lesson from
`docs/handoff/` history: a preview-equivalent state here is just a `ready`, non-activated deployment with
no container at all (Functions has no long-lived container, unlike Sites), so the leaked-container class
of bug `SiteContainerRegistry`-based tests hit doesn't apply the same way — but double check
`FunctionGitDeploymentTests`' own teardown doesn't need anything beyond what `FunctionTests.cs`'s existing
cleanup already does.

## Done means

- `dotnet test` green (unit + integration, real Docker daemon).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run against `praxycore.dev`**: connect a real function to a real repository
  (reusing the GitHub App installation Sites Phase 4's owner-test already created — no second App needed),
  push to its production branch and watch it build and auto-activate, push to a different branch and
  confirm the deployment appears `ready` but doesn't touch the active one, confirm an unrelated
  repository's push is correctly ignored, and — the one genuinely new cross-resource check — connect the
  *same* repository to both a site and a function and confirm a single push correctly triggers both
  independently.
- `git status` clean, conventional commits, on a new branch off `main`.
- `docs/self-host.md`'s existing "Git integration" section gets updated to note it now covers both Sites
  and Functions (same GitHub App, same setup steps) — this is a documentation update to an existing
  section, not a new one.
- Write `docs/handoff/functions-git-integration-report.md`.

## Deploying (only if the owner asks)

Same posture as Sites Phase 4: don't deploy this to `praxycore.dev` without being asked, since it changes
what a real, already-configured webhook endpoint on the owner's live domain does.
