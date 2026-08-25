# Sites Phase 4 — report

**Status: complete.** Every item in `docs/handoff/sites-phase-4-prompt.md`'s scope shipped, including the
shared-infrastructure requirement (`Praxy.Vcs`) called out in that prompt's revision. Full-repo
`dotnet test` green — **379/379 unit, 197/197 integration** (real Postgres via Testcontainers, real
Docker daemon throughout). Console `tsc -b && vite build` clean. Manually click-tested against a
live local dev instance (see Owner-test checklist) — including deliberately exercising the
"GitHub App not configured yet" state, which surfaced and fixed a real bug before it ever reached the
owner.

## What shipped

**New project `src/Praxy.Vcs/`** — references `Praxy.Core` and `Praxy.Persistence` only (see Deviations
for why `Praxy.Auth` was dropped from the plan's own ref list), no reference to `Praxy.Sites` or
`Praxy.Functions` in either direction. Entirely in terms of GitHub's own concepts — zero `Site`/
`SiteDeployment` types anywhere in it:

- [`GitHubAppJwt.cs`](../../src/Praxy.Vcs/GitHubAppJwt.cs) — RS256 App-identity JWT, hand-rolled on the
  BCL (`RSA` + `System.Text.Json`), pure/deterministic given an injectable `TimeProvider`. Guards on an
  unconfigured App (`AppId`/`PrivateKey` empty — the default state for every fresh instance) and throws
  a clean `PraxyException(422, vcs_github_not_configured, ...)` instead of letting
  `RSA.ImportFromPem("")` surface a raw `ArgumentException` as an unhandled 500 (see Deviations — found
  live, not anticipated in planning).
- [`GitHubWebhookSignature.cs`](../../src/Praxy.Vcs/GitHubWebhookSignature.cs) — GitHub's
  `sha256=<hex HMACSHA256(secret, rawBody)>` scheme, deliberately not `Praxy.Webhooks.WebhookSignature`
  (a different, Stripe-style scheme for Praxy's own outbound deliveries). Same
  `CryptographicOperations.FixedTimeEquals` discipline.
- [`GitHubPushEvent.cs`](../../src/Praxy.Vcs/GitHubPushEvent.cs) — the parsed payload
  (`RepositoryFullName, Ref, Branch, CommitSha, CommitMessage, InstallationId`) plus
  `GitHubPushEventParser.Parse`, throwing `GitHubPushPayloadException` on anything not shaped like a
  real push event.
- [`IGitHubClient.cs`](../../src/Praxy.Vcs/IGitHubClient.cs) / [`GitHubClient.cs`](../../src/Praxy.Vcs/GitHubClient.cs) —
  the App-JWT- and installation-token-authenticated GitHub REST calls (`GetApp`, `GetInstallation`,
  `GetRepositoryInstallation`, `CreateInstallationToken`, `ListBranches`). An interface specifically so
  tests substitute a fake instead of hitting real GitHub.
- [`IGitRepositoryCloner.cs`](../../src/Praxy.Vcs/IGitRepositoryCloner.cs) / [`GitCliRepositoryCloner.cs`](../../src/Praxy.Vcs/GitCliRepositoryCloner.cs) —
  shells out to the system `git` CLI (`init` → `remote add` → `fetch --depth 1 origin <sha>` →
  `checkout FETCH_HEAD`, pinning the exact pushed commit rather than a branch's tip at fetch time).
  `GitCheckout` is `IAsyncDisposable`, deleting its temp directory on dispose whether the build that
  follows succeeds or fails.
- [`GitHubAppService.cs`](../../src/Praxy.Vcs/GitHubAppService.cs) — the orchestration facade: install
  URL computation, installation-callback upsert, `vcs_installations` listing, repository
  accessibility/branch-listing/token-minting. Never caches which installation covers which repository —
  every check is live against GitHub, so a revoked/reconfigured installation is caught immediately.
- [`VcsOptions.cs`](../../src/Praxy.Vcs/VcsOptions.cs) — `Praxy:Vcs:GitHub:*` config, startup-only (not
  console-rotatable — the prompt left this as an implementation call; see Deviations). Accepts the
  private key as raw PEM or base64 (self-host docs recommend base64 — real newlines don't survive a
  single-line `.env` value).

**Database** (migration `20260825044835_SitesGitIntegration`,
[`Entities/Vcs.cs`](../../src/Praxy.Persistence/Entities/Vcs.cs) /
[`Entities/Sites.cs`](../../src/Praxy.Persistence/Entities/Sites.cs)): new `vcs_installations` table
(`id, installation_id, account_login, account_type, created_at` — **no `project_id`**, the first table
in this schema without one, per the prompt's own explicit call); `sites` gains
`repository_full_name`/`production_branch` (both nullable); `site_deployments` gains `source`
(`upload`|`git`, check-constrained, existing rows backfilled `upload`), `commit_sha`, `commit_message`,
`branch`.

**`Praxy.Sites`** ([`SitesService.cs`](../../src/Praxy.Sites/SitesService.cs), now depending on
`Praxy.Vcs`): `ConnectRepositoryAsync` (validates `owner/repo` shape, confirms the App installation
actually covers the repo, confirms the chosen branch is real — all three fail loudly, mirroring
`AddDomainAsync`'s validate-then-persist shape from Phase 3), `DisconnectRepositoryAsync`,
`CreateGitDeploymentAsync` (no `SiteDeploymentSource` row — nothing uploaded), and `HandleGitPushAsync`
(the webhook's dispatch target: a direct `sites` query matching `RepositoryFullName`, not an abstract
"connected resource" interface, per the prompt's own restraint — a push for an unmatched repository is
a no-op, not an error).

[`SiteBuildWorker.cs`](../../src/Praxy.Sites/SiteBuildWorker.cs): branches on `deployment.Source` —
`"git"` mints an installation token, clones the exact commit into a temp directory
(`IGitRepositoryCloner`), and builds the Docker context directly from that directory
(`SiteRuntimeTemplates.BuildContextFromDirectoryAsync`, a new sibling to the tar-based
`BuildContextAsync` — same generated Dockerfile, same macOS-`bsdtar`-PAX-attribute workaround, walks a
real directory instead of re-emitting a `TarReader`'s entries). **The one behavior change to existing
logic**: auto-activation, previously unconditional on every successful build, now only fires for an
upload-sourced deployment or a git-sourced one whose branch matches the site's `ProductionBranch` — a
non-production push builds, goes `ready`, and stops there, already reachable at its Phase 2 preview URL
with zero other code changes needed for that half.

**`Praxy.Api`**: new [`VcsEndpoints.cs`](../../src/Praxy.Api/Endpoints/VcsEndpoints.cs) —
`GET /v1/vcs/github/callback` (public, GitHub's installation-flow redirect target),
`POST /v1/vcs/github/webhook` (public, raw body read and HMAC-verified **before** any JSON parsing —
the landmine the prompt called out explicitly), `GET /v1/console/vcs/github/installations` and
`/install-url` (operator-authed, genuinely instance-wide — no `{projectId}` in either path).
[`SiteEndpoints.cs`](../../src/Praxy.Api/Endpoints/SiteEndpoints.cs) gained
`GET .../git/branches?repository=owner/repo`, `POST .../git` (connect), `DELETE .../git` (disconnect);
`SiteResponse` gained `repositoryFullName`/`productionBranch`, `SiteDeploymentResponse` gained
`source`/`commitSha`/`commitMessage`/`branch` (folded into the existing site/deployment DTOs rather than
new dedicated GET endpoints — a simplification, see Deviations).

**Console**: new "Git repository" card on `SiteSettingsPage.tsx`, structurally mirroring the "Custom
domains" card (empty/not-connected/connected states, `ConfirmButton` disconnect) — gated on whether any
`vcs_installations` row exists, with a link to the new instance-wide settings page otherwise. New
`GitHubSettingsPage.tsx` at `/project/$projectId/github` (nested under a project's nav for shell
consistency even though its data is instance-wide — see Deviations), a "Manage" group nav entry with a
new `GithubIcon`. `SiteDeploymentsPage.tsx` gained a `Source` column (`branch @ shortSha` for git rows)
and a matching summary line in the deployment detail sheet.

**`deploy/Dockerfile`**: runtime stage now installs `git` via `apt-get` — the base
`mcr.microsoft.com/dotnet/aspnet:10.0` image doesn't carry it, and `GitCliRepositoryCloner` needs it on
`PATH`.

## Deviations & notes

- **`Praxy.Vcs` does not reference `Praxy.Auth`**, despite the prompt's own scope item 1 suggesting
  "the same encrypted-at-rest treatment `InstanceKey` already gives other secrets... default to
  startup-only unless you find a concrete reason not to." Went with startup-only, plain-config-bound
  `VcsOptions` (the same shape every other feature's `*Options` record already uses —
  `SitesOptions`/`FunctionsOptions`/`WebhookOptions`), which needs no `InstanceKey` at all — no concrete
  reason turned up to store these five values in the DB instead of config, so `Praxy.Auth` was simply
  never needed. Smaller dependency footprint than the plan sketched, not a functional gap.
- **Found and fixed a real bug live, not anticipated during planning**: calling
  `GET /console/vcs/github/install-url` before the instance's own GitHub App is configured (the default
  state for every fresh install) originally threw a raw `System.ArgumentException` from
  `RSA.ImportFromPem("")`, surfacing as an unhandled 500 with no useful message. Added an explicit guard
  in `GitHubAppJwt.Create` (new error type `vcs_github_not_configured`, 422) and a matching console state
  (`GitHubSettingsPage.tsx` shows "This instance's GitHub App isn't set up yet — see
  docs/self-host.md's Git integration section..." instead of a stuck spinner). Covered by a new
  `GitHubAppJwtTests` theory (4 cases: blank/whitespace `AppId`/`PrivateKey`).
- **`useGithubInstallUrl` sets `retry: false`.** While click-testing the fix above against a live local
  instance, the "Connect GitHub" button got stuck permanently spinning after the query settled to a 422
  — confirmed via direct DOM/network inspection that exactly one request fired and completed, yet
  `isPending` never flipped. Root cause not fully isolated (possibly a TanStack Query v5 + this dev
  environment's retry-timing interaction), but the fix is correct regardless of cause: a `422
  vcs_github_not_configured` is not transient, retrying it can't succeed until the owner reconfigures
  and restarts, so disabling retry for this one query is the right design on its own merits, not just a
  workaround.
- **`SiteGitRepositoryInaccessible` (as sketched in the kickoff prompt's own error-type suggestion)
  became `VcsGithubRepositoryInaccessible` instead.** `GitHubAppService.EnsureRepositoryAccessibleAsync`
  is meant to be resource-agnostic (a future Functions consumer calls the same method) — throwing a
  `Site*`-prefixed error type from inside `Praxy.Vcs` would leak Sites-specific naming into a
  supposedly-shared layer. `SiteGitRepositoryInvalid` (Sites' own format/branch-membership validation,
  thrown from `SitesService` itself before it ever calls into `Praxy.Vcs`) keeps the `Site*` prefix
  correctly, since that check *is* Sites-specific.
- **No dedicated `GET .../git` connection-state endpoint.** The plan sketched one; folding
  `repositoryFullName`/`productionBranch` directly into the existing `SiteResponse` (the site's own
  `GET`/list endpoints already return it) was simpler and needed no new route.
- **Webhook success responses are `204`, not `200`.** `OpenApiDocumentTests`'s existing ratchet
  (`Every_operation_documents_a_response_body_or_says_it_has_none`) doesn't accept a bare `200` with no
  content schema as documented — `204` was both the honest shape (there's nothing to say back to
  GitHub) and the one that satisfies the test without inventing a response body no one needs.
- **Migration's `source` column backfill fixed from `""` to `"upload"`.** `dotnet ef migrations add`
  generated an empty-string default for the new NOT-NULL column on existing rows, which would have
  violated the same migration's own `ck_site_deployments_source` check constraint on any table with
  pre-existing deployments — caught by inspection before ever running it, hand-edited to `"upload"`
  (the value every existing row's real source actually is).
- **No audit-log entries for webhook-triggered deployments, or for any instance-level `Praxy.Vcs`
  action.** The existing `AuditAsync` helper needs an authenticated operator (`RequireOperatorFilter.
  Current(http)`) for its actor string, which doesn't exist on the public webhook/callback endpoints;
  and `AuditLogEntry` is project-scoped, which doesn't fit `vcs_installations`' deliberate lack of a
  `project_id`. Not explicitly required by the prompt's scope; flagged as a known gap below rather than
  inventing a new audit convention for this phase alone.

## Known gaps (deliberate, per the prompt's own non-goals)

- No commit statuses or PR comments posted back to GitHub.
- No branch-pattern filters — one fixed production branch per site, everything else is a preview.
- No build-command auto-detection — git-sourced and upload-sourced deployments go through the identical
  generated Dockerfile.
- No git providers besides GitHub.
- Console tar upload and the starter-template deploy are unchanged.
- No access control on preview URLs beyond Phase 2's existing URL-is-the-secret model.
- One repository per site, at most.
- **Functions does not consume `Praxy.Vcs` yet** — the shared layer exists and is resource-agnostic
  (verified: zero `Site`/`SiteDeployment` references anywhere in `src/Praxy.Vcs/`), but wiring it up to
  Functions is explicitly its own future phase, not started here.
- No audit-log coverage for webhook-triggered deployments or instance-level Vcs actions (see Deviations).

## Tests

Per the prompt's own request to document plainly what's proven against fixtures versus a real GitHub App
installation: **everything in this phase's test suite runs against fakes — no real GitHub App,
installation, or network call happens anywhere in `dotnet test`.**

- **Unit** (`tests/Praxy.Tests.Unit/`): `GitHubWebhookSignatureTests` (valid/wrong-secret/tampered-body/
  malformed-header, mirroring `WebhookSignatureTests`'s own coverage), `GitHubAppJwtTests` (RS256
  structure, issuer, exact 10-minute `iat`/`exp` window, signature verifiable by the matching public
  key, base64-encoded-PEM acceptance, the not-configured guard), `GitHubPushEventParserTests` (a
  realistic push payload, branch derivation from a multi-segment ref, missing-optional-field tolerance,
  malformed/incomplete-payload rejection).
- **Integration** (`tests/Praxy.Tests.Integration/SiteGitDeploymentTests.cs`): `IGitHubClient` and
  `IGitRepositoryCloner` are DI-replaced with fakes (`FakeGitHubClient`, `FakeGitRepositoryCloner` — the
  latter materializes the same minimal fake-Next.js fixture `SiteCustomDomainTests`/`SitesAskTlsTests`
  already use, as real files instead of a tar, so the real Docker build pipeline still runs genuinely).
  Covers: a fixture-signed push to the production branch creates a deployment and the site actually
  becomes active on it (`WaitForSiteActiveAsync`, polling both deployment status and site state — a
  "ready" status and true activation are two separate sequential steps, and polling only the former can
  observe the gap between them, which an earlier draft of this test caught doing); a push to a
  non-production branch builds a deployment reachable at its own preview URL while the site's active
  deployment stays untouched; an unsigned or wrong-secret payload is rejected with `401` before any
  deployment row is created; a push for a repository no site references is a `204` no-op, not an error.
- Full-repo `dotnet test`: **379/379 unit, 197/197 integration**, confirmed the same session these
  changes landed.

## Commands

New config, all under `Praxy:Vcs:*` (see `docs/self-host.md`'s "Git integration" section for the full
GitHub App creation walkthrough — permissions, callback/webhook URLs, and how to supply the private
key):

- `Praxy:Vcs:GitHub:AppId` / `ClientId` / `ClientSecret` / `PrivateKey` / `WebhookSecret` — unset by
  default (the feature is off, with a clean typed error, until configured).
- `Praxy:Vcs:CloneTimeoutSeconds` (default 60) — ceiling on a single `git` subprocess call.
- `Praxy:Vcs:MaxWebhookBodyBytes` (default 25,000,000) — caps how much of an inbound webhook body the
  unauthenticated endpoint buffers before verifying its signature.

New runtime requirement: `deploy/Dockerfile`'s image now installs `git`; running `api` bare
(`dotnet run`, not the Docker image) needs `git` on `PATH` yourself for git-sourced builds to work (the
rest of the phase — connecting a repository, receiving webhooks, dispatch — works without it; only the
actual clone step needs it).

Everything from `docs/handoff/sites-phase-3-report.md`'s Commands section is unchanged.

## Owner-test checklist

**Verified this session, against a live local dev instance** (`dotnet run` + `npm run dev`, real local
Postgres, no Docker build attempted since no real GitHub App exists here):

- `GET /console/vcs/github/installations` and the "GitHub" console page render correctly with zero
  installations connected.
- `GET /console/vcs/github/install-url` against an unconfigured instance returns a clean `422
  vcs_github_not_configured` (not a raw 500), and the console shows a clear explanatory message instead
  of a stuck-loading button — this is the state every fresh self-hosted instance starts in, so getting
  it right mattered more than the "happy path" here.
- The "Git repository" card on an existing site's Settings page correctly shows the "GitHub isn't
  connected yet, connect one in Settings → GitHub first" state, linking to the right place.
- `SiteDeploymentsPage.tsx`'s new `Source` column renders `upload` correctly for a pre-existing,
  non-git deployment; the deployment detail sheet's success view is unaffected (no spurious git-source
  line for an upload-sourced deployment).
- Full end-to-end webhook → build → activate flow, and the non-production-branch preview path, proven
  via `SiteGitDeploymentTests`'s real Docker builds against fakes (see Tests above) — not through the
  console UI live, since that requires a real GitHub App.

**Not verified — genuinely needs the owner**, per the kickoff prompt's own instruction not to create or
register a GitHub App on their behalf, and its "Deploying" section's instruction not to touch
`praxycore.dev` unless asked: create a real GitHub App per `docs/self-host.md`'s new steps, install it,
connect a real site to a real repository with a real production branch, push to that branch and watch it
build and go live automatically, push to a different branch and confirm a preview deployment appears
without touching production, confirm an unrelated repository's push webhook is correctly ignored — the
exact wording of the prompt's own "Done means" owner-test.

## Next

This closes the four-phase Sites sequence the owner committed to. No `sites-phase-5-prompt.md` is
written — per the kickoff prompt, none is expected unless the owner opens a new one (framework presets
beyond Next.js remain deferred, per the owner's 2026-08-22 call). The natural next step already designed
for but explicitly not started: a Functions git-integration phase consuming this same `Praxy.Vcs` layer
— `FunctionsService.CreateDeploymentAsync`'s shape is close enough to `SitesService`'s own that this
phase's restraint (no abstract "connected resource" interface invented on spec) should pay off directly
when that phase adds its own parallel `functions` query alongside `HandleGitPushAsync`'s existing `sites`
one.
