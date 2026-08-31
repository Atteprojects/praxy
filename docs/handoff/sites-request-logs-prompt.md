# Session task — Sites per-request logs

## Why this exists

A self-host comparison against Appwrite (2026-08-30, informal research session) found Appwrite's Sites
detail view has a dedicated "Logs" tab showing real per-request runtime/access logs (Method, Duration,
Created), described in-product as "automatically generated based on your site's activity." Praxy's
Sites has no equivalent — `SiteProxyMiddleware` (`src/Praxy.Sites/SiteProxyMiddleware.cs`) forwards every
request through YARP's `IHttpForwarder` directly to a site's container and records nothing about it.
`FunctionExecution` gives Functions this kind of observability for invocations; Sites traffic (arbitrary
end-user HTTP requests to a hosted app) has no analogue at all today.

Read `SiteProxyMiddleware.cs` in full — both the production and preview dispatch paths — before writing
anything; this task adds an observation hook to that middleware, not a new routing path. Also read
`docs/handoff/ci-and-retention-prompt.md` and `src/Praxy.Api/Infrastructure/RetentionSweeper.cs`/
`RetentionOptions.cs` before designing the new table — see the landmine below on why this is not
optional here. Work on a new branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **No request/response body capture.** Sites traffic is arbitrary end-user web traffic, not an
  explicitly invoked function call — capturing bodies here is a much larger privacy and storage-volume
  concern than `FunctionExecution.RequestBody`/`ResponseBody` already accepted for Functions. Metadata
  only: method, path, status code, duration, timestamp, response size if cheap to capture.
- **No tracing/correlation-ID UI, no log search/filtering beyond simple pagination** — Appwrite's own
  Logs tab is a plain table; match that bar, not more.
- **No analytics/dashboards** (request-rate charts, status-code breakdowns). A raw paginated list is the
  whole scope.
- **No changes to `SiteProxyMiddleware`'s actual routing/forwarding decisions** — production-path
  container resolution via `SiteContainerRegistry`, the preview cold-start path, custom-domain
  resolution — all unchanged. This task only adds something that observes the outcome, on the response
  path, without changing what gets served or how.

## Scope

1. **New `site_requests` table** (EF migration from `src/Praxy.Persistence`): `id, site_id, project_id,
   deployment_id, method, path, status_code, duration_ms, created_at`. Keep it minimal — this table will
   be high-volume (every request to every deployed site), so don't add columns beyond what the console
   table needs to render.
2. **Write path in `SiteProxyMiddleware`**: after YARP's forward call completes (success or not), record
   one row. **Do not block the response on a synchronous DB write** — this sits directly in every
   request a real visitor makes to a hosted site, unlike `FunctionExecution` rows which are written from
   a background worker off an explicitly invoked, already-async execution path. Use a bounded in-memory
   channel written to from the middleware and drained by a small background worker that batches inserts
   — mirroring the claim/background-processing shape already established by `FunctionExecutionWorker`,
   adapted for a producer/consumer channel instead of a `FOR UPDATE SKIP LOCKED` poll (there's no
   "pending work" row to claim here; the request itself is the event). If the channel is full, drop the
   log line rather than block or grow unbounded — this is observability, not a durability-guaranteed
   record, and Sites serving traffic must never be slowed down or failed by logging pressure.
3. **Console**: a new "Logs" tab on the site detail view (alongside Overview/Deployments/Settings),
   structurally mirroring `FunctionExecutionsPage.tsx`'s paginated `DataTable` — Method, Path, Status,
   Duration, Created columns.

## Landmines — read before writing code

- **This table must be retention-eligible from day one — do not repeat the `function_executions`
  deferral.** `docs/handoff/ci-and-retention-prompt.md` explicitly and knowingly left
  `function_executions` out of retention scope because nothing forced the question yet. Sites request
  volume (every HTTP request to every deployed site, unconditionally) will likely dwarf function
  invocation volume, so leaving `site_requests` unbounded by default is not a safe repeat of that call.
  Wire this table into the existing `RetentionSweeper`/`RetentionOptions`
  (`src/Praxy.Api/Infrastructure/`) with a short default window, documented in `docs/self-host.md`'s
  config table the same way the other `Praxy:Retention:*` rows are.
- **Decide explicitly whether to log every request or sample under load, and say so in the report.**
  Logging literally every request is the simplest correct starting point and matches what Appwrite
  appears to do, but if you find write volume becoming a real concern during testing, a config knob
  (`Praxy:Sites:RequestLogSampleRate` or similar, following the "every limit is configurable" cross-phase
  rule) is a reasonable follow-up — don't silently pick a sampling strategy without flagging it.
- **The write path shares the request/response lifecycle with YARP's forwarder** — make sure the
  duration measurement wraps the actual forward call, not the whole middleware invocation (which would
  also include this task's own bounded-channel write, if that write is somehow made synchronous by
  mistake).

## Tests

`tests/Praxy.Tests.Integration/` — a new `SiteRequestLogTests.cs` (real Docker daemon, a live site
container): a handful of requests through the middleware produce matching rows in `site_requests` with
correct method/status/duration, and the retention sweep actually prunes rows past the configured window.

## Done means

- `dotnet test` green (real Docker daemon).
- `tsc -b && vite build` clean.
- Owner click-tests visiting a live deployed site a few times and watching entries appear in the new Logs
  tab, and confirms Sites traffic itself doesn't visibly slow down with logging enabled.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/sites-request-logs-report.md`, stating the retention window chosen and whether
  sampling was needed.
