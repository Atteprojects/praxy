# Phase 6 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run end to end against a
fresh, isolated throwaway Praxy instance (same pattern Phase 5 used) — register hook → local echo
server → create row → delivery logged with a byte-verified signature → point a hook at a dead
endpoint → watch three retries with visible backoff spacing land in a local HTTP listener → delivery
log shows all three `HTTP 500` attempts → subscription auto-disables with a console warning banner
→ re-enable clears it → redeliver from the console produces a fresh, successful delivery. 341 .NET
tests green (253 unit + 88 integration, up from 300 total in Phase 5 — 41 new). Console `tsc -b &&
vite build` clean.

## What shipped

**`src/Praxy.Webhooks`** (new project — `Praxy.Core` + `Praxy.Persistence` + `Praxy.Events` +
`Praxy.Realtime` references, no new NuGet packages):

- `WebhookOutboxDispatcher` (`BackgroundService`): claims un-dispatched `praxy.events` rows with
  `FOR UPDATE SKIP LOCKED` — mirrors `SchemaJobRunner`'s claim shape, but does the claim, the
  per-subscription `WebhookDelivery` inserts, and the `dispatched_at` stamp inside **one** EF
  transaction (`db.Database.BeginTransactionAsync`), so a crash mid-dispatch leaves the event
  exactly "not yet dispatched" rather than partially fanned out. Matching reuses
  `Praxy.Realtime.ChannelGrammar.ExpandEventNames` verbatim — no second wildcard matcher.
- `WebhookDeliveryWorker` (`BackgroundService`): claims `webhook_deliveries` due for attempt (same
  `FOR UPDATE SKIP LOCKED` shape), signs and POSTs one attempt, logs a `WebhookDeliveryAttempt` row,
  and either reschedules with full-jitter exponential backoff (`WebhookBackoff.Compute`,
  `uniform(0, min(cap, base·2^(attempt-1)))`) or finalizes the delivery as terminally `failed` past
  `MaxAttempts` and registers the subscription's consecutive-failure count, auto-disabling it past
  `DisableAfterConsecutiveFailures` — loud (`DisabledReason` + console banner), never a silent stop.
  A `WebhookDeliverySignal` (same `SemaphoreSlim(0,1)` shape as `SchemaJobSignal`) wakes the worker
  immediately after dispatch or a redeliver, instead of waiting a poll tick.
- `SsrfGuard`: a `SocketsHttpHandler.ConnectCallback` that resolves DNS itself and connects directly
  to the resolved address (not the hostname) after rejecting private/loopback/link-local/multicast
  ranges — closes the DNS-rebinding TOCTOU gap a validate-then-let-the-stack-connect check would
  leave open. `AllowPrivateNetworkTargets` (config, default `false`) is the self-host escape hatch
  for reverse-proxied internal targets. `AllowAutoRedirect = false` on the same handler is the whole
  of "no redirects followed cross-origin."
- `WebhookSignature`: `v1=<hex HMAC-SHA256(timestamp + "." + body)>`, a separate
  `X-Praxy-Webhook-Timestamp` header — Stripe's scheme, per research/appwrite-api.md's decision.
  Delivery headers: `X-Praxy-Webhook-{Id,Events,Name,ProjectId,Signature,Timestamp}`. `Events`
  carries the subscription's own pattern(s) that matched (not just the concrete type), so a receiver
  subscribed to several patterns can tell which one fired.
- `WebhookSubscriptionsService` / `WebhookDeliveriesService`: console-facing CRUD, list/get/redeliver.
  Redeliver **clones** a fresh `WebhookDelivery` row (`RedeliveredFromId` pointer back to the
  original) rather than mutating history — the original's attempt log stays exactly as it was.
- `WebhookOptions`: every knob configurable from `Praxy:Webhooks:*` config (same plain-record
  pattern as `SchemaJobRunnerOptions`/`RealtimeOptions`) — `DispatchPollIntervalSeconds` (2),
  `DeliveryPollIntervalSeconds` (2), `TimeoutSeconds` (15), `MaxAttempts` (10), `BackoffBaseSeconds`
  (1), `BackoffCapSeconds` (300), `DisableAfterConsecutiveFailures` (10),
  `AllowPrivateNetworkTargets` (false), `MaxResponseBodyCaptureBytes` (8192).

**Persistence**: `WebhookSubscription`, `WebhookDelivery`, `WebhookDeliveryAttempt` entities;
`OutboxEvent.DispatchedAt` (nullable, indexed) added as the dispatcher's claim marker. Migration
`20260816163802_Webhooks`.

**`src/Praxy.Api`**: `WebhookEndpoints.cs` under `/v1/console/projects/{projectId}/webhooks` — list/
create (returns `{webhook, secret}`, secret shown exactly once)/get/patch (partial: name/url/events/
enabled)/delete, plus `/{id}/deliveries` list/get (payload + attempt log)/`/deliveries/{id}/redeliver`.
Same operator-filter chain and audit-log convention as `ConsoleAuthAdminEndpoints`. `webhooks` flipped
`true` in `/v1/console/capabilities`. `Program.cs` wires the typed `WebhookHttpClient` (mirrors
`GoogleOAuthProvider`'s typed-client shape — no new package needed for `IHttpClientFactory`) with the
SSRF-guarded handler, both hosted services, and the options/signal singletons.

**Console**: `WebhooksPage` (list, disabled-subscriptions warning banner, create modal with an event
picker + reveal-once secret display identical in shape to the Phase 1 API-key flow, enable/disable/
delete row actions) and `WebhookDeliveriesPage` (`<DataGrid />` of deliveries, a `<Sheet />` per
delivery with raw JSON payload, the full per-attempt log, and a Redeliver button gated to
terminal-status deliveries). Both wired into `router.tsx` and gated behind `features.webhooks` in
`ProjectLayout.tsx`'s nav, same pattern as every prior phase's screens.

## Deviations & notes

- **Webhooks currently fire only on row events — this is a real, load-bearing scope boundary, not
  an oversight.** `praxy.events` (the durable outbox the Phase 6 dispatcher reads) is written
  exclusively by `RowsService.WriteOutboxAsync` (confirmed by grep, not assumed: it's the only
  `db.Events.Add` call in the codebase). Every other event-emitting endpoint
  (`ConsoleAuthAdminEndpoints`'s user/team/session/membership actions) calls `IEventBus.PublishAsync`
  directly — the in-process bus Phase 4's realtime consumes, never the outbox. A webhook subscribed
  to e.g. `users.*.create` would silently never fire. The console's event picker therefore only
  offers the three row-event presets (`databases.*.tables.*.rows.*.{create,update,delete}`); the
  "add a custom pattern" field still accepts anything grammar-valid for narrowing to one database/
  table, but nothing outside row events is offered as a preset because nothing outside row events is
  currently deliverable. **Read this before starting Phase 7**: Functions' event triggers are
  specified to share "one event vocabulary" with webhooks/realtime (architecture.md §7) — if triggers
  need non-row events, broadening which write paths call `WriteOutboxAsync`-equivalent is a
  prerequisite, not a Phase 7 detail.
- **Two hosted services, not one.** The prompt described "an outbox consumer"; this shipped as a
  dispatcher (outbox → per-subscription delivery rows, transactional) plus a worker (delivery rows →
  HTTP attempts, retriable). Splitting them means a slow/backed-off delivery never blocks the
  dispatcher from processing the next event, and each has its own claim/poll loop mirroring
  `SchemaJobRunner`'s already-verified shape rather than inventing a combined one.
- **Signing secrets are stored in plaintext, not hashed.** Unlike every other secret in this
  codebase, a webhook secret must be usable to compute a *new* HMAC on every delivery — a one-way
  hash can't do that. Full reasoning and the alternative considered (a project-key encryption layer
  that doesn't exist yet anywhere in the codebase) is written up in
  `docs/research/dotnet-stack.md`'s new "Webhook delivery" section. Reveal-once still happens at the
  API response layer, same UX as API keys.
- **No new NuGet packages.** `Microsoft.Extensions.Http.Resilience`/Polly was considered and skipped
  — it has no SSRF concept and the full-jitter backoff formula was three lines to hand-roll, already
  specified for the realtime reconnect in research/flutter-sdk.md. `IHttpClientFactory` access in
  `Praxy.Webhooks` uses the same zero-extra-package typed-client trick `GoogleOAuthProvider` already
  established (the concrete `HttpClient` is a BCL type; only `Praxy.Api`, which already has the Web
  SDK's shared framework, needs `AddHttpClient<T>`).
- **The delivery body is an envelope, not the raw outbox payload.** `{event, timestamp, projectId,
  data}` — `data` is the same `{databaseId, tableId, rowId, roles}` shape realtime and the outbox
  already carry (row events include no row body, matching Phase 4/5's documented behavior). Appwrite
  doesn't wrap its webhook body this way; the wrapper was judged worth the divergence so a receiver
  doesn't have to cross-reference headers to know what fired.

## Known gaps (deliberate, next phases or later)

- **Auto-disable counts terminally-failed *deliveries*, not raw HTTP failures.** A delivery that
  succeeds on its 9th of 10 attempts resets the counter to zero — "consecutive" means consecutive
  deliveries that each exhausted their own retry budget, not consecutive HTTP calls. Matches the
  wording of the roadmap's requirement; worth a second look if real-world usage wants a stricter
  definition.
- **SSRF guard's IP-range table is hand-written, not a maintained library.** Covers RFC1918, loopback,
  link-local (incl. the cloud-metadata address), multicast/reserved, and IPv6 unique-local/link-local
  — unit-tested (`SsrfGuardTests`) against the ranges that matter for this threat model, but a full
  security-review pass (roadmap Phase 9) should verify it against the threat-model table item by item
  like everything else gets at that point.
- **No cleanup/retention job for `webhook_delivery_attempts`/old `webhook_deliveries`.** Rows
  accumulate forever today, same as `praxy.events` and `praxy.audit_log` already do — a retention
  policy is a Phase 9 hardening concern across all three, not specific to webhooks.
- **The persistent dev stack (`praxy-dev-pg`/:5090/:5173) had its API and console **processes**
  restarted this phase** (stale processes from an earlier session, picked up the new build/migration)
  but its **data was never touched** — the actual owner-test click-through ran against a separate,
  fully isolated throwaway Postgres container + a second API instance + a temporarily-repointed
  console dev server, torn down afterward (`console/vite.config.ts`'s proxy-target edit reverted;
  `git diff` on it is empty). Same isolation discipline Phase 5 used.

## Tests

`tests/Praxy.Tests.Unit`: `WebhookSignatureTests` (sign/verify round-trip, wrong secret/timestamp/
body all fail closed, malformed signature doesn't throw, cross-checked against a hand-computed
HMAC-SHA256 independent of the implementation under test), `WebhookBackoffTests` (every draw within
`[0, min(cap, base·2^(attempt-1))]`, capped correctly at large attempt numbers, not lockstep across
32 draws), `SsrfGuardTests` (every private/loopback/link-local/multicast/unique-local range from the
threat model blocked, public addresses and just-outside-the-range boundaries not blocked, IPv4-mapped
IPv6 loopback blocked, `ConnectAsync` throws before ever attempting a socket when every candidate is
blocked). `ErrorTypesTests` (pre-existing) automatically covers the three new `Webhook*` error
constants.

`tests/Praxy.Tests.Integration/WebhookDeliveryTests.cs`: a real loopback `HttpListener` the delivery
worker's real `HttpClient`/`SocketsHttpHandler` connects to over an actual socket (SSRF guard relaxed
via `AllowPrivateNetworkTargets` — the exact escape hatch it exists for) — no ASP.NET Core in-memory
transport involved for the outbound leg. `Owner_test_flow_delivers_and_the_signature_verifies`:
register → row create → delivery headers/signature/payload all asserted against the real captured
wire bytes, secret never re-echoed by GET, redeliver produces a distinct delivery id pointing back at
the original. `Dead_endpoint_retries_then_the_subscription_auto_disables`: exactly `MaxAttempts`
real HTTP 500 attempts land, delivery finalizes `failed`, subscription auto-disables with a reason.
`Invalid_url_and_empty_events_are_refused_at_creation`: validation 400 with field errors.

## Commands

No changes to CLAUDE.md's Commands section — webhooks run automatically as part of the existing
`dotnet run --project src/Praxy.Api` / `npm run dev --prefix console` dev commands (the two new
hosted services start with the API, same as `SchemaJobRunner`/`RealtimeHub` already do) and
`dotnet test` already picks up the new test files. Self-host operators can tune delivery behavior via
the `Praxy:Webhooks:*` config keys listed above (all optional, sensible defaults).

## Owner-test checklist (run by this session, all passing)

Run against an isolated throwaway instance (fresh Postgres container on a scratch port, a second API
instance on `:5091`, the console dev server temporarily repointed at it), driving the real console UI
in the Browser pane:

1. **Register a webhook against a local echo server** — created "Local echo" via the console's
   `+ Create webhook` (URL `http://127.0.0.1:8765/hook`, "Row created" preset checked), the
   reveal-once secret modal matched the API-keys flow exactly, extracted the exact secret text via
   DOM read (a screenshot alone visually truncated the `<pre>` box — noted as a verification-method
   lesson, not a product bug).
2. **Create a row → delivery logged, signature verifies** — `POST .../rows` via curl; the echo
   listener received the request within ~1s carrying all six `X-Praxy-Webhook-*` headers; the
   signature verified byte-exact against the raw captured wire body (the "+00:00" UTC offset is
   emitted as `+` by `System.Text.Json`'s default encoder — a hand-retyped body string won't
   match, the raw captured bytes must); the console's delivery Sheet showed `succeeded`, the pretty-
   printed payload, and one `HTTP 200` attempt.
3. **Point a webhook at a dead URL → watch retries/backoff** — "Dead endpoint" webhook against a
   second local listener that always returns 500 (`MaxAttempts=3`, tightened backoff for a fast
   owner-test loop); exactly three real HTTP attempts landed with visible backoff spacing (17:30:59,
   :31:00, :31:01); the delivery log showed all three `HTTP 500` attempts with the captured error
   text; the subscription auto-disabled (`DisableAfterConsecutiveFailures=1`) with a console warning
   banner ("Dead endpoint" was disabled automatically…") and a "disabled" badge, not a silent stop.
4. **Redeliver from the console** — clicked "Redeliver" on the first webhook's succeeded delivery;
   response confirmed a new, distinct delivery id pointing back at the original; the echo server
   received a fresh signed request; the new delivery reached `succeeded`.
5. **Re-enable a disabled webhook** — clicked "Enable" on "Dead endpoint"; the warning banner and
   badge cleared immediately.

Also verified: `dotnet build`/`dotnet test` (341/341) and `npm run build --prefix console` (`tsc -b
&& vite build`) both clean; the throwaway Postgres container, API process, and echo servers were torn
down afterward; `console/vite.config.ts`'s temporary proxy-target edit was reverted (`git diff` on it
is empty); the persistent dev stack's Postgres data was never touched (only its stale API/console
*processes* were restarted to run the new build).

## Next: Phase 7

The outbox now has a real consumer — webhooks deliver, retry, log, and auto-disable, verified against
real sockets and a real console. The prompt below is ready to paste into a fresh session.
