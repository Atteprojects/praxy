# Sites per-request logs — report

**Status: complete.** Every item in `docs/handoff/sites-request-logs-prompt.md`'s scope shipped.
`dotnet test` green — **380/380 unit, 218/218 integration** (real Postgres via Testcontainers, real
Docker daemon throughout, including two new tests for this feature). Console `tsc -b && vite build`
clean. Owner-tested against the live local dev instance: visited a real deployed site's URL a few times
and watched matching entries appear in the new Logs tab within seconds.

## What shipped

**Database** (migration `20260831152351_SiteRequestLogs`,
[`Entities/Sites.cs`](../../src/Praxy.Persistence/Entities/Sites.cs)): new `site_requests` table — `id,
site_id, project_id, deployment_id, method, path, status_code, duration_ms, created_at`. Metadata only,
per the prompt's non-goals — no request/response bodies. Two indexes: `(site_id, created_at)` for the
console's own "this site's requests, newest first" query, and a standalone `created_at` for the
retention sweep's global age scan — a composite starting with `site_id` wouldn't serve that second
query, and this table's expected volume made a second index worth it in a way the prompt's own
comparison to `audit_log` (which only has the composite) flagged as a real difference, not an
oversight to copy.

**`Praxy.Sites`** — the write path, exactly the shape the prompt asked for:
- [`SiteRequestLogWriter.cs`](../../src/Praxy.Sites/SiteRequestLogWriter.cs): a bounded
  `Channel<SiteRequestLogEntry>` (`Praxy:Sites:RequestLogChannelCapacity`, default 10,000,
  `BoundedChannelFullMode.DropWrite`). `TryEnqueue` never blocks and never throws — a full channel
  silently drops the entry, per the prompt's "never block or fail real site traffic over logging
  pressure."
- [`SiteRequestLogWorker.cs`](../../src/Praxy.Sites/SiteRequestLogWorker.cs): a `BackgroundService`
  that waits for at least one channel entry, drains up to 500 more that are immediately available,
  and flushes the batch as one `AddRange` + `SaveChangesAsync`. Not a claim-based worker like
  `FunctionExecutionWorker` — there's no "pending work" row to claim, the request itself is the event,
  already in memory the instant it's enqueued.
- [`SiteProxyMiddleware.cs`](../../src/Praxy.Sites/SiteProxyMiddleware.cs): `ForwardToContainerAsync`
  (the one method both the production/preview path and the custom-domain path already funneled
  through) now wraps the actual `forwarder.SendAsync` call in a `Stopwatch` and enqueues a log entry
  afterward — success or failure, matching the prompt's "after YARP's forward call completes (success
  or failure)." The three early-rejection paths (site not deployed, preview cold-start failure/timeout,
  quota exceeded) are deliberately **not** logged — nothing was actually served for a log line to
  describe, and the prompt's own wording ties this to the forward call completing, not to every
  possible response this middleware can produce.

**`Praxy.Api`**: `GET /v1/console/projects/{projectId}/sites/{siteId}/requests` — same limit/offset
pagination shape as `FunctionEndpoints.ListExecutions`, newest first
(`SitesService.ListRequestsAsync`). `docs/openapi/v1.json` regenerated (one new operation, two new
schemas).

**Console**: new "Logs" tab on the site detail view (`SiteLogsPage.tsx`, between Deployments and
Settings), a plain paginated `DataGrid` — Method/Path/Status/Duration/Created, status color-coded
(mint/amber/coral by range) mirroring `FunctionExecutionsPage.tsx`'s own badge convention. No
drill-down/sheet — matches the prompt's "a plain table... not more." Polls every 5s
(`useSiteRequests`), same cadence as the other Sites list hooks.

**Retention** (`RetentionOptions.cs`/`RetentionSweeper.cs`): `site_requests` wired in from this
session, not deferred — `Praxy:Retention:SiteRequestsMaxAgeDays` defaults to **7 days**, sharply
shorter than the 90-day default every other retention window uses. Documented in
`docs/self-host.md`'s config table and a new "Sites request logs" subsection.

## The sampling question (landmine — answered)

**Chose to log every request, not sample.** Reasoning, per the prompt's instruction to decide
explicitly and say so:
- This is the simplest correct starting point and matches what Appwrite's own Logs tab appears to do.
- The bounded channel already provides graceful degradation under real overload (drop, don't block or
  crash) with zero sampling logic — there's no evidence yet that write volume is a real problem this
  early, and adding a sampling knob speculatively would be tuning against a load pattern nobody has
  observed.
- If a self-hoster's sustained traffic does outpace `SiteRequestLogWorker`'s drain loop enough to matter,
  the channel capacity knob (`Praxy:Sites:RequestLogChannelCapacity`) and a possible future
  `Praxy:Sites:RequestLogSampleRate` are both real, scoped follow-ups — not designed here, since nothing
  currently motivates the extra complexity.

## Deviations & notes

- **Duration measurement wraps only `forwarder.SendAsync`**, not this method's own logging call after
  it — the prompt's own landmine. Verified by inspection: the `Stopwatch` starts immediately before the
  forward call and stops immediately after, before `requestLog.TryEnqueue(...)` runs.
- **`resolvedDeploymentId` is a new local in `SiteProxyMiddleware.InvokeAsync`**, not a change to
  `RunningSiteContainer` — that record deliberately doesn't carry a deployment id (it's the registry's
  dictionary key, not part of the value), so the production/preview branches each now assign a
  `Guid resolvedDeploymentId` declared outside both, mirroring the existing `activeId`/`deploymentId`
  pattern-variable idiom already used for `running` itself. No behavior change, pure bookkeeping.
- **No new `Id` needed on `RunningSiteContainer` or the registry** — confirmed while reading the file
  that every call site already knows its own resolved deployment id at the point it calls
  `ForwardToContainerAsync`; the prompt's "no changes to routing/forwarding decisions" held exactly.

## Known gaps (out of scope, noted for whoever picks them up)

- **No batched-delete cap on the retention sweep's `site_requests` query.** `ExecuteDeleteAsync` with a
  plain age filter, same as the other three tables — consistent with existing `RetentionSweeper`
  precedent, but this table is the one most likely to accumulate a very large backlog if
  `Praxy:Retention:SiteRequestsMaxAgeDays` is raised on a busy instance or the sweep is paused for a
  while. Not attempted here since the existing sweeper doesn't batch any of its other three deletes
  either — flagging it as a shared follow-up for all four, not a `site_requests`-specific gap.
- **A dropped log entry (full channel) is silent** — no counter or log line records how many entries
  were dropped. Worth adding if `Praxy:Sites:RequestLogChannelCapacity` ever needs real tuning data to
  size correctly; skipped here since nothing today would consume that signal.

## Tests

`tests/Praxy.Tests.Integration/SiteRequestLogTests.cs` (new, real Docker daemon):
- `Proxied_requests_produce_matching_site_request_log_rows` — deploys a minimal real site, waits for it
  to actually be running (not just `ready`), issues two real proxied requests (one `200`, one `404`),
  and polls the new `GET .../requests` endpoint until matching rows appear — proves the full pipeline
  (middleware → channel → worker → DB → API), not just that a row eventually exists somewhere.
- `Old_site_request_rows_are_pruned_by_retention_a_recent_one_is_not` — same seed-directly-via-EF-then-
  poll-the-database pattern `RetentionTests.cs` already established for the other three tables.

Full-repo `dotnet test`: **380/380 unit, 218/218 integration** (210 pre-existing + the 2 new above,
`Praxy.Tests.Integration`'s total that had grown to 216 as of the last-merged PR).

## Commands

New config, all documented in `docs/self-host.md`'s table and `CLAUDE.md`'s Sites paragraph:
- `Praxy:Sites:RequestLogChannelCapacity` (default 10,000)
- `Praxy:Retention:SiteRequestsMaxAgeDays` (default 7)

## Owner-test checklist

Done by me this session against the local dev instance (`api`/`console` launch configs,
`owner@test.local`):

- Opened an existing live site's console page — confirmed the new "Logs" tab appears between
  Deployments and Settings.
- Visited the site's real public URL directly (`http://<key>.<projectId>.sites.localhost:5090`) a few
  times, including a path that 404s.
- Opened the Logs tab and confirmed matching rows appeared within seconds: correct methods, correct
  paths (including the 404 one), correct status codes (color-coded — mint for `200`, amber for `404`),
  and non-negative durations.
- Confirmed the site itself kept responding normally throughout — no observable slowdown from logging.

## Next

No further prompt is written. This closes out all four findings from the 2026-08-30/31 Appwrite
self-host comparison (`docs/research/appwrite-comparison-decisions.md` has the full decision log across
all four). The two open threads that document already flagged — a `PRAXY_ENDPOINT`-equivalent for
functions to actually use their injected credentials, and whether `site_requests`' retention delete
ever needs batching under real load — are both real, standalone candidates for a future session, not
picked up here.
