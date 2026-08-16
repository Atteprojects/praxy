# Phase 4 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run end to end — live in a
real browser session (console-to-console, via the realtime inspector this phase built) and via
four dedicated integration tests hitting the real WebSocket protocol against a real
Testcontainers-backed Postgres. 300 tests green (215 unit, 85 integration).

## What shipped

**`Praxy.Realtime`** — the connection manager, channel grammar, and fan-out index the roadmap
called for:

- `Connection` — one WebSocket's state: resolved roles (mutable for lazy revalidation), active
  subscriptions, a bounded outbound `Channel<ReadOnlyMemory<byte>>` (capacity 256, single writer
  task per socket per research/dotnet-stack.md), and `RequestClose` for hub- or overflow-triggered
  shutdown. Subscriptions/roles/index-bookkeeping are mutated only by the connection's own read-loop
  task — the one concurrency rule that lets `ConnectionRegistry`'s per-connection state stay
  lock-free while its cross-connection dictionaries stay `Concurrent*`.
- `ConnectionRegistry` — the project→role→channel→connection index (`_roleIndex`) for O(1)
  permission-filtered fan-out against an event's precomputed `Permissions`, plus a channel-only
  `_bypassIndex`/`_bypassPrefixIndex` pair for bypass connections (operator console, bypass-flagged
  API keys) that see every event on a subscribed channel regardless of role. `MarkForRevalidation`
  (user/team/membership events) and `CloseSession` (`sessions.delete`) are plain scans over live
  connections — a deliberate trade-off, since both are rare paths, not the row-write hot path the
  indexed structures exist for.
- `ChannelGrammar` — channel derivation for a row event (4 variants: table, row, action-suffixed
  table, action-suffixed row), the `account` → `account.<userId>` subscribe-time rewrite, and
  `ExpandEventNames`'s powerset id-wildcarding for the outgoing `events[]` field.
- `RealtimeHub` — the one `IEventBus` consumer for realtime, run as an `IHostedService` so it
  subscribes at startup without anything needing to inject it directly. Fans out row/team/account
  events, closes sockets on `sessions.delete`, flags connections dirty on role-changing events.
- `TicketStore` — in-memory single-use 60s tickets, keyed by opaque token, storing identity
  (`userId`/`sessionId` or `apiKeyId`) rather than a raw secret.

**API** — `GET /v1/realtime?project=<id>(&ticket=<t>)`: caller resolution happens *before*
`AcceptWebSocketAsync` (see Deviations — this is what makes the "early subscribe" race structurally
impossible rather than something to detect). Priority: `X-Praxy-Key` header → `?ticket=` → session
cookie/header → the operator's own console cookie (bypass) → guest. Message-mode protocol
(`ping`/`subscribe`/`unsubscribe` in, `connected`/`response`/`event`/`pong`/`error` out), a 64KB
per-message cap closing with `1003`, and native `.NET` WebSocket keepalive (`KeepAliveInterval`
30s / `KeepAliveTimeout` 20s) for transport-level dead-peer detection — the app-level `ping`/`pong`
messages are handled independently, just JSON echo. `POST /v1/realtime/ticket` mints a ticket for
an existing session or key. A per-project connection quota (`Praxy:Realtime:MaxConnectionsPerProject`,
default 1000) closes the socket with `1013` on overflow, same code as a slow consumer.

**Console** — a realtime inspector (`/project/:id/realtime`, `g r` chord): connects through the
operator's own console session (same-origin cookie, no ticket needed — a browser WS handshake
carries cookies automatically), subscribes to a bypass-only `databases.*` firehose (see Deviations),
shows a live event tail on `<DataGrid />` with a channel/event-name filter and a payload-viewer
`Sheet`. The project overview gets a live connection-count tile (`GET
/v1/console/projects/{id}/realtime/connections`, polled every 5s) linking to the inspector.
`capabilities.features.realtime` flipped to `true`.

## Deviations & notes

- **"Early subscribe before auth settles is queued, not closed" is satisfied structurally, not by
  building a literal queue.** Every input the caller resolution needs — cookie, ticket, key header
  — is already on the HTTP request before the WebSocket upgrade even happens; Praxy fully resolves
  the principal and roles, then calls `AcceptWebSocketAsync`. A subscribe message physically cannot
  arrive before the connection object (and its resolved roles) exist, so there is no race to queue
  around. This is a stronger guarantee than Appwrite's own design, not a weaker one — noted here
  because the prompt asked for this behavior explicitly and it's worth being clear about *how* it's
  met.
- **Bounded outbound channel uses `FullMode = Wait`, not `DropWrite`** (research/dotnet-stack.md's
  snippet uses `DropWrite`). `DropWrite` makes `TryWrite` always report success by silently
  discarding the item being added — which can't signal "this connection is falling behind" back to
  the caller. Since the actual requirement is "close the connection on overflow," not "silently
  drop individual messages and keep going," `Wait` mode is what makes `TryWrite` return `false`
  when full, which `Connection.Enqueue` uses to trigger `RequestClose(Overloaded, …)`.
- **A bypass-only `"<resource>.*"` firehose channel** (e.g. `databases.*`) is a Praxy addition not
  in research/appwrite-api.md's channel grammar. The console inspector needs to see every row event
  project-wide without enumerating every table's concrete channel string up front — impossible with
  concrete-channel-only subscribe. Implemented as a separate prefix index
  (`ConnectionRegistry._bypassPrefixIndex`) that only ever gets populated when `Connection.Bypass`
  is true; a non-bypass connection subscribing to `"databases.*"` gets nothing; `ConnectionRegistryTests`
  proves both directions. Ordinary callers still have no wildcard subscribe — deny-by-default holds.
- **Operator console access reuses the single `/v1/realtime` endpoint** rather than a second
  `/v1/console/projects/{id}/realtime` route. The console's own browser tab carries the
  `praxy_session_console` cookie, checked as a fourth principal branch (after key, ticket, app
  session) with the same org-membership check `ConsoleProjectFilter` uses; a match gets
  `Bypass = true` and skips `IRoleResolver` entirely, mirroring how `ConsoleRowEndpoints` bypasses
  row permissions unconditionally. Keeps architecture.md §6's "one realtime endpoint" literally
  true instead of forking it.
- **A bypass-flagged API key (`ApiKey.BypassRowPermissions`) also bypasses realtime permission
  filtering**, not just row CRUD — "the same way a trusted server integration works" (phase-3
  report's phrasing for the row-CRUD flag) extends naturally to its own subscriptions.
- **`queries?` on a subscribe entry is accepted and stored but never applied as a filter** — matches
  Appwrite's own actual behavior (the field exists in their wire format but isn't used for
  server-side realtime filtering either). Revisit only if a real use case shows up; no owner-test
  step needs it.
- **The `response` server message shape (`{to, subscriptions}`)** isn't fully pinned down by
  research/appwrite-api.md beyond "server→client: … response …". Implemented as an ack of exactly
  which subscription ids were just processed, tagged with `to: "subscribe" | "unsubscribe"`.
- **Revalidation is checked once per received client message**, not on a dedicated timer. In
  practice this is at least as often as the SDK's own ~20s `ping` cadence (research/appwrite-api.md),
  so a fully idle connection's role refresh lags by at most one ping interval. Noted as the honest
  trade-off rather than adding a per-connection timer for a rare path.

## Known gaps (deliberate, next phases or later)

- Connection count on the project overview is polled (5s), not pushed — a stat tile doesn't need
  its own realtime channel; simplest thing that could work.
- No SDK-side reconnect/backoff — that's Phase 5's job per research/flutter-sdk.md ("exponential
  backoff with full jitter," explicitly scoped to the client).
- No per-message rate limiting on an open realtime connection beyond the bounded outbound buffer
  and the per-project connection quota — a client hammering `ping`/`subscribe` isn't throttled.
  Not exercised by the owner test; worth revisiting if it becomes a real abuse vector.
- Membership/label churn on a **very** large team briefly costs a full live-connection scan
  (`MarkForRevalidation`) — acceptable at expected self-host scale, called out explicitly as a
  trade-off above rather than a surprise.

## Tests

4 new unit test files (`ChannelGrammarTests`, `ConnectionRegistryTests`, `RealtimeMessagesTests`,
`TicketStoreTests` — 34 new unit tests): channel derivation for all three resource shapes, the
account rewrite, event-name wildcard expansion, the bypass firehose (both that it works for bypass
connections and that a non-bypass connection can't use it to skip permissions), quota
accept/reject, revalidation-marking only touching matching roles, session-close scoping. One new
integration suite (`RealtimeTests`, 6 tests) extending `AuthTestBase` with a `TestServer`
WebSocket-client harness (`Factory.Server.CreateWebSocketClient()`), authenticating every
connection via a minted ticket (the harness has no API to attach custom headers to a WS handshake,
and a ticket is exactly the right tool for a non-browser-shaped client anyway): connect/ping/
subscribe round trip, permission-filtered delivery (a caller who can read gets the event, one who
can't gets silence), row-level channel scoping, session revocation closing the socket, and an
unknown project rejected before the upgrade with a normal JSON error response.

## Commands

No change to the command set. New optional config: `Praxy:Realtime:MaxConnectionsPerProject`
(default 1000) — same `builder.Configuration.GetValue("Praxy:…", default)` pattern every other
Phase 1–3 limit uses, no appsettings.json entry needed unless overriding the default.

```
docker run -d --name praxy-dev-pg -e POSTGRES_USER=praxy -e POSTGRES_PASSWORD=praxy \
  -e POSTGRES_DB=praxy -p 5432:5432 postgres:17-alpine   # dev database
dotnet run --project src/Praxy.Api                       # API :5090 (Scalar at /scalar/v1)
npm run dev --prefix console                              # console :5173, /v1 proxied to :5090 (WS too)
dotnet test                                                # 300 tests; Docker required (Testcontainers)
cd deploy && ./up.sh                                       # self-host stack → http://localhost:8080/console
```

## Owner-test checklist (run by this session, all passing)

Run against a from-scratch instance (fresh throwaway Postgres, `dotnet run` on a spare port, the
console's production build served from the API's own `wwwroot/console` — exactly how self-host
actually works) plus the four automated `RealtimeTests`:

1. **Two tabs, edit in one, watch the other's inspector under a second** — claimed the instance,
   created project "Demo," opened the realtime inspector in one tab (connected, "Live" badge), in a
   second tab created a database → table → `title` column → a row via the console's own row-create
   sheet. The `create` event appeared in the inspector within the same second, including all 8
   wildcard-expanded `events[]` entries, the 4 derived `channels[]`, and the raw payload
   (`databaseId`/`tableId`/`rowId`/`roles`). Screenshotted both tabs mid-flow.
2. **Subscribe to a table the session can't read → no events** — automated as
   `RealtimeTests.A_subscriber_who_cannot_read_the_table_receives_nothing`: table granted
   `read("user:A")` only; user A's socket received the create event, user B's socket received
   nothing within an 800ms silence window (no error, no close — invisible, exactly like a row a
   caller can't read is invisible over REST).
3. **Revoke the session → confirm its socket closes** — automated as
   `RealtimeTests.Revoking_the_sessions_closes_its_socket`: an operator `DELETE
   .../users/{id}/sessions/{id}` (the same endpoint Phase 1 built, no new publish call added) closes
   that user's live socket; the client's next `ReceiveAsync` returns a WebSocket `Close` frame.
4. **Row-level channel delivers only that row's events** — automated as
   `RealtimeTests.Subscribing_to_a_specific_row_channel_delivers_only_that_rows_events`: subscribed
   to `databases.<db>.tables.<t>.rows.<row1>`; a different row's create produced silence, an update
   to the subscribed row delivered exactly one event carrying that row's id.

Also verified live in the browser: the "Realtime" sidebar entry appears only once
`capabilities.features.realtime` is true, the project overview's connection-count tile went `0 → 1`
the instant the inspector's socket opened, and the payload `Sheet` renders `events[]`/`channels[]`/
`timestamp`/pretty-printed `payload` correctly.

## Next: Phase 5

Praxy's realtime layer is real — the fourth SDK-facing surface (auth, schema, data, now live
updates) is done. The prompt below is ready to paste into a fresh session.
