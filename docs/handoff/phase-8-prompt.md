# Phase 8 — session prompt

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 8 (Messaging) of Praxy**, a self-hosted BaaS (.NET 10 API + PostgreSQL +
Vite/React console + a Flutter SDK). Phases 0–7 shipped instance claim, full app-user auth, the
dynamic schema engine, the full data plane, realtime, a native Flutter client, an outbox-consuming
webhook delivery pipeline, and a Docker-backed function executor (sync/async/event/cron invocation,
encrypted env vars, scoped user JWTs). This phase adds email messaging: topics, targets, subscribers,
send-to-topic and send-to-users, per-message delivery status, and moving the auth flow's email
templates into this system. The plan is settled — implement, don't re-plan.

Read first, in this order:

1. `docs/handoff/phase-7-report.md` — what Functions actually shipped and its deviations. Not
   directly load-bearing for Messaging, but its "Deviations & notes" section documents two patterns
   you'll reuse verbatim: the `FOR UPDATE SKIP LOCKED` claim-loop shape every async pipeline in this
   codebase now uses (`SchemaJobRunner`, `WebhookDeliveryWorker`/`WebhookOutboxDispatcher`,
   `FunctionBuildWorker`/`FunctionExecutionWorker`), and the "per-consumer claim column, never a
   shared one" lesson from splitting `OutboxEvent.DispatchedAt` — relevant if send-to-topic ends up
   needing its own outbox-style fan-out (it may not: sending is operator-triggered, not event-
   triggered, so check whether you need the outbox at all before reaching for it out of habit).
2. `src/Praxy.Auth/EmailSender.cs` in full — `IEmailSender`/`EmailMessage(To, Subject, TextBody)`/
   `SmtpOptions`/`SmtpEmailSender`/`LoggingEmailSender` already exist and already send Phase 1's
   verification/recovery emails (`AppAuthService.cs` calls `email.SendAsync(new EmailMessage(...))`
   directly with inline text, no template system — grep it yourself, don't take this description on
   faith). The roadmap line "templates for the auth emails moved here" means this phase likely needs
   to introduce a template system and re-route those two call sites through it — confirm that's really
   what "moved here" means (versus just *documenting* the existing behavior) before assuming the scope.
3. **A real design question this phase has to resolve, not inherit an assumption about**: today's SMTP
   config (`SmtpOptions`, bound from `Praxy:Smtp:*` app configuration in `Program.cs`) is
   **instance-wide** — one SMTP server for the whole Praxy instance, set by whoever runs `docker
   compose up`. The roadmap's Phase 8 line says "SMTP provider config (reuses Phase 1 sender)" and
   separately calls for a console "provider settings" screen — read literally, a *settings screen*
   implies **per-project** configuration, which is a different model than what exists today. Decide
   which this actually is (reuse the instance-wide config as-is, with the screen just displaying it
   read-only or not existing at all; or genuinely make it per-project, which touches
   `Project.Settings` the way `ProjectAuthSettings` does for Google OAuth) by checking what
   `research/appwrite-api.md` and `architecture.md` say about it, and document the decision the way
   Phase 6/7 documented their own crypto/executor decisions.
4. `docs/architecture.md` §7 (Events) again — re-read with an eye specifically toward whether
   send-to-topic needs to consume `praxy.events` (it plausibly doesn't; sending is an operator action,
   not an event reaction) versus whether per-message delivery status needs its own outbox-shaped
   queue (a `send` action fanning out to N targets, each needing an attempt/status row, looks a lot
   like `WebhookOutboxDispatcher` fanning an event out to N subscriptions — that pattern may transfer
   even without touching `praxy.events` itself).
5. `docs/roadmap.md`'s Phase 8 scope block and owner-test checklist (your acceptance gate) — quoted
   here for convenience: *"Email only initially (owner's minimal-options rule): SMTP provider config
   (reuses Phase 1 sender), topics, targets (user email), subscribers, send-to-topic + send-to-users,
   per-message delivery status, templates for the auth emails moved here. Providers/SMS/push are
   additive later — model `providers` generically now."* Owner test: *"create topic → subscribe two
   users → compose + send → delivery status per target → auth verification email still renders with
   the project template."*
6. `docs/research/appwrite-api.md` and `docs/research/dotnet-stack.md` for whatever either already
   says about messaging/email templates/provider modeling — check before assuming neither covers it.

Build exactly the roadmap's Phase 8 scope:

- **Providers modeled generically now**, per the roadmap, even though only email ships — the shape
  should not need a rewrite when SMS/push land later (Phase 9+, additive).
- **Topics, targets, subscribers**: a topic groups subscribers; a target is (for this phase) an app
  user's email address. Subscribing/unsubscribing, listing subscribers per topic.
- **Send-to-topic and send-to-users**: compose a message, send to everyone subscribed to a topic, or
  to an explicit list of users.
- **Per-message delivery status**: one row the console can query per target, same "the console can
  always see what happened" principle every prior phase's job/delivery/execution table follows
  (`schema_jobs`, `webhook_deliveries`, `function_executions` are the three existing examples — match
  their shape, don't invent a fourth).
- **Auth email templates moved here**: resolve point 3 above, then make the verification/recovery
  (and any other Phase 1 auth emails — check for all call sites, not just the two already found)
  render through the new template system while still working exactly as before from the caller's
  perspective.
- **Console**: messages list + composer, topics + subscribers, provider settings (shape depends on
  point 3's resolution). Flip the `messaging` capability flag in `CapabilitiesEndpoints.cs` and gate
  the nav entry in `ProjectLayout.tsx` behind it — the same two wiring points every phase since Phase
  4 has used; `console/src/screens/FunctionsPage.tsx` + `FunctionDeploymentsPage.tsx` (Phase 7) are
  the freshest precedent for a list-plus-detail-with-live-status shape, alongside
  `WebhookDeliveriesPage.tsx` (Phase 6) for the delivery-status-per-target table specifically.

Follow CLAUDE.md's cross-phase rules — identifiers never from request strings, deny-by-default, every
limit configurable and loud when tripped, error `type` strings snake_case and unit-tested (add new
ones to `src/Praxy.Core/Errors/ErrorTypes.cs`'s `All` list, same as every prior phase). No changes to
the Flutter SDK (`sdk/flutter/`), `src/Praxy.Webhooks/`, or `src/Praxy.Functions/` this phase unless
Messaging genuinely needs something from one of them — if it does, that's a signal to stop and flag
it in the report, not to reach back in casually (Phase 7's report has a worked example of exactly this
kind of flagged, justified, minimal cross-phase touch).

When done: run the roadmap's Phase 8 owner test yourself (create topic → subscribe two users →
compose + send → delivery status per target → auth verification email still renders with the project
template), then follow the handoff protocol at the bottom of `docs/roadmap.md`: write
`docs/handoff/phase-8-report.md` and `docs/handoff/phase-9-prompt.md`, update CLAUDE.md's Commands
section if it changed, and print the Phase 9 prompt for the owner.
