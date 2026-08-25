# Session task — Sites Phase 4 (git integration)

## Why this exists

Sites Phases 1–3 (subdomain hosting, preview URLs + graceful redeploy, custom domains) are shipped and
live on praxycore.dev. Every deployment today comes from a console tar upload or the bundled starter
template. The owner wants push-to-deploy: connect a site to a GitHub repository, push to its production
branch to build and go live automatically, push to any other branch to get a preview without touching
production.

Read `docs/research/praxy-sites.md`'s "Phase 4" section in full before writing any code — it's the actual
design spec, grounded in Appwrite's real `deploy-from-git` and self-host `version-control` docs (re-checked
fresh for this phase, not recalled from the original Phase 1-era sketch) and in the current, real shape of
`SitesService`, `SiteBuildWorker`, `SiteRuntimeTemplates`, and `Praxy.Webhooks`' signature-verification
class. Two things that section found **cut** scope versus the original sketch, worth internalizing before
you start: Appwrite doesn't post commit statuses or PR comments back to GitHub (drop that from Praxy's
design too), and Sites Phase 2's existing preview-URL mechanism already does everything a non-production
push needs — this phase adds a new deployment *source*, not new serving infrastructure.

This is the last of the four phases the owner committed to in sequence. Framework presets beyond Next.js
remain deferred indefinitely (owner's explicit call, 2026-08-22). Work on a new branch off `main`. Read
`CLAUDE.md` first.

## Non-goals — do not build these

- **No commit statuses or PR comments posted back to GitHub.** Verified against Appwrite's own
  `deploy-from-git` docs: they don't do this either. Don't add GitHub Checks/Commit-Status API calls.
- **No branch-pattern filters** (`preview/**`-style globs matching multiple branches to different
  environments). Appwrite only added this in a later release (1.9.5) than the version this design is
  based on. One fixed production branch per site, everything else is a preview — that's the whole model
  for v1.
- **No build-command auto-detection.** Moot for Praxy specifically — the build pipeline is already fixed
  to Next.js + `SiteRuntimeTemplates`'s generated Dockerfile. A git-sourced deployment goes through the
  exact same Dockerfile generation an uploaded tar does; only the source of the files changes.
- **No git providers besides GitHub.** No GitLab, no Bitbucket, no generic git URL + deploy key.
- **No changes to console tar upload or the starter-template deploy.** Both keep working unchanged for a
  site that never connects a repository — git integration is an additional deployment source, not a
  replacement.
- **No access control on preview URLs beyond what Phase 2 already has** (URL-is-the-secret, no login
  check). Appwrite restricts preview links to org members; Praxy doesn't have an equivalent mechanism
  today and this phase shouldn't invent one just for git-triggered previews specifically.
- **No multi-repository-per-site or multi-site-per-repository complexity.** One site connects to at most
  one repository at a time.

## Scope

1. **`Praxy:Sites:GitHub:*` config**: `AppId`, `ClientId`, `ClientSecret`, `PrivateKey` (PEM, likely wants
   the same encrypted-at-rest treatment `InstanceKey` already gives other secrets — your call on whether
   it's a startup config value or something rotatable via the console; a startup-only value matches how
   every other instance-wide secret in this codebase is handled, so default to that unless you find a
   concrete reason not to), `WebhookSecret`. Instance-wide, not per-project — the GitHub App itself is
   configured once per Praxy instance, same as Appwrite's own `_APP_VCS_GITHUB_*` env vars.
2. **`vcs_installations` table** (new EF migration): `id, installation_id, account_login, account_type,
   created_at`. Instance-wide (no `project_id` — one GitHub App installation can cover repositories used
   by any project). `GET /v1/sites/vcs/github/callback` is GitHub's installation-flow redirect target;
   exchange whatever GitHub hands back for a stored installation record. Console needs a place to trigger
   "Install/connect GitHub" and see the current installation status — a new instance-level settings
   surface (not per-site), the closest existing precedent being platform/API-key management screens, not
   anything site-specific.
3. **`sites` gains `repository_full_name`, `production_branch`** (both nullable). **`site_deployments`
   gains `source` (`upload`|`git`), `commit_sha`, `commit_message`, `branch`** — for console display and
   so the build worker and console both know which deployments came from a push versus a manual upload.
4. **`POST /v1/sites/vcs/github/webhook`**: verifies GitHub's own webhook signature (see Landmines — this
   is a different wire format from `Praxy.Webhooks.WebhookSignature`, do not reuse that class as-is),
   parses `push` events, matches `repository.full_name` against connected `sites` rows. For each matching
   site: if the pushed ref is that site's `production_branch`, create a `SiteDeployment` with
   `source="git"` and let the existing auto-activate-on-success path in `SiteBuildWorker` handle the rest
   unchanged; otherwise create the deployment without activating it — it becomes `ready` and reachable at
   its Phase 2 preview URL exactly like any other non-active deployment already is, no new code needed for
   that part.
5. **Git-sourced build context**: a short-lived GitHub App installation access token (see Landmines for the
   two-step JWT → installation-token exchange) clones the pushed commit, and a new sibling to
   `SiteRuntimeTemplates.BuildContextAsync` builds the same generated-Dockerfile Docker context directly
   from the checked-out directory instead of from an uploaded tar's `MemoryStream`. The Dockerfile
   generation itself (`Dockerfile(rootDirectory, baseImage, envVarKeys)`) doesn't need to change at all.
6. **Console**: a "Git repository" card on `SiteSettingsPage.tsx`, mirroring the "Custom domains" card
   Phase 3 just shipped structurally (add/connect flow, current-state display, disconnect action) —
   connect (only available once an instance-level GitHub installation exists), pick production branch from
   the repo's real branch list (an API call through the installation token), show the connected
   `owner/repo` + branch once set, disconnect action. `SiteDeploymentsPage.tsx`'s deployment list should
   show `source`/`commit_sha`/`branch` for git-sourced rows alongside the existing columns.

## Landmines — read before writing code

- **GitHub's webhook signature format is not the same scheme `Praxy.Webhooks.WebhookSignature` uses.**
  That class implements Stripe-style signing (`v1=<hex HMAC-SHA256(timestamp + "." + body)>`, timestamp in
  a separate header) — a deliberate Phase 6 choice, not GitHub's format. GitHub sends
  `X-Hub-Signature-256: sha256=<hex HMAC-SHA256(raw body only)>`, no timestamp mixed in. Write a
  GitHub-specific verifier; it's fine (good, even) to reuse the *discipline* — constant-time comparison via
  `CryptographicOperations.FixedTimeEquals`, the same primitive `WebhookSignature.Verify` already uses —
  just not the class or the wire format.
- **The raw request body must be captured before any model binding touches it.** Signature verification
  needs the exact bytes GitHub signed; letting ASP.NET Core deserialize the payload first (implicitly
  consuming and potentially re-encoding the stream) before you compute the HMAC is a classic way to get a
  verifier that fails on real traffic despite passing every unit test built against a byte-identical fixture.
  Read the raw body explicitly, verify against those exact bytes, deserialize afterward.
- **GitHub App authentication is a two-step token exchange, not a single bearer token.** Sign a short-lived
  JWT with the App's own private key (RS256, `iss` = App ID, ~10 minute expiry — GitHub's documented
  format), exchange that JWT for an installation access token (also short-lived, ~1 hour) scoped to one
  specific installation, and use *that* token for clone/API calls. Don't try to use the PEM private key
  directly as a bearer credential anywhere — it only ever signs the JWT.
- **Self-hosted instances must be internet-reachable for GitHub to deliver webhooks at all.** A bare
  `dotnet run` on `localhost` cannot receive them without a tunnel (ngrok or equivalent). `praxycore.dev`
  is already public — target that for the real owner-test rather than fighting local dev networking for a
  phase whose entire point is receiving inbound webhooks.
- **A git clone needs a place to happen that isn't inside the Docker build context tar-handling code
  path.** `SiteRuntimeTemplates.BuildContextAsync` currently reads a `MemoryStream` in-process; a shallow
  clone needs real disk (or an in-memory git implementation, almost certainly not worth it) — decide where
  that temp directory lives and that it's reliably cleaned up even when a build fails or is cancelled,
  the same discipline `SiteDeploymentSource`'s "tar bytes deleted once the build finishes, success or
  failure" already follows for uploads.

## Tests

`tests/Praxy.Tests.Integration/` — a `SiteGitDeploymentTests.cs` covering: a real `push` webhook payload
(fixture, not a live GitHub round trip — signed with a test secret) to the production branch creates and
auto-activates a deployment; the same to a non-production branch creates a deployment that's reachable at
its preview URL but doesn't touch the site's active deployment; an unsigned or badly-signed payload is
rejected before any deployment is created; a payload for a repository no connected site references is a
no-op, not an error. Unit tests for the GitHub signature verifier (valid, invalid, wrong-secret, tampered
body) mirroring `Praxy.Webhooks`' own signature test coverage. The JWT-signing/installation-token exchange
and the actual clone step are the hardest pieces to test without hitting real GitHub — isolate them behind
an interface so the webhook-handling and build-worker logic can be tested against a fake, and document
plainly in the report which parts were proven against fixtures versus a real GitHub App installation.

## Done means

- `dotnet test` green (unit + integration, real Docker daemon).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run against `praxycore.dev`** (per the internet-reachability landmine above): the
  owner creates a real GitHub App per this phase's setup docs, installs it, connects a real site to a real
  repository with a real production branch, pushes to that branch and watches it build and go live
  automatically, pushes to a different branch and confirms a preview deployment appears without touching
  production, confirms an unrelated repository's push webhook is correctly ignored.
- `git status` clean, conventional commits, on a new branch off `main`.
- `docs/self-host.md` gets a "Git integration" section: exact GitHub App creation steps (permissions,
  callback/webhook URLs, the six config values), mirroring the level of detail Appwrite's own
  `version-control` self-host doc has.
- Write `docs/handoff/sites-phase-4-report.md`. This closes the four-phase sequence the owner committed to
  — no further `sites-phase-N-prompt.md` is expected unless the owner opens a new one (framework presets
  beyond Next.js remain deferred, per the owner's own 2026-08-22 call).

## Deploying (only if the owner asks)

This phase's entire purpose requires a real, owner-created GitHub App and a real webhook endpoint — do not
attempt to set up or register a GitHub App on the owner's behalf, and do not deploy this to `praxycore.dev`
without being asked, since it changes what's reachable at a real webhook URL on their live domain.
