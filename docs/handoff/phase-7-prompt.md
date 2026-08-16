# Phase 7 — session prompt

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 7 (Functions) of Praxy**, a self-hosted BaaS (.NET 10 API + PostgreSQL +
Vite/React console + a Flutter SDK, with webhooks as of last phase). Phases 0–6 shipped instance
claim, full app-user auth, the dynamic schema engine, the full data plane, realtime, a native Flutter
client, and an outbox-consuming webhook delivery pipeline (signed HTTP deliveries with retry/backoff/
auto-disable). This phase adds a Docker-backed function executor: deploy code, run it synchronously
or asynchronously, trigger it from events, and schedule it with cron. The plan is settled — implement,
don't re-plan.

Read first, in this order:
1. `docs/handoff/phase-6-report.md` — what webhooks actually shipped and its deviations, especially
   **the "Deviations & notes" section's first bullet**: `praxy.events` (the durable outbox) is written
   *exclusively* by `RowsService.WriteOutboxAsync` — every other event-emitting endpoint
   (`ConsoleAuthAdminEndpoints`'s user/team/session/membership actions) publishes only to the
   in-process realtime bus, never the outbox. This phase's roadmap line "event triggers on the same
   grammar" and its owner-test's "trigger via row create" are both satisfied by the *current* scope
   (row events only) — but if you find yourself wanting a function to trigger on e.g. `users.*.create`,
   that event was never written to the outbox in the first place, and extending which write paths call
   an outbox-write is a prerequisite, not a detail to work around. Verify this against
   `src/Praxy.Tables/RowsService.cs` and `src/Praxy.Api/Endpoints/ConsoleAuthAdminEndpoints.cs`
   yourself before assuming either way.
2. `docs/research/dotnet-stack.md`'s new **"Webhook delivery: SSRF guard and secret storage"**
   section (bottom of the file) — read it even though this phase isn't webhooks: its second half is
   about why webhook signing secrets are stored in plaintext because no project-key *encryption*
   layer exists anywhere in this codebase yet. This phase's roadmap line **"env vars encrypted at
   rest" is not optional the way it was for webhooks** — function environment variables routinely
   hold real credentials (API keys, DB passwords), so this is very likely the phase that has to
   actually build that layer (ASP.NET Core's `Microsoft.AspNetCore.DataProtection` — already
   available via `Praxy.Api`'s Web SDK shared framework, `AddDataProtection()`/`IDataProtector`, no
   new package needed for the API project itself — is the natural candidate; confirm current version/
   API shape before trusting memory, same as every other package in this file). Document whatever you
   decide in this same research file, mirroring how the webhook section documents its own decision.
3. `docs/architecture.md` §7 (Events — the one event vocabulary shared by realtime/webhooks/function
   triggers) for the outbox contract, and skim §5/§6 for how sessions/roles/permissions work since
   "scoped user JWT injected into invocations" needs the same role-resolution/JWT machinery Phase 1
   already built (`IRoleResolver`, `POST /account/jwts` if it exists — check `src/Praxy.Auth/` before
   assuming; the JWT-minting endpoint was listed as "Phase 1 optional, Phase 7 required" in
   research/appwrite-api.md's auth-flows section, so it's plausible it doesn't exist yet).
4. `docs/roadmap.md`'s Phase 7 scope block and owner-test checklist (your acceptance gate) — and the
   M6/Phase-2 estimate table in `docs/architecture.md` §12 if you want the original scope framing.
5. `docs/research/dotnet-stack.md`'s existing pin: `Docker.DotNet.Enhanced` 4.3.3 ("Phase 7 only",
   the Testcontainers-maintained fork — `Docker.DotNet` itself has been stale since May 2023 with no
   deprecation notice, don't reach for it by habit). Verify this version is still current before
   trusting it; the whole file's discipline is "machine-verified pins," not memory.
6. `docs/research/appwrite-api.md` for the open-runtimes HTTP contract (shared-secret header, build/
   start phase split) if it's documented there, and whatever open-runtimes' own public contract
   documents for the parts it isn't — this is a real external contract Praxy is adopting, not an
   internal design choice, so get the wire shape right rather than improvising it.

Build exactly the roadmap's Phase 7 scope:

- **Docker executor** on the open-runtimes contract: HTTP server inside the container, a
  shared-secret header authenticating the executor's calls into it, a build phase (produce a runnable
  image/artifact from uploaded code) distinct from a start phase (run it, wait for warm-up).
  `Docker.DotNet.Enhanced` for the Docker API client.
- **Deployments**: tar upload → build → activate, with build logs captured and queryable (mirror the
  `schema_jobs`/webhook-delivery-attempt pattern of "a row the console can poll/tail," not an
  ephemeral in-memory log).
- **Warm pool**: keep some number of recently-used function containers alive to skip cold-start on
  the next invocation; configurable, per CLAUDE.md's "every limit is configurable and loud when
  tripped" — a pool that's cold when the owner expects it warm should be visible somewhere, not a
  silent latency surprise.
- **Executions**: sync (30s hard cap, response streamed/returned to the caller) and async (result
  **stored**, not discarded — the roadmap is explicit that this is different from sync and a common
  gap in competitors). Both produce a queryable execution record with status/duration/logs, same
  "the console can always see what happened" principle every prior phase's job-like features follow.
- **Event triggers** on the outbox (see point 1 above for the current scope boundary) and **cron
  schedules** for time-based invocation — reuse `Praxy.Realtime.ChannelGrammar.ExpandEventNames` for
  trigger-pattern matching exactly like webhooks does, don't build a third matcher.
- **Scoped user JWT** injected into invocations that need to act as a specific user — this is the
  "Phase 7 required" JWT minting research/appwrite-api.md flagged; check whether `IRoleResolver`'s
  existing role-string output is what gets encoded, or whether something new is needed.
- **Env vars encrypted at rest** — see point 2 above, this is the real crux of this phase's crypto
  work.
- **Runtimes**: Dart first ("dogfoods the SDK" — `sdk/flutter/`'s existing client is a real
  consumer to validate against), Node second.
- **Console**: function list, deployments screen (+ build logs), executions screen (+ logs),
  settings (env vars, triggers, schedule, timeout) — same `<DataGrid />`/`<Sheet />`/create-modal
  conventions every prior console screen uses (`console/src/screens/WebhookDeliveriesPage.tsx` and
  `console/src/screens/RealtimeInspectorPage.tsx` are the closest recent precedent for a
  list-plus-live-log-detail shape). Flip the `functions` capability flag in
  `CapabilitiesEndpoints.cs` and gate the nav entry in `ProjectLayout.tsx` behind it, same two
  wiring points every phase since Phase 4 has used.

Constraints that hold: no changes to the Flutter SDK (`sdk/flutter/`) or the webhook delivery pipeline
(`src/Praxy.Webhooks/`) this phase unless Functions genuinely needs something from them — if it does,
that's a signal to stop and flag it, not to reach back in casually. Follow CLAUDE.md's cross-phase
rules — identifiers never from request strings, deny-by-default, every limit configurable and loud
when tripped, error `type` strings snake_case and unit-tested (add new ones to
`src/Praxy.Core/Errors/ErrorTypes.cs`'s `All` list, same as every prior phase). If Docker isn't
available in your execution environment, say so explicitly rather than claiming the owner-test passed
— this phase's tests will need `Docker.DotNet.Enhanced` talking to a real Docker daemon (Testcontainers
already proves Docker is available in this project's test environment, per every prior phase's
integration-test run).

When done: run the roadmap's Phase 7 owner test yourself (deploy from console → invoke sync → see
logs → trigger via row create → async execution shows stored output → failed build shows its log),
then follow the handoff protocol at the bottom of `docs/roadmap.md`: write
`docs/handoff/phase-7-report.md` and `docs/handoff/phase-8-prompt.md`, update CLAUDE.md's Commands
section if it changed, and print the Phase 8 prompt for the owner.
