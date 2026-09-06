# Security review — Phase 3 report: authorization and project isolation

Design: `docs/research/security-review.md`. Prompt: `docs/handoff/security-review-phase-3-prompt.md`.
Scope: the permission model itself across all three subsystems — Storage's additive bucket/per-file
grants, a function's minted credential scope and lifetime, project isolation at entry points that bypass
the normal API surface (the Sites proxy, the GitHub webhook, the schedulers/workers), the API key scope
model (untouched by Phases 1-2), the several principal/project resolution entry points, and cross-project
id confusion generally. This is the last phase in the sequence.

Every reachable finding below was demonstrated with a dedicated integration test against a real,
from-scratch Postgres (Testcontainers, `postgres:17-alpine`) and, for Sites/Functions findings, a real
Docker daemon doing genuine `docker build`/`docker run` calls — never a mock or an in-memory stand-in for
either. Each such test was run and shown failing before its fix and passing after, with the specific
before/after evidence quoted in that finding's own section, except where noted as reading-only. Full
`dotnet test`: 619 unit + 337 integration, 0 failed.

## 1. Findings table

| id | title | anonymous caller | project developer | status |
|----|-------|-------------------|--------------------|--------|
| A | A preview hostname naming a *different* project's real deployment id reaches that project's live container | **Critical** | **Critical** | **Fixed** |
| B | A `BypassRowPermissions` key scoped only to `databases.read` could firehose-subscribe to `users.*`/`teams.*`/`buckets.*` and see every account/membership/file event | N/A (needs a key) | **High** | **Fixed** |
| C | Minting a realtime ticket for a key never checked the realtime scope (only connect/redeem did) | N/A | Low | **Fixed** |
| D | `DELETE .../teams/{id}/memberships/{id}` let an unauthenticated caller distinguish "exists" (404) from "doesn't exist" (401) | Low | Low | **Fixed** |
| E | Two different projects could both connect a site/function to the identical GitHub repository, so one push redeploys both | Informational (needs console access) | Medium → **Critical under multitenancy** | **Fixed for new connections**; pairs that already exist are untouched — see the finding |
| F | `PRAXY_FUNCTION_JWT`'s lifetime was a flat 15 minutes regardless of the invocation's own timeout | N/A | Medium | **Fixed** |
| G | `FunctionExecutionService.RunAsync` looked up a function by id alone, with no defense-in-depth project check | N/A (not reachable today) | Low | **Fixed** (hardening) |
| H | Storage's additive bucket/per-file permission model, and cross-bucket/cross-project file-id confusion | N/A | N/A | **Verified sound, no defect found** (2 coverage tests added) |
| I | API key → project resolution and scope enforcement across Storage/Tables/Rows/Functions/Users/Teams | N/A | N/A | **Verified sound, no defect found** |
| J | Sites' production-hostname and custom-domain resolution, build workers, and the function scheduler | N/A | N/A | **Verified sound, no defect found** |
| K | `GitHubWebhookSignature` HMAC verification; `installation.id` parsed but never consumed | N/A | N/A | **Verified sound, no defect found** |
| L | "One role resolver" is true for *role resolution*; the allow/deny/per-item *decision* logic is three independently-maintained copies (Tables/Storage/Functions) | Informational | Informational | **Accepted — documented, not fixed** |
| M | `PRAXY_FUNCTION_API_KEY` (schedule/event triggers) never expires and can hold any scope an operator grants | N/A | Medium → **multitenancy prerequisite** | **Accepted — reaffirms a prior decision** |

## 2. Findings, one section each

### A — A forged preview hostname reaches another project's live container

**What an attacker does.** Requests an ordinary-looking preview URL for a site they own —
`<someGuid>.<their-own-key>.<their-own-projectId>.{Domain}` — except the leading label is not one of
their own deployment ids. It is the real, already-active deployment id of a *different* project's site,
obtained however (a shared preview link, a build log, a PR comment, browser history — the id itself is
a UUIDv7 and needs no special protection for this attack to work, because the flaw is not that the id
is guessable, it's that *any* id, once known from anywhere, becomes a bearer token to that container).
No malformed input, no crafted payload — the request is a plain `GET` with a spoofed `Host` header.

**What they get.** `SiteProxyMiddleware.InvokeAsync`'s preview branch
(`src/Praxy.Sites/SiteProxyMiddleware.cs`, pre-fix line 116) resolved `site` correctly from the
hostname's own `<key>.<projectId>` (proving only that *the attacker's own site* exists), then checked
`registry.TryGet(deploymentId, out running)` against `SiteContainerRegistry` — a single, unpartitioned,
process-wide `Dictionary<Guid, Entry>` holding the live production container of **every enabled site on
the instance, in every project**, plus every currently-warm preview. The registry hit skipped the
`d.Id == deploymentId && d.SiteId == site.Id` ownership check entirely — that query ran **only on a
registry miss**. Once past it, `ForwardToContainerAsync` streamed the full request (headers, body,
WebSocket upgrade) to the victim's real container and returned the victim's real response, logging the
request under the *attacker's* `site.Id`/`project.Id` (corrupting the victim's own request-log audit
trail as a side effect, since the log row for what actually happened lands under the wrong project).
Because the request is a full proxy pass-through, not just a read, this reaches any API routes the
victim's own app exposes — not merely disclosure of static content.

**Why this doesn't require multitenancy to matter today.** A site's hostname is public by design (that
is the entire point of hosting one), and creating an enabled site costs nothing (no deployment required
— `Site.Enabled` defaults to `true`). So the "attacker" role here needs only *some* project on the
instance and *any* one legitimate site's `<key>.<projectId>` — which is, by construction, the same
information the site's own public URL already reveals to every visitor. A fully anonymous internet
caller with zero credentials, who has simply visited any one publicly deployed site on the instance
before, already has everything this attack needs except the victim's deployment id.

**How it was demonstrated.** `SiteProxyIsolationTests.A_forged_preview_hostname_naming_another_projects_own_deployment_id_does_not_reach_its_container`
(new): creates a "Victim" and an "Attacker" project (one ordinary operator, two ordinary projects — no
crafted attack shape needed), deploys the victim's site with a body of `victim-secret-payload`, creates
an attacker site with no deployment of its own, then requests
`https://{victimDeploymentId}.attacker-site.{attackerProjectId}.sites.localhost/`. Reproduced live before
the fix by temporarily disabling only the new ownership predicate (keeping every signature intact): the
attacker's request returned **200** with the victim's `victim-secret-payload` body. After the fix: the
same request returns a clean 404 ("This preview is not available."), and a second assertion in the same
test confirms the attacker's *own* preview of their *own* deployment still works — proving the fix
rejects only the cross-site case, not preview requests generally (the class of mistake Phase 2's own
Finding A warns about: a fix that "compiles" but silently breaks the ordinary case too).

**Severity.** Critical under both actor models — full-container access requiring no credentials beyond
knowledge of one public site's own URL, reachable today, not merely a multitenancy-latent risk.

**The fix.** `SiteContainerRegistry`'s internal `Entry` now records the owning `SiteId` alongside the
container (`src/Praxy.Sites/SiteContainerRegistry.cs`); `TryGet`/`Set`/`StartOrJoinAsync` all take a
mandatory `Guid expectedSiteId`/`siteId` parameter rather than offering an unscoped overload at all — so
a caller cannot forget the check the way this bug forgot it, the same "make the wrong thing
uncompilable" approach the Storage review praised elsewhere. Every call site
(`SiteProxyMiddleware`'s production/preview/custom-domain branches, `SitesService.ActivateAsync`/
`IsRunning`, `SiteReconciler`) now threads the already-resolved site's own id through. A new
`AllContainers()` (site-unscoped, explicitly documented as cleanup/ops-only, never request-path) replaces
the handful of test-fixture teardown loops that previously called the now-removed unscoped `TryGet`.

**Test.** `SiteProxyIsolationTests.cs` (new file) — the finding's own repro above, verified failing
before the fix and passing after, plus the "attacker's own preview still works" boundary check in the
same test.

### B — A `databases.read` bypass key firehose-subscribes to `users.*`/`teams.*`/`buckets.*`

**What an attacker does.** This isn't an external attacker — it's an operator's own legitimately
narrowly-scoped integration turning out to have far more reach than the scope it was granted. An
operator mints an ordinary "trusted server, only touches the database" key: `scopes: ["databases.read"]`,
`bypassRowPermissions: true` (a real, realistic grant shape — `BypassRowPermissions` is documented as
exactly the flag for a trusted unattended service, independent of which scopes it also holds). That key
opens a realtime socket and subscribes to the firehose wildcards `users.*`, `teams.*`, `buckets.*`.

**What they get.** `RequireRealtimeScope` (`src/Praxy.Api/Endpoints/RealtimeEndpoints.cs`) was, and
remains, the single gate at connect time — but it only ever checked `databases.read`, regardless of what
the connection would go on to do. `ConnectionRegistry.Reindex`'s bypass branch
(`src/Praxy.Realtime/ConnectionRegistry.cs`) honored a `"<resource>.*"` wildcard subscription for *any*
bypass connection with no further check, and fan-out matching for a wildcard subscription keys purely on
the event's own type prefix (`users`, `teams`, `buckets`) — none of which is `databases`. So a key holding
only `databases.read` received every user account event (password/email changes, session creation —
`AppAuthService`'s own event list), every team/membership event, and every file event project-wide,
despite the operator never granting `users.read`/`teams.read`/`storage.read`. This defeats the operator's
own intentional scope narrowing for whatever service that key was actually handed to.

**How it was demonstrated.** `RealtimeTests.A_bypass_key_scoped_only_to_databases_read_cannot_firehose_user_events`
(new): mints exactly that key shape, subscribes to `users.*`, then renames a signed-up app user via the
ordinary `PATCH /v1/account/name` endpoint. Verified failing before the fix (the socket received the
rename event) and passing after (silence, per `AssertSilentAsync`). A companion test,
`A_bypass_key_holding_users_read_still_receives_the_user_firehose`, confirms a key that *does* hold
`users.read` keeps working — the fix narrows by scope, it doesn't remove the firehose feature.

**Severity.** Not reachable by an anonymous caller (needs a minted key). High for a project developer:
it defeats the one control (`ApiKeyScopes`) an operator has for limiting what a given integration or
collaborator's key can see, in a system whose whole security model for "who can see what" is scopes plus
row/table permissions.

**The fix.** `Connection.AllowedBypassResources` (`src/Praxy.Realtime/Connection.cs`, new, nullable —
null for an operator console connection, which stays fully unrestricted since organization membership
already grants it everything in the project) carries the resource words (`databases`/`buckets`/`users`/
`teams`) a bypass **key**'s own scopes actually cover, computed once at connect/ticket-redeem time
(`RealtimeEndpoints.AllowedBypassResourcesFor`). `ConnectionRegistry.Reindex` now refuses to index *any*
bypass subscription — wildcard or an exact channel — outside that set (`ResourceWord`, the channel's own
leading dot-segment), consistent with the codebase's existing "an unrecognized/unauthorized channel is
simply inert" philosophy rather than introducing a new error path.

**Self-review note.** The fix's first draft threaded `AllowedBypassResources` through a `bypass: bool`
parameter on the pre-existing `BuildAsync` local helper in `RealtimeEndpoints.ResolveCallerAsync`; once
both bypass call sites (API key, operator) needed their own distinct value for that field, `BuildAsync`'s
own `bypass` branch became dead code no caller could still reach. Removed in the same commit rather than
left behind as a second, unreachable copy of "how to build a bypass caller."

**Test.** `RealtimeTests.cs`: the two tests above.

### C — Minting a realtime ticket for a key skipped the scope check

**What an attacker does/gets.** Nothing on its own — `POST /v1/realtime/ticket` never called
`RequireRealtimeScope` for a `Key` principal, unlike the socket-connect path and the ticket-*redeem*
path, both of which do. A key missing `databases.read` could still mint a ticket; redemption then
rejected it, so the inconsistency was not independently exploitable, only confusing (a caller reaching
"ticket minted successfully" before finding out one round trip later that it's useless).

**Severity.** Low/Low — an inconsistency the codebase's own pattern (checked in two of three places, not
the third) flagged as worth closing rather than a live vulnerability.

**The fix.** `CreateTicket` now runs the same `RequireRealtimeScope` check for a `Key` principal before
minting, mirroring the other two call sites exactly.

**Test.** `RealtimeTests.Minting_a_ticket_for_a_key_without_the_realtime_scope_fails_at_mint_time` (new)
— a `storage.read`-only key now gets `401 general_unauthorized_scope` at mint time instead of a token
that redemption would have rejected anyway.

### D — `DeleteMembership` leaked whether a membership exists via 404-vs-401 ordering

**What an attacker does.** Sends `DELETE /v1/teams/{teamId}/memberships/{membershipId}` with no
credentials at all (no session, no API key) for two membership ids: one real, one made up.

**What they get.** Every other handler in `TeamEndpoints.cs` resolves access
(`RequireTeamAccessAsync`/`RequireUserOrScope`) **before** loading the team/membership — precisely so an
unauthorized caller can't tell "doesn't exist" apart from "exists but you can't touch it," a design
principle this codebase states explicitly in more than one place (`FilesService`'s own doc comment:
"the existence of someone else's file doesn't leak through the status code"). `DeleteMembership`
(`src/Praxy.Api/Endpoints/TeamEndpoints.cs`, pre-fix) hand-rolled its own check *after* loading both rows:
a made-up id 404'd (`TeamNotFound`/`MembershipNotFound`), a real one reached the scope check and got 401
(`general_unauthorized`) instead. Same guest caller, same team, different status code depending purely on
whether the membership id happens to exist.

**Severity.** Low/Low — the leaked fact is only "does this opaque UUID exist," not access to anything;
recorded because the codebase's own stated threat model treats this exact leak shape as worth closing
everywhere else.

**How it was demonstrated.** `TeamsTests.An_unauthenticated_delete_cannot_tell_a_real_membership_from_a_made_up_one`
(new): a real, API-key-created membership and a random made-up wire id, both `DELETE`d with no
credentials. Verified failing before the fix (404 vs 401) and passing after (401/`general_unauthorized`
for both).

**The fix.** `DeleteMembership` now calls `AppPrincipalFilter.RequireUserOrScope(http, TeamsWrite)` first
— a `Guest` is rejected immediately, a `Key` must hold `TeamsWrite` before anything is loaded, and a
session passes through unconditionally (self-removal is always allowed, checked afterward against the
now-loaded membership) — the same ordering every other handler in the file already uses.

**Test.** `TeamsTests.cs`: the test above.

### E — Two projects connecting to the same GitHub repository both redeploy on one push

**What an attacker does.** Two different projects on the same instance — call them P1 and P2 — each
independently connect a site or function to the identical `owner/repo` string (realistic for a shared
org monorepo, or simply two teams both given access to the same GitHub App installation; the installation
itself is instance-wide with no per-project scoping). No forged webhook payload needed.

**What they get.** `SitesService.HandleGitPushAsync`/`FunctionsService.HandleGitPushAsync`
(`src/Praxy.Sites/SitesService.cs`, `src/Praxy.Functions/FunctionsService.cs`, pre-fix) matched a push
purely on `RepositoryFullName`, globally, with no `ProjectId` filter and no notion of which GitHub App
installation authorized which connection (`GitHubAppService`'s own remark: "Praxy never tracked which
installation covered which repository"). A single push to that repository redeployed **both** P1's and
P2's connected resource — P2 receives a deployment (with P1's commit SHA/message copied into its own
`SiteDeployment` row) it never requested, on a schedule it doesn't control, running whatever code that
commit contains. Since both deployments build from the *same underlying repository content*, this is not
only a metadata leak — it is P2's site or function actually rebuilding and going live on code neither P2
nor its own workflow chose to deploy.

**Severity.** Not reachable by an anonymous caller (connecting a repository needs console access).
Medium under today's single-trusted-operator model (this requires the same operator's own projects to
share a repo, which is closer to a self-inflicted footgun than an attack) — but this is exactly the
"harmless under one operator, critical under multitenancy" shape the review's own threat model calls out:
once a second, untrusted developer can independently connect *their own* project to a repository they
don't administer (their own fork, or one the shared installation happens to cover), a push they control
redeploys a project that isn't theirs, running code they authored.

**How it was demonstrated.** `VcsRepositoryIsolationTests.cs` (new file, reusing
`SiteGitDeploymentTests`'s existing GitHub fakes — no real network call anywhere in the suite): project A
connects a site to `acme/website`; project B's attempt to connect either a site or a function to the same
repository now gets `409 vcs_repository_already_connected` instead of succeeding. A third test confirms
the documented, intended case — one project's site *and* function sharing one repository — still works.

**The fix.** `ConnectRepositoryAsync` in both `SitesService` and `FunctionsService` now rejects (409,
`ErrorTypes.VcsRepositoryAlreadyConnected`, new) a repository already connected by a **different**
project's site or function, checked via the shared `PraxyDb` (`db.Sites`/`db.Functions` are both
reachable from either service without a new project reference, since both already depend on
`Praxy.Persistence`). A same-project match — the documented site+function sharing case, or simply
re-connecting/re-branching the same site's own existing connection — is explicitly excluded and stays
allowed.

**Accepted limitation, not fixed further.** This is an application-level check-then-write, not a
database constraint spanning two tables — a genuine (very narrow) TOCTOU exists if two connect requests
for the identical repository string from two different projects race at the exact same instant. Closing
that fully would mean either a cross-table uniqueness mechanism or tracking installation-to-repository
ownership explicitly (the deeper fix `GitHubAppService`'s own remark gestures at) — a real redesign of
how repository connections are tracked, out of a review phase's budget. The sequential case — which is
what every realistic "two teams independently set up a connection" scenario actually looks like — is
fully closed.

**Test.** `VcsRepositoryIsolationTests.cs` (new file): the three tests above.

**Residual, found in review before merge: this closes the door, not the room.** The guard is at
*connect* time; the harm happens at *push* time, and `HandleGitPushAsync` is unchanged — it still
resolves a push purely by `db.Sites.Where(s => s.RepositoryFullName == evt.RepositoryFullName)` with
no project predicate at all. So an instance that **already** has two projects on one repository stays
fully exposed: every push still fans out to both, and nothing detects, warns, or reports it. Only new
connections are prevented.

This is the case the prompt's own lesson 3 exists for — "exercise the property against state that
predates whatever you change" — and this finding's tests create both projects fresh, so the
pre-existing shape was never exercised.

**Not fixed, deliberately.** Making `HandleGitPushAsync` fail closed on an ambiguous match would
silently stop deploying for an instance whose (insecure) setup currently works, which is a worse
failure than the one it prevents, and picking a winner between two equally valid claims is a design
decision rather than a review fix. Recorded as an accepted risk instead, with the detection query an
operator can actually run:

```sql
SELECT repository_full_name, count(DISTINCT project_id) AS projects
FROM (SELECT project_id, repository_full_name FROM praxy.sites WHERE repository_full_name IS NOT NULL
      UNION ALL
      SELECT project_id, repository_full_name FROM praxy.functions WHERE repository_full_name IS NOT NULL) r
GROUP BY repository_full_name HAVING count(DISTINCT project_id) > 1;
```

Zero rows means nothing to do. **Checked against praxycore.dev: zero rows** — its only
repository-connected pair is a site and a function within the same project, which is the documented,
intended case. Any row returned should be disconnected from all but one project by hand.

### F — `PRAXY_FUNCTION_JWT`'s lifetime was flat regardless of the invocation's own timeout

**The question.** Phase 1's own follow-up, explicitly deferred to this phase
(`docs/handoff/security-review-phase-1-report.md`): now that this JWT is guaranteed freshly minted per
invocation (Phase 1's Finding C closed the cross-invocation leak), is its 15-minute
`AccountJwtService.DefaultLifetime` still the right value?

**What was true.** `FunctionExecutionService.BuildEnvAsync` (`src/Praxy.Functions/FunctionExecutionService.cs`,
pre-fix) minted every user-triggered invocation's JWT with the same flat 15-minute lifetime — the same
constant the self-service `/account/jwts` endpoint's own cap uses — regardless of how long that
invocation could actually run. A **sync** invocation (the primary data-plane path) is hard-capped at
`MaxSyncTimeoutSeconds` (30s default) and its container stopped immediately after; it received a token
valid up to **30x longer** than the request that could ever legitimately use it. If a function's own code
(or a compromised dependency it pulls in) exfiltrates `process.env.PRAXY_FUNCTION_JWT` — logs it, returns
it in a response, phones it home — the resulting bearer credential (scoped correctly and narrowly to "act
as the triggering user," confirmed sound by this phase's own research and not the defect here) stayed
usable from anywhere for up to fifteen minutes after a sub-second invocation, not merely for the
invocation's own runtime.

**How it was demonstrated.** `FunctionWarmPoolCredentialIsolationTests.The_minted_function_jwts_lifetime_is_bounded_by_the_invocations_own_timeout_not_a_flat_default`
(new, real Docker): invokes a function whose `timeoutSeconds` is 15, decodes the returned JWT's own `exp`
claim (no signing key needed — just the standard base64url JWT payload segment), and asserts the lifetime
lands near "15s + grace," nowhere near 900s. Verified failing before the fix (measured ~14m59s) and
passing after (well under 60s).

**Severity.** Not reachable by an anonymous caller directly (requires the function's own code to leak its
credential first — a supply-chain scenario, not a network attack). Medium for a project developer: it's
their own function's credential-leak blast radius, needlessly widened by a shared default that was never
re-examined for this specific, narrower use.

**The fix.** The JWT's lifetime is now `timeoutSeconds + JwtGraceSeconds` (10s, for the callback's own
round trip — container start, connect, the verification call itself) instead of the flat
`AccountJwtService.DefaultLifetime` — `timeoutSeconds` being the exact value already computed for the
invocation's own hard cap (`Math.Min(fn.TimeoutSeconds, options.MaxSyncTimeoutSeconds)` for sync, up to
900s for async), so a sync invocation's leaked-JWT window shrinks from 15 minutes to well under a minute,
and even the rare 900s async ceiling is no longer *also* the default for the common sub-second case.
`AccountJwtService.DefaultLifetime` itself is untouched — it still backs the unrelated self-service
`/account/jwts` endpoint, which this phase did not touch.

**Test.** `FunctionWarmPoolCredentialIsolationTests.cs`: the test above.

### G — `FunctionExecutionService.RunAsync` had no defense-in-depth project check

**What was true.** `RunAsync` looked up `db.Functions.FirstOrDefaultAsync(f => f.Id == execution.FunctionId, ct)`
— by `FunctionId` alone, trusting `execution.ProjectId` (used moments later to mint the invocation's JWT
and set `PRAXY_PROJECT_ID`) to already agree with whatever project the resolved function actually belongs
to. Tracing every current creator of a `FunctionExecution` row (`FunctionsService.CreateExecutionAsync`,
`FunctionEventDispatcher.DispatchNextAsync`, `FunctionScheduler.FireDueAsync`) confirms `ProjectId` is
always stamped from the very same already-project-scoped function/claimed row `FunctionId` comes from —
so the two cannot actually disagree today, and this is not a live, exploitable defect. It is exactly the
shape of thing worth hardening anyway: nothing *enforces* that invariant at the one place that would mint
a JWT and set an env var for the wrong project if a future creation path (or a hand-inserted row) ever
violated it.

**The fix.** The lookup now filters on `f.Id == execution.FunctionId && f.ProjectId == execution.ProjectId`
together. Purely a tightening — every legitimate execution row still resolves identically, confirmed by
the full Functions test suite passing unchanged.

**Test.** None added specifically (there is no way to construct the violating state through any current
code path to demonstrate a before/after difference — this is stated as reading-only hardening, not a
demonstrated fix, consistent with the phase's own "reading-only findings marked as such" standard).

## 3. Verified sound (no defect found)

### H — Storage's additive bucket/per-file permission model; cross-bucket/cross-project file-id confusion

Read `FileAccessRules.Resolve`, `FilesService`'s `RequireFileAsync`/`RequireBucketAsync`/`FindAsync`, and
`BucketsService.GetAsync`, then traced every file-touching endpoint (plain download, Range, transform/
derivative, metadata, upload, replace, delete, and both bucket- and file-level permission grant/revoke
endpoints) end to end. Confirmed:

- Exactly one shared decision function (`FileAccessRules.Resolve`) backs every path; Range parsing and
  the transform pipeline are both permission-free and only ever run after that decision has already
  produced a `StoredFile` — matching the documented design exactly.
- The additive property is real, not just documented: a bucket-level `Allow` always short-circuits before
  any `FilePermission` row is even queried (`FilesService.RequireFileAsync`), and the `Resolve` function's
  branch order proves no path exists where a per-file grant is checked *instead of* the bucket grant.
- Every file lookup is scoped by `(BucketId, Id)` together (`FilesService.FindAsync`), and every bucket
  lookup by `(ProjectId, Id)` together (`BucketsService.GetAsync`) — never by a bare id with project/
  bucket membership checked as a separate, skippable step. No endpoint resolves a `StoredFile` by id
  alone anywhere in `Praxy.Storage` or `Praxy.Api`.

No defect found. Two coverage gaps were closed with new tests rather than left as "correct by reading
alone": `StorageEngineTests.A_data_plane_api_key_cannot_reach_another_projects_bucket_by_id` (the existing
`Another_projects_bucket_is_not_reachable` test only exercised the console/operator surface, not the
data-plane's independent `X-Praxy-Project` + API-key resolution path) and
`A_file_uploaded_to_one_bucket_is_not_reachable_through_a_sibling_buckets_route` (a file id from bucket A
requested through bucket B's own route, same project, both 404 `FileNotFound`).

### I — API key → project resolution and scope enforcement across the data plane

Every API-key-reachable endpoint across Storage, Databases/Tables/Rows, Functions, Users, and Teams was
inventoried against `ApiKeyScopes`' twelve scopes. Findings: scope-gating is the exhaustive rule, not the
exception (Functions' invoke-path scope check is architecturally identical to every other write path, not
a special case); `ApiKeyService.ResolveAsync` genuinely intersects a key's own stored `ProjectId` against
the request's target project (`ApiKeyService.cs`) rather than validating the key and separately trusting
the caller's project claim, and every resource lookup chain re-filters by its immediate parent down to the
project root (bucket → project, file → bucket, table → database → project, row → table's own project
re-check even off a project-agnostic cache lookup in `RowsService.ResolveTableAsync`). No cross-project
key misuse found, and no read-scoped key found reaching a write path. Findings B, C, and D above were
found *during* this same inventory pass — the two "genuine bug" and one "inconsistency" items the prompt
asked this pass to actually produce, not merely confirm the model's soundness.

### J — Sites' production-hostname/custom-domain resolution, build workers, and the scheduler

`SiteProxyMiddleware`'s **production** path (`site.ProjectId == projectId && site.Key == key` from the
hostname, serving only `site.ActiveDeploymentId`) and **custom-domain** path (`SiteCustomDomainLookup`
against a schema-enforced globally-unique `Hostname` column, `domain.SiteId` fixed at creation and never
reassigned by any endpoint) are both sound — only the preview branch (Finding A) had the gap.
`SiteBuildWorker`/`FunctionBuildWorker` resolve their site/function purely off the claimed deployment
row's own immutable `SiteId`/`FunctionId` FK, never re-deriving project id from a second, possibly-
disagreeing source. `FunctionScheduler.FireDueAsync` claims `id`, `project_id`, and `schedule` from the
identical locked row in one query — a schedule cannot be confused with, or made to target, another
project's function, the cleanest of every entry point traced this phase.

### K — GitHub webhook HMAC verification; `installation.id` parsed but unused

Re-confirmed `GitHubWebhookSignature.Verify`'s correctness (Phase 1's own "sound, worth not re-deriving"
list) and traced the payload parser: none of `repository.full_name`, `ref`/branch, `after` (commit SHA,
regex-validated), or `head_commit.message` is a Praxy-side resource or project id, so there is no path
where an attacker-controlled payload field is trusted as a routing identifier. One loose end noted:
`GitHubPushEvent.InstallationId` is parsed and then never referenced anywhere downstream — not a defect
(nothing trusts it, so there's nothing to exploit), but the concrete confirmation that Finding E's root
cause (no installation-to-repository ownership tracking) is real: the one field that *could* disambiguate
which connection authorized a given push is thrown away immediately after parsing.

## 4. Accepted risks

- **Finding E's TOCTOU** (described in its own section) — a same-instant race between two different
  projects' connect requests for the identical repository string could still both succeed. Accepted: the
  sequential case (every realistic scenario) is closed; a full fix needs installation-to-repository
  ownership tracking, a genuine redesign out of this phase's budget.

- **`PRAXY_FUNCTION_API_KEY`'s unbounded lifetime and scope breadth (Finding M).** Unlike the JWT path
  (Finding F, and Phase 1's Finding C before it), the schedule/event-trigger credential is not freshly
  minted per invocation — `FunctionsService.ApplyPlatformScopesAsync` creates it once with `expiresAt:
  null` and the same plaintext secret is decrypted and reused on every matching invocation until an
  operator explicitly clears the function's platform scopes. It can hold any subset of `ApiKeyScopes.All`
  an operator grants (never row-permission-bypassing — `bypassRowPermissions` is hardcoded `false` for
  this key — but scopes alone can include `users.write`/`databases.write`/etc.). A leaked platform key
  with a broad, legitimate-sounding grant (a "nightly cleanup job" needing `users.write` +
  `databases.write`) is therefore a **standing, non-expiring, project-admin-grade credential** — a
  materially larger and longer-lived blast radius than the equivalent JWT-path leak, for what is
  conceptually the same "function acting on behalf of something" shape. **This is not a new discovery**:
  `docs/handoff/functions-scheduled-credentials-report.md` already reasoned about and accepted this exact
  tradeoff (a persisted, function-owned key chosen over a fresh-per-invocation JWT specifically to avoid
  touching every `RequestPrincipal.Key`-shaped call site) and flagged it as a known, unrevisited gap.
  Reaffirmed here rather than fixed, for the same reason: giving it a rotating/expiring lifetime is
  meaningful engineering work against a shipped subsystem, not a review-phase-sized fix. Recorded again
  in this report specifically because Phase 3's own mandate is authorization/credential review, and this
  is now the single largest asymmetry left in the credential story across all three subsystems.

- **Finding E's pre-existing pairs** (described in its own section) — the connect-time guard does not
  reach two projects that were *already* sharing a repository when it shipped; `HandleGitPushAsync`
  still matches across every project. Accepted rather than fixed, because failing closed on an
  ambiguous match would silently stop deploying for a setup that currently works. Detection query in
  the finding; praxycore.dev returns zero rows.

- **Finding L (decision-logic duplication)** — see its own note in §2/§5. Accepted as documentation of a
  real drift risk rather than fixed, since building a genuine shared abstraction across Tables' raw-SQL
  predicate, Storage's C# enum decision, and Functions' simpler boolean check would be a cross-subsystem
  redesign, explicitly out of scope for a review phase.

## 5. Multitenancy prerequisites — the durable list

This is the last phase in the security-review initiative, so this section is written to stand on its own
rather than only add to Phases 1-2's lists — it references their entries rather than repeating the
reasoning behind them.

**From Phase 1** (`docs/handoff/security-review-phase-1-report.md` §4) — closed, not outstanding, but
recorded as the concrete proof this class of defect exists: Function containers sharing a Docker network
with Postgres, and no per-container isolation flags beyond memory/CPU, were both exactly this shape of
defect (fine under one trusted operator, critical the moment a second developer shares the instance) and
are now fixed. The Docker socket mount itself remains the standing, explicitly out-of-scope prerequisite
every phase in this initiative has worked around rather than re-litigated.

**From Phase 2** (`docs/handoff/security-review-phase-2-report.md` §4) — nothing rose to this bar; both of
that phase's fixed findings were equally severe for an anonymous caller as for a project developer.

**New from Phase 3:**

- **Finding A (this report) was, before the fix, exactly this class of defect in its purest form** — and
  the one exception to the usual pattern: it did *not* require a second untrusted developer to be
  dangerous today, because a site's own hostname is public by design. Recorded here anyway because it is
  the clearest illustration in this entire initiative of why "the operator can already see everything in
  their own projects" is not a safe assumption to build isolation on: the registry that made this
  possible held every project's containers together specifically because no isolation boundary between
  projects had ever been designed into it, not because one operator's own trust made a boundary
  unnecessary.
- **Finding E (cross-project repository fan-out)** — the clearest "harmless under one operator, critical
  under multitenancy" case this phase found: today it requires the *same* operator's own projects to
  share a repository (self-inflicted at worst); the moment a second developer can connect their own
  project to a repository they don't administer, a push they control redeploys code onto a project that
  isn't theirs. Its accepted TOCTOU (§4) and the deeper "no installation-to-repository ownership" gap
  `GitHubAppService` itself already documents are both **hard prerequisites for real git-integration
  multitenancy** specifically — any design that lets a second, untrusted developer connect their own
  repositories needs installation-scoped ownership tracking before that feature can be considered safe
  for them.
- **Finding M (`PRAXY_FUNCTION_API_KEY`'s unbounded lifetime/scope)** — reaffirmed from
  `docs/handoff/functions-scheduled-credentials-report.md`, now formally logged as a multitenancy
  prerequisite by this review: a standing, non-expiring, operator-scoped-but-potentially-broad credential
  is an acceptable tradeoff when the only party who can grant its scopes is also the only party who could
  ever be harmed by leaking them. It stops being acceptable the moment scopes can be granted by, or on
  behalf of, one developer in a way that could affect another.
- **The Docker socket mount** (unchanged from Phase 1, restated here because this is the initiative's
  last report): still the one standing, explicitly-conceded prerequisite underneath all three subsystems.
  No sandboxed alternative exists in this release.
- **Finding L (decision-logic duplication)** is *not* listed as a multitenancy prerequisite — it is a
  maintainability/drift risk that could, in the future, produce a defect of this shape, not a defect that
  exists today. Recorded separately (§4) rather than here, so this list stays a list of things that are
  actually true right now, not things that could theoretically become true.

## Owner test

The prompt's checklist asks for one scenario run by hand: two projects, each with its own bucket/file,
function, and site, confirming neither reaches the other's resources by any discoverable or guessable id.
That property is exercised here as several targeted, automated tests instead of one manual pass — each
isolating a single mechanism rather than one broad session bundling all of them together, which is what
actually let Finding A's precise failure mode (a registry hit skipping the ownership check) surface:
`SiteProxyIsolationTests` (site, by deployment id), `StorageEngineTests`'s two new tests (bucket by id,
file by id across a sibling bucket), `VcsRepositoryIsolationTests` (function/site, by connected
repository), and the existing Functions suite unchanged under Finding G's tightened lookup (function, by
execution id). Every one of these runs against a real Postgres and, where a container is involved, a real
Docker daemon — never a mock. No manual click-through of the console was needed or performed: this phase
shipped no new console screens, and the fixes are pure backend authorization logic reachable identically
whether the console or a direct API call issues the request.

## 6. For later phases — and whether a Phase 4 is warranted

This is the last phase in the sequence Phases 1 and 2 didn't already claim, and the prompt's own
non-goals section says as much: nothing turned up here that is a fourth phase's worth of dedicated work
rather than a documented, accepted risk.

- **A genuine fix for Finding E's TOCTOU and the deeper installation-ownership gap** — worth doing if
  git-integration multitenancy is ever actually built, not before. Shape: record which `VcsInstallation`
  a site/function's repository connection was validated against, and require the *pushing* installation
  id (already parsed, currently discarded — Finding K) to match at dispatch time, replacing the current
  bare string-equality match entirely.
- **A rotating/expiring `PRAXY_FUNCTION_API_KEY`** (Finding M) — the same fresh-per-invocation delivery
  Phase 1 considered and deliberately deferred for `PRAXY_FUNCTION_JWT` (a wire-contract change across
  both wrapper languages, harder still for Dart's read-only `Platform.environment`) would need the same
  treatment here, or at minimum a rotation/expiry knob and a `LastUsedAt` surfaced in the console's
  Platform Access UI so a stale, unrevoked grant isn't invisible to the operator who granted it.
- **A shared decision abstraction for Finding L** — not urgent (all three copies agree today, and each
  has its own direct unit tests), but the cheapest real fix already sketched during this phase: a small
  parity/golden test asserting `FileAccessRules.Resolve`, `QueryCompiler.PermissionPredicate`, and
  `FunctionsService.CanExecute` agree on a shared table of (bypass, grant, caller-roles) inputs would
  catch future drift without requiring any of the three to actually change shape. Not added this phase —
  designing a fair common input shape across a raw-SQL predicate, a C# enum decision, and a simpler
  boolean check is more than a "cheap and clearly right" fix, even though it isn't a full redesign either.
- **This review initiative is complete.** Phases 1 (container boundary), 2 (HTTP edge), and 3
  (authorization/isolation) have each shipped their own fixes and this report's §5 is the durable,
  standalone prerequisite list `docs/research/security-review.md` said this phase would produce. No
  further phases are scoped or needed unless a future feature (real multitenancy, a new subsystem)
  reopens this kind of review deliberately.

## Commands

No new configuration this phase — every fix is a pure logic/query change (`SiteContainerRegistry`'s
mandatory site-scoping, `ConnectionRegistry`'s bypass-resource filtering, the two `ConnectRepositoryAsync`
cross-project checks, `FunctionExecutionService`'s JWT-lifetime computation and defensive project filter,
`TeamEndpoints.DeleteMembership`'s check ordering) plus one new error type
(`ErrorTypes.VcsRepositoryAlreadyConnected`). No new `Praxy:*` knob, no changed default, no new runtime
dependency, no EF migration.
