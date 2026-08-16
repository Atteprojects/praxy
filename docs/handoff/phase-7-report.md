# Phase 7 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run end to end against an
isolated throwaway instance (fresh Postgres container on a scratch port, a second API instance on
`:5095`, the console dev server temporarily repointed at it — same isolation pattern Phases 5/6 used),
driving the real console UI in the Browser pane: deploy from console → invoke sync (saw the response
and logs) → trigger via row create (event execution appeared) → async execution shows its stored
output → failed build shows its log. 358 .NET tests green (268 unit + 90 integration, up from 341
total in Phase 6 — 17 new: 15 unit across `FunctionRuntimesTests`/`RuntimeTemplatesTests`, 2
integration in `FunctionTests`). Console `tsc -b && vite build` clean.

## What shipped

**`src/Praxy.Functions`** (new project — `Praxy.Core` + `Praxy.Persistence` + `Praxy.Events` +
`Praxy.Realtime` + `Praxy.Auth` references, `Docker.DotNet.Enhanced` 4.3.3 + `Cronos` 0.13.0 as the
only new NuGet packages):

- `DockerExecutor`: thin wrapper over four verbs — build an image, start/stop a container, invoke over
  HTTP. Talks to the running function container via its published loopback port with a plain
  `HttpClient`, not the Docker attach/exec API, so per-invocation latency doesn't pay for Docker's own
  stream framing. Build streams its log via the raw NDJSON response stream (not the library's
  callback-based overload — see Deviations, that overload hangs).
- `RuntimeTemplates`: builds the actual Docker build context — the uploaded tar plus a generated
  Dockerfile and a generated minimal HTTP wrapper server, using `System.Formats.Tar` (BCL, no
  package) for both reading and writing. Dart's wrapper is generated fresh per build (Dart has no
  dynamic `require`, so the entrypoint has to be a static `import`); Node's wrapper is a fixed file
  that reads `PRAXY_ENTRYPOINT` from the environment at runtime.
- `WarmPool`: keeps recently-used containers alive across invocations keyed by deployment id, LRU-
  bounded (`WarmPoolSize`) and idle-swept (`MaxIdleSeconds`, via `FunctionPoolSweeper`). Implements
  `IAsyncDisposable` so a graceful shutdown stops every warm container instead of leaking them.
  `FunctionsService.IsWarm`/the function response's `isWarm` field make a cold pool visible in the
  console rather than a silent latency surprise.
- `FunctionBuildWorker` (`BackgroundService`): claims queued `FunctionDeployment` rows
  (`FOR UPDATE SKIP LOCKED`, same shape as `SchemaJobRunner`/`WebhookDeliveryWorker`), builds, flushes
  the build log into the row roughly once a second while building, and on success auto-activates the
  new deployment (evicting the previous active deployment's warm container so the next invocation runs
  the new code) — plus an explicit `/activate` endpoint on any `ready` deployment for rollback.
- `FunctionExecutionService`: the one place that actually invokes a function — resolves the active
  deployment, decrypts env vars (`InstanceKey`, see Deviations), mints a scoped user JWT when the
  triggering caller was a specific app user, calls the container, and always finalizes a queryable
  `FunctionExecution` row.
- `FunctionExecutionWorker` (`BackgroundService`): claims `async = true, status = 'waiting'` execution
  rows; sync executions never reach this worker (the invoking request runs `RunAsync` directly and
  finalizes its own row) — the `async = true` claim filter is what keeps the two paths from racing the
  same row.
- `FunctionEventDispatcher` (`BackgroundService`): the outbox consumer for event-triggered functions,
  same claim shape as `WebhookOutboxDispatcher`, reusing `ChannelGrammar.ExpandEventNames` verbatim.
  Claims independently via `OutboxEvent.FunctionsDispatchedAt` — see Deviations for why that's a
  separate column from webhooks' `WebhooksDispatchedAt`, not a shared one.
- `FunctionScheduler` (`BackgroundService`): cron via Cronos, `FOR UPDATE SKIP LOCKED` claim +
  next-occurrence stamp in one transaction so two instances can't double-fire a tick.
- `FunctionsService`: console-facing CRUD for functions, env vars, deployments, executions — the
  delivery/build/execution pipelines never route through it directly, same separation
  `WebhookSubscriptionsService` draws from its workers.

**Persistence**: `FunctionDef`, `FunctionEnvVar`, `FunctionDeployment`, `FunctionDeploymentSource`
(the uploaded tar bytes, kept in its own 1:1 table so a deployment list query never drags a
multi-megabyte blob along — deleted once the build finishes either way), `FunctionExecution` entities;
`OutboxEvent.DispatchedAt` split into `WebhooksDispatchedAt` + `FunctionsDispatchedAt` (see
Deviations). Migration `20260816205131_Functions`.

**`src/Praxy.Api`**: `FunctionEndpoints.cs` — console admin surface under
`/v1/console/projects/{projectId}/functions` (CRUD, env vars, deployments incl. tar upload +
activate, executions incl. manual sync/async invoke) plus the data-plane invocation endpoint
`POST /v1/functions/{functionId}/executions` (app-user sessions, scoped JWTs, and API keys with the
new `functions.execute` scope). `POST /v1/account/jwts` (`AccountEndpoints.cs`) mints the scoped user
JWT research/appwrite-api.md flagged "Phase 1 optional, Phase 7 required". `functions` flipped `true`
in `/v1/console/capabilities`. `Program.cs` wires `FunctionsOptions` (every knob configurable under
`Praxy:Functions:*`), the singleton `DockerExecutor`/`WarmPool`, and all five hosted services.

**`src/Praxy.Auth`**: `AccountJwtService` (mint/verify, reuses `CompactJwt` + `InstanceKey.SigningKey`
exactly as `OAuthService` already does). `RequestPrincipal.JwtUser` — a new principal case for a
caller authenticated with a JWT instead of a session; `RoleResolver` resolves it to the same roles an
`AppUser` would get, but `AppPrincipalFilter.RequireUser` deliberately doesn't accept it (session-
management endpoints — list/delete sessions, change password — still require a real session; a
stateless JWT has no session to reference). `ApiKeyScopes.FunctionsExecute` added, following the same
per-feature-scope convention every prior data-plane feature uses.

**Console**: `FunctionsPage` (list + create modal: key/name/runtime/entrypoint/timeout/event-trigger
presets/cron), `FunctionDetailHeader` (name/status chips + Deployments/Executions/Settings tabs,
mirrors `TableDetailHeader`), `FunctionDeploymentsPage` (upload via file input, `<DataGrid />` list,
`<Sheet />` with a live-tailing build log while queued/building, Activate button), `FunctionExecutionsPage`
(Run modal for manual sync/async test invokes, `<DataGrid />` list, `<Sheet />` with response
body/logs/errors/cold-start badge), `FunctionSettingsPage` (enabled toggle, entrypoint/timeout,
trigger checkboxes, cron field, env var add/remove with reveal-once-style write-only values, danger
zone). All four wired into `router.tsx`; nav entry gated behind `features.functions` in
`ProjectLayout.tsx`, same pattern every phase since Phase 4 uses.

**`deploy/docker-compose.yml`**: mounts `/var/run/docker.sock` into the api container
(docker-outside-of-docker) with an inline comment on the security implication (root-equivalent host
access) and the escape hatch (comment the line out; Functions becomes unusable, nothing else is
affected).

## Deviations & notes

Six real bugs were found and fixed by actually exercising this phase's code — against real Docker, a
real macOS-produced tar, and the real console UI — rather than by reasoning about the code in the
abstract. Recording all of them because CLAUDE.md asks deviations to carry their *why*, and "we tested
it for real and it broke" is exactly the kind of why worth keeping:

- **The Phase 6 research doc's claim that no project-key encryption layer existed was wrong.**
  `Praxy.Auth.InstanceKey` (Phase 1) already had `Encrypt`/`Decrypt` (AES-256-GCM) and was already in
  live use for `Identity.AccessTokenEnc` — a grep miss in Phase 6's research, not a real gap. Function
  env vars (`FunctionEnvVar.ProtectedValue`) reuse it instead of adding
  `Microsoft.AspNetCore.DataProtection` as originally proposed. Corrected in
  `docs/research/dotnet-stack.md` in place, not silently — the paragraph now explains what was wrong
  and why.
- **`OutboxEvent.DispatchedAt` had to become two columns, not stay one.** Functions is a second
  independent outbox consumer; sharing webhooks' single claim column would mean whichever dispatcher
  claims a row first silently hides it from the other. Renamed to `WebhooksDispatchedAt`, added
  `FunctionsDispatchedAt` — the one place this phase touched `src/Praxy.Webhooks/` (one query's column
  name in `WebhookOutboxDispatcher.cs`), flagged per the phase prompt's explicit instruction to stop
  and flag rather than reach back in casually.
- **`Docker.DotNet.Enhanced`'s "correct" build overload hangs.** The `IProgress<JSONMessage>`-callback
  overload — the one the library's own naming suggests is right, and initially what was written —
  was observed to hang indefinitely on some failed builds, not honoring cancellation, intermittently
  reproducible. Switched to manually parsing the raw NDJSON response stream from the (misleadingly)
  `[Obsolete]`-marked `Task<Stream>` overload instead; full write-up in `docs/research/dotnet-stack.md`.
- **macOS's `tar` breaks Docker builds outright.** Uploading a tar built with macOS's stock `tar`
  failed every build with `lsetxattr ... operation not supported` — `bsdtar` embeds a
  `com.apple.provenance` PAX extended attribute that the Linux side of Docker's context extraction
  rejects. Found by actually uploading a Mac-built tar through the console during the owner test, not
  by inspection. Fixed by having `RuntimeTemplates.BuildContextAsync` re-emit a fresh minimal entry
  (name/mode/data only) per file instead of forwarding the original `TarEntry` object — robust against
  this and whatever the next tar-producing tool's platform-specific quirk turns out to be.
- **`FunctionExecution.TriggeredBy`'s original 128-char cap was too short for event triggers.**
  `event:<full event type>` (three 32-char hex ids plus separators) runs to ~134 chars; the cap
  silently poisoned `FunctionEventDispatcher`'s transaction on every single row event, in an infinite
  retry loop (the failed `SaveChangesAsync` rolled back the claim too, so it re-claimed and re-failed
  on the same event forever) — found by the event-trigger integration test hanging, not by review.
  Raised to 300.
- **A stale-tracked-entity bug in the sync invoke path.** `ConsoleInvoke`/`Invoke` create (and thereby
  EF-track) the execution row, then `FunctionExecutionService.RunAsync` finalizes it via
  `ExecuteUpdateAsync` — a bulk statement that updates the database directly without touching the
  change tracker — all in the same request's `DbContext` scope. Re-reading the row afterward
  (`GetExecutionAsync`) resolved to the *stale* tracked instance (still `status = "waiting"`) via EF's
  identity map, not the fresh database values. Fixed with `.AsNoTracking()` on
  `FunctionsService.GetExecutionAsync`/`ListExecutionsAsync` — execution rows are always read-only
  from this service's perspective, so tracking bought nothing there anyway.
- **`GET /account/roles`'s debug endpoint mislabeled JWT-authenticated callers as `"guest"`** even
  though the underlying `roles` array resolved correctly (`RoleResolver` already had a `JwtUser` case;
  the endpoint's own `principal` switch just didn't). Found while manually verifying the scoped-JWT
  feature works end to end during the owner test. Added the missing switch arm.

Also, by design rather than by bug:

- **Auto-activate on successful build, plus an explicit `/activate` for rollback.** The roadmap's "tar
  upload → build → activate" phrasing could read as three always-manual steps; auto-activating the
  newest successful build (Appwrite's own default) is what the owner-test's "deploy from console →
  invoke sync" expects without an extra click, and the explicit endpoint/button on any `ready`
  deployment still covers reverting to an older build.
- **Event triggers inherit Phase 6's row-events-only scope boundary verbatim.** `praxy.events` is
  still written only by `RowsService.WriteOutboxAsync` (confirmed again this phase, not re-verified
  differently) — a function subscribed to e.g. `users.*.create` will never fire, for the same reason
  a webhook subscribed to it wouldn't. The console's trigger picker only offers the three row-event
  presets, matching `WebhooksPage`'s exact reasoning and copy.
- **The console can't drive a native `<input type="file">` file picker in this session's browser
  automation** (no OS-level dialog to interact with). The owner-test's "deploy from console" step
  therefore uploaded the tar via `curl` against the same session the browser was authenticated with,
  then used the browser to observe every subsequent screen (build log, activation, sync/async invoke,
  event-triggered execution, settings) update from real server state. The upload *endpoint* and the
  console's upload *UI code* are both exercised — `FunctionDeploymentsPage`'s file input, its
  `useCreateDeployment` hook, and the raw-bytes `fetch` all match what a real click-driven upload
  would send — only the literal OS file-picker interaction itself was substituted.
- **No per-function execute permissions.** Any authenticated caller (app user, JWT, or a key with the
  `functions.execute` scope) with project access can invoke any enabled function via the data plane —
  Appwrite has fine-grained function execute-access grants; the roadmap's Phase 7 scope block doesn't
  call this out, so it wasn't invented. Noted as a known gap below, not a silent omission.
- **JWTs satisfy role resolution but not session-management endpoints.** A `RequestPrincipal.JwtUser`
  resolves the same roles as a session would (so a function's SDK calls back into the data plane and
  are correctly authorized as the triggering user) but does not satisfy `AppPrincipalFilter.RequireUser`
  — list/delete sessions and password changes still need a real session. Deliberate scope boundary,
  documented on `AccountJwtService` and `RequestPrincipal.JwtUser` themselves.

## Known gaps (deliberate, next phases or later)

- **No per-function execute permissions** (see above) — a Phase 9 hardening candidate alongside the
  threat-model pass, if real usage wants it.
- **No image/container cleanup job.** Superseded deployment images and stopped containers accumulate
  (Docker layer cache means disk growth is bounded but not zero) — same "no retention job yet" shape
  as `webhook_delivery_attempts`/`praxy.events`/`praxy.audit_log` already carry into Phase 9.
- **Cold-start latency is real and unoptimized.** `ColdStartTimeoutSeconds` (default 60) is generous
  precisely because a cold `docker run` + npm/dart-toolchain boot can take real seconds; the warm pool
  is the only mitigation this phase ships (no pre-warming, no predictive scaling).
- **Dart/Node runtime wrapper contract is Praxy's own design, not open-runtimes' actual per-language
  SDK surface** — see `docs/research/dotnet-stack.md`'s new section for the reasoning. A future phase
  wanting drop-in compatibility with existing open-runtimes function code would need to either adopt
  their SDK surface exactly or ship a compatibility shim.
- **No streaming/binary response support.** Function responses are JSON-envelope text; a function
  that needs to stream bytes (e.g. serve an image) isn't supported yet.

## Tests

`tests/Praxy.Tests.Unit`: `FunctionRuntimesTests` (entrypoint validation — extension matching per
runtime, path-traversal and garbage-string rejection), `RuntimeTemplatesTests` (build-context
generation — Dart's per-build codegen carries the entrypoint into a static `import`, Node's fixed
wrapper never does, health path / runtime port constants agree with the generated wrapper source).
`ErrorTypesTests` (pre-existing) automatically covers the new `Function*` error constants.

`tests/Praxy.Tests.Integration/FunctionTests.cs`: real Docker, real `node:22-alpine` builds, no
stubbing (mirrors `WebhookDeliveryTests`' "no in-memory transport on the outbound leg" discipline,
applied to the Docker leg here). `Deploy_from_console_invoke_sync_and_async_and_trigger_via_row_create`:
deploy → wait for `ready` → sync invoke returns the function's actual response → async invoke returns
immediately then the polled row shows the stored result → a second function with an event trigger
fires on real row creation. `Failed_build_shows_its_log`: a `package.json` that isn't valid JSON makes
the generated Dockerfile's `npm install` RUN step fail — a genuine `docker build` failure, not a
runtime one — and the deployment's `error`/`buildLog` are both populated.

## Commands

New/changed:

- **Functions require a reachable Docker daemon at runtime, not just for tests.** `dotnet run
  --project src/Praxy.Api` now needs `/var/run/docker.sock` (or `Praxy:Functions:DockerEndpoint` /
  `DOCKER_HOST` pointed elsewhere) reachable from the process — already true for anyone who's run
  `dotnet test` (Testcontainers needs it too), but this is the first phase where the running *API*,
  not just its test suite, talks to Docker directly.
- **Self-host (`deploy/docker-compose.yml`) mounts the host's Docker socket into the api container.**
  Docker-outside-of-Docker — the container can now build and run other containers as siblings via the
  host daemon. This is root-equivalent host access from inside the api container; the compose file
  documents this inline and the escape hatch (comment the volume mount out) for operators who decide
  Functions isn't worth that tradeoff on their deployment.
- Self-hosters can tune the whole pipeline via `Praxy:Functions:*` config keys: `DockerEndpoint`,
  `DartBaseImage` (default `dart:stable`), `NodeBaseImage` (default `node:22-alpine`),
  `BuildPollIntervalSeconds`, `ExecutionPollIntervalSeconds`, `SchedulePollIntervalSeconds`,
  `BuildTimeoutSeconds` (600), `ColdStartTimeoutSeconds` (60), `MaxSyncTimeoutSeconds` (30, the
  roadmap's hard cap on sync invocations regardless of a function's configured timeout),
  `WarmPoolSize` (10), `MaxIdleSeconds` (300), `PoolSweepIntervalSeconds` (30), `MemoryLimitMb` (256),
  `CpuLimit` (1.0), `MaxResponseCaptureBytes` (65536), `MaxSourceBytes` (25MB deployment upload cap).
- No other command changes — functions run automatically as part of the existing `dotnet run
  --project src/Praxy.Api` / `npm run dev --prefix console` dev commands (the five new hosted services
  start with the API, same as every prior phase's background workers) and `dotnet test` already picks
  up the new test files.

## Owner-test checklist (run by this session, all passing)

Run against an isolated throwaway instance (fresh Postgres container on a scratch port, a second API
instance on `:5095`, the console dev server temporarily repointed at it — `console/vite.config.ts`'s
proxy-target edit reverted afterward, `git diff` on it is empty), driving the real console UI in the
Browser pane:

1. **Deploy from console** — created "Greeter" (Node runtime) via the console's `+ Create function`
   modal with the "Row created" event preset checked; uploaded a tar (via `curl` against the browser's
   authenticated session — see Deviations for why the literal file-picker click was substituted) that
   first failed with the macOS-`tar`-xattr bug live in the console's build-log Sheet, then, after the
   fix, built and auto-activated successfully — the Deployments tab showed `ready`/`active` with the
   real image tag.
2. **Invoke sync → see logs** — clicked "Run" in the Executions tab; the Execution sheet showed
   `completed`, `GET / HTTP 200`, a `cold start` badge (first invocation), the actual JSON response
   body from the deployed code, and a logs panel (empty — the function didn't log anything, correctly
   reflected as "(no output)" rather than an error).
3. **Trigger via row create** — created a database/table/row via the data-plane API (against the same
   isolated instance); a new execution appeared in the Executions list within ~1s with
   `trigger = event · async`, `completed`, and `Triggered by
   event:databases.<id>.tables.<id>.rows.<id>.create` (this surfaced and fixed the `TriggeredBy`
   length bug, and separately a text-wrapping bug in the detail Sheet — both described in Deviations).
4. **Async execution shows stored output** — ran with "Run asynchronously" checked; the sheet updated
   from the queued row to `completed` with the actual response body once the worker finished, and
   showed no `cold start` badge (the warm container from step 2 was reused, ~6ms).
5. **Failed build shows its log** — covered inline in step 1 (the real, unplanned macOS-tar failure)
   and re-verified explicitly afterward with a deliberately-invalid `package.json`: the deployment
   reached `failed` with a populated `error` and `buildLog`, and the function's `activeDeploymentId`
   stayed absent.

Also verified manually beyond the roadmap's literal checklist, since they're explicit Phase 7 roadmap
lines: **env vars encrypted at rest** (`GREETING` env var set via the Settings screen, never re-shown;
a redeployed function that reads `process.env.GREETING` echoed the correct decrypted value back) and
**scoped user JWT** (`POST /v1/account/jwts` minted a JWT for a signed-up app user; invoking the
function through the *data-plane* endpoint as that user injected `PRAXY_FUNCTION_JWT` — the deployed
code confirmed its presence — and `PRAXY_FUNCTION_USER_ID` matched exactly; the minted JWT correctly
resolved `user:<id>` roles via `GET /account/roles` and, as designed, was correctly refused by
`GET /account/sessions`, confirming the "resolves roles, doesn't satisfy session-management endpoints"
boundary holds).

Also verified: `dotnet build`/`dotnet test` (358/358: 268 unit + 90 integration) and `npm run build
--prefix console` (`tsc -b && vite build`) both clean; the throwaway Postgres container, second API
process, and every `praxy-fn-*` Docker image/container created during testing were torn down
afterward; the persistent dev stack was never touched by any of this session's throwaway resources.

## Next: Phase 8

Functions are real: deployable, invocable sync and async, event- and cron-triggered, with encrypted
env vars and a scoped-JWT identity story for calling back into the data plane. The prompt below is
ready to paste into a fresh session.
