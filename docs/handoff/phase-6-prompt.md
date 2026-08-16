# Phase 6 — session prompt

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 6 (Webhooks) of Praxy**, a self-hosted BaaS (.NET 10 API +
PostgreSQL + Vite/React console + a Flutter SDK as of last phase). Phases 0–5 shipped instance
claim, full app-user auth, the dynamic schema engine, the full data plane (row CRUD, query DSL,
permissions), realtime (WebSocket, permission-filtered fan-out), and a native Flutter/Dart client.
This phase returns to the normal .NET-API-plus-console shape after Phase 5's SDK-only detour: an
**outbox consumer** that turns `praxy.events` rows (written since Phase 3, read by nothing until
now) into signed HTTP deliveries. The plan is settled — implement, don't re-plan.

Read first, in this order:
1. `docs/handoff/phase-5-report.md` — what the SDK actually shipped and its deviations, so you
   know what's already consuming the event grammar you're about to add a second consumer for
   (realtime fans out `PraxyEvent` in-process; webhooks read the durable `praxy.events` outbox —
   same event *shape*, two different delivery mechanisms, per architecture.md §7). Not required
   reading for the webhook work itself, but useful context for why the outbox table already exists
   with real rows in it — `RowsService.WriteOutboxAsync` (`src/Praxy.Tables/RowsService.cs`) has
   been writing `OutboxEvent` rows inside the same transaction as every row create/update/delete
   since Phase 3.
2. `docs/architecture.md` §7 (Events) and §11 (threat model — SSRF is listed as a resource-
   exhaustion / cross-origin-abuse-adjacent risk your SSRF guard closes) for the outbox contract:
   at-least-once delivery, `praxy.events` as the durable source of truth, one event vocabulary
   shared by realtime/webhooks/function-triggers (future).
3. `docs/roadmap.md`'s Phase 6 scope block and owner-test checklist (your acceptance gate).
4. `docs/research/appwrite-api.md`'s Webhooks section and Event grammar section — Praxy adopts
   Appwrite's delivery shape (headers, retry posture) but **fixes the signature scheme**: Appwrite
   signs with `base64(HMAC-SHA1(url + body))` (SHA-1, no timestamp, replay-vulnerable); Praxy uses
   `X-Praxy-Webhook-Signature: v1=<hex HMAC-SHA256(timestamp + "." + body)>` with a separate
   `X-Praxy-Webhook-Timestamp` header — Stripe's scheme, chosen specifically because it's replay-
   resistant and needs no URL-canonicalization games. The event grammar
   (`<resource>.<id>[.<subresource>.<id>].<action>[.<attribute>]`, `*` wildcards any id segment) is
   the same one `Praxy.Realtime.ChannelGrammar`/`RowsService.BuildEvent` already produce — reuse the
   event `Type` strings verbatim, don't invent a second vocabulary.
5. `docs/research/dotnet-stack.md` before adding any package (`FOR UPDATE SKIP LOCKED` outbox-
   consumer pattern, `HttpClient`/`IHttpClientFactory` config for the delivery client, retry/backoff
   library choice if any) — it holds machine-verified pins; check it before trusting memory.

Build exactly the roadmap's Phase 6 scope:

- **Outbox consumer**: a hosted service reading `praxy.events` with `FOR UPDATE SKIP LOCKED`,
  at-least-once semantics (a delivery attempt that crashes mid-flight must be retried, not lost or
  double-committed-as-done). Mirror `SchemaJobRunner`'s polling/locking shape
  (`src/Praxy.Tables/SchemaJobRunner.cs`) where it fits — same problem shape, already-verified
  pattern in this codebase.
- **Per-project webhook subscriptions**: URL, the event-name pattern(s) it subscribes to (with `*`
  wildcards, matched against the same expansion `ChannelGrammar.ExpandEventNames` already computes
  for realtime — don't recompute wildcard matching a second way), enabled/disabled, a signing
  secret generated at creation and shown exactly once (same reveal-once pattern as API keys from
  Phase 1).
- **Delivery**: `X-Praxy-Webhook-{Id,Events,Name,ProjectId}` headers plus the signature/timestamp
  pair above, 15s timeout, **no redirects followed cross-origin**, an SSRF guard (deny
  private/loopback/link-local ranges by default; self-host config can allow them for
  reverse-proxied internal targets). Retries with exponential backoff + jitter (same "full jitter,
  not lockstep" reasoning Phase 5's realtime reconnect used — a design principle recorded in
  research/flutter-sdk.md and worth mirroring server-side too — check whether a suitable retry
  primitive already exists per dotnet-stack.md before hand-rolling one). A delivery log:
  per-attempt status/latency/response code/response body (capped size). Disable a webhook
  automatically after N consecutive failures, surfaced in the console as a warning banner, not a
  silent stop.
- **Console**: webhook list + create (URL, event picker, signing-secret-reveal-once), a delivery
  log view with payload and a redeliver button. Same DataGrid/sheet conventions the rest of the
  console already uses — reuse, don't reinvent.

Constraints that hold: no realtime or SDK changes this phase (Phase 5's client is done; if webhooks
somehow need something from it, that's a signal to stop and flag, not to reach back into
`sdk/flutter/`). Follow CLAUDE.md's cross-phase rules — identifiers never from request strings,
deny-by-default, every limit configurable and loud when tripped, error `type` strings snake_case
and tested. If the actual `praxy.events`/outbox schema doesn't support something this prompt
assumes, verify against `src/Praxy.Events/` and `src/Praxy.Persistence/` before concluding it's a
real gap — Phase 3 built the outbox exactly for this phase to consume, so a mismatch is more likely
a misreading than an actual absence.

When done: run the roadmap's Phase 6 owner test yourself (register a webhook against a local echo
server → create a row → confirm the delivery is logged and its signature verifies → point a
webhook at a dead URL → watch retries/backoff happen → redeliver from the console), then follow the
handoff protocol at the bottom of `docs/roadmap.md`: write `docs/handoff/phase-6-report.md` and
`docs/handoff/phase-7-prompt.md`, update CLAUDE.md's Commands section if it changed, and print the
Phase 7 prompt for the owner.
