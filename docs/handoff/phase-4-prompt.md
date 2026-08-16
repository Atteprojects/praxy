# Phase 4 — session prompt

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 4 (Realtime) of Praxy**, a self-hosted BaaS (.NET 10 API + PostgreSQL +
Vite/React console). Phases 0–3 shipped the solution skeleton, instance claim, console operator sessions,
projects API, full app-user auth (email+password + Google OAuth, teams/memberships, the one `IRoleResolver`
— do not fork it), the dynamic schema engine (databases → tables → columns → indexes, sync/async DDL, job
runner), and the full data plane: row CRUD, the 24-method query DSL/compiler, keyset pagination, permission
filtering (table-level always, row-level `EXISTS` join when `row_security` is on — the same OR-combination
logic you'll reuse for fan-out), a catalog cache, and an outbox (`praxy.events`, written but not yet
consumed) plus an in-process `IEventBus` (also written but not yet consumed — every row write already
publishes to it). This session adds the WebSocket endpoint, channel subscriptions, and permission-filtered
fan-out — the first real consumer of both. The plan is settled — implement, don't re-plan.

Read first, in this order:
1. `docs/handoff/phase-3-report.md` — what exists, where it lives, deviations that affect you (particularly:
   `IEventBus.PublishAsync` is already called after every row create/update/delete commit, with
   `PraxyEvent.Permissions` set to the roles that can *read* that row, computed pre-commit — this is exactly
   the precomputed-roles fan-out input architecture.md §6 asks for, already built; the catalog cache is
   invalidated by direct calls from mutating services, not through `IEventBus` — don't assume schema changes
   publish events you can subscribe to)
2. `docs/roadmap.md` — the Phase 4 scope block and owner-test checklist (your acceptance gate)
3. `docs/architecture.md` §6 (realtime — channel grammar, fan-out design, permission-filtering-at-fan-out
   not at subscribe, bounded per-connection channel), §5 (role resolution — reuse `IRoleResolver`, resolved
   once at connect per the roadmap, don't re-resolve per event), §7 (events — `PraxyEvent` shape, already
   defined in `Praxy.Events`)
4. `docs/research/appwrite-api.md`'s realtime section — the message-mode wire protocol (client→server
   `ping`/`subscribe`/`unsubscribe`, server→client `connected`/`response`/`event`/`pong`/`error`), the
   `subscriptions[]` matched-ids field (lets the SDK dispatch without re-matching channel strings), close
   codes, and Praxy's one deliberate divergence from Appwrite (early subscribe before auth settles is
   *queued*, never `1008`-closed — Appwrite's close-then-reconnect-then-resend loops in their own SDK)
5. `docs/research/dotnet-stack.md`'s WebSocket + bounded channel section — the exact shape
   (`app.UseWebSockets`, `Channel.CreateBounded<ReadOnlyMemory<byte>>(256)`, one writer task per socket,
   `KeepAliveTimeout` for dead-peer detection) is already verified there; use it as written

Build exactly the roadmap's Phase 4 scope:

- **WebSocket endpoint**: `GET /v1/realtime?project=<id>` (+ `ticket=<t>` for non-browser clients), upgraded
  in-process. Browsers authenticate with the session cookie already on the request; native clients call
  `POST /v1/realtime/ticket` first to mint a single-use, 60-second ticket (avoids a long-lived session
  secret riding a URL into proxy logs) and pass it as the query param instead.
- **Message-mode protocol only** — no URL-mode `channels[]` query param subscribing. Client→server:
  `{"type":"ping"}`, `{"type":"subscribe","data":[{subscriptionId, channels, queries?}]}` (batched,
  client-generated ids), `{"type":"unsubscribe","data":[{subscriptionId}]}`. Server→client: `connected`
  (carries the resolved user or null), `response`, `event` (carries `events[]`, `channels[]`,
  **`subscriptions[]`** — which of the caller's subscriptions matched —`timestamp`, `payload`), `pong`,
  `error`. Close codes `1003`/`1008`/`1013` per appwrite-api.md; a subscribe arriving before auth settles is
  queued, not closed.
- **Channel grammar**: `account` (server rewrites to `account.<userId>` at subscribe),
  `databases.<db>.tables.<t>.rows`, `databases.<db>.tables.<t>.rows.<rowId>`, `teams.<teamId>`, plus
  action-suffixed variants (`...rows.create`) subscribable directly.
- **Roles resolved once at connect** via the existing `IRoleResolver` (same resolver the query compiler
  already uses — do not fork it), indexed project→role→channel→connection for O(1) fan-out lookups against
  an event's precomputed `Permissions`. Membership/session-changing events should set a revalidation flag on
  affected connections rather than re-resolving eagerly; a `sessions.delete` event **closes that session's
  sockets** outright (Phase 1's session revocation already publishes this — wire the realtime consumer to
  react to it, don't add a new publish call).
- **Fan-out is a hash-lookup intersection**, not a permission re-check: each event carries the roles that
  can see it (row events already carry this from Phase 3 — reuse `PraxyEvent.Permissions` as-is), each
  connection carries its subscriber's resolved roles, delivery = non-empty intersection against the
  connection's subscribed channels. Permission filtering happens at fan-out, never at subscribe time.
- **Bounded per-connection channel** (capacity 256, single writer task per socket — `SendAsync` isn't safe
  for concurrent callers). A slow consumer whose buffer fills gets closed with `1013`; let the client
  resubscribe. Never let a queue grow unbounded.
- **Ticket endpoint**: `POST /v1/realtime/ticket`, single-use, 60s expiry, requires an existing session/key.
- **API keys may subscribe** (scope-checked) — Appwrite bars server credentials from realtime entirely;
  Praxy's `appwrite-api.md` deviation list explicitly keeps this open.
- **Ping every 30s, drop the connection on a missed pong.** Cap connections per project as a quota
  (configurable, per CLAUDE.md's "every limit is configurable and loud when tripped" rule — though a
  connection cap has no `Retry-After` equivalent; a clear close code/reason is the loud part here).

**Console** — realtime inspector: a live event tail (subscribe to something broad, e.g. every
`databases.*` channel the operator's own bypass-everything access can see) with a channel filter and a
payload viewer; live connection count surfaced on the project overview screen. console-design.md called
this out as "the cheapest possible debugging tool for the fan-out logic and it demos extremely well" — worth
building well, not as an afterthought.

Constraints that hold: conventional commits, small and topical; never commit `.env`; Testcontainers
integration tests where they make sense (a raw WebSocket test harness against the Testcontainers-backed API
factory — check how `Praxy.Tests.Integration`'s `PraxyApiFactory`/`WebApplicationFactory` currently issue
HTTP clients and extend for a WebSocket client, `ClientWebSocket` against the test server's WS endpoint);
new error `type` strings registered in `ErrorTypes.All` (the snake_case lint test enforces the format);
**identifiers never from request strings** where any of this touches SQL (it mostly won't — realtime reads
no new tables, it consumes the event stream); deny by default carries over — a connection with no matching
role for a channel gets no events on it, not an error, exactly like a row a caller can't read is invisible
rather than a 403.

I have full permission for package installs and edits inside this repo (no new NuGet package should be
needed — WebSockets and `System.Threading.Channels` ship in the shared framework, per dotnet-stack.md). Use
subagents where useful.

When done: run the roadmap's Phase 4 owner test yourself (two browser tabs — edit a row in one, watch the
event in the other's inspector in under a second → subscribe to a table the session can't read → confirm no
events arrive → revoke the session → confirm its socket closes → subscribe to a specific
`rows.<rowId>` channel and confirm it delivers only that row's events), then follow the handoff protocol at
the bottom of `docs/roadmap.md`: write `docs/handoff/phase-4-report.md` and
`docs/handoff/phase-5-prompt.md`, update CLAUDE.md's Commands section if it changed, and print the Phase 5
prompt.
