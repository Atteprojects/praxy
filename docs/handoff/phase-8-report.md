# Phase 8 — report

**Status: complete.** All roadmap items shipped; owner-test checklist run end to end against an
isolated throwaway instance (fresh Postgres container on a scratch port, a second API instance on
`:5095`, the console dev server temporarily repointed at it — same isolation pattern Phases 5/6/7
used), driving the real console UI in the Browser pane. 373 .NET tests green (277 unit + 96
integration, up from 358 total in Phase 7 — 6 new integration tests in `MessagingTests.cs`, 8 new
unit tests across `TemplateTextTests`/`EmailProviderConfigTests`, plus `ErrorTypesTests` picking up
the 9 new error constants for free). Console `tsc -b && vite build` clean.

## What shipped

**`src/Praxy.Messaging`** (new project — `Praxy.Core` + `Praxy.Persistence` + `Praxy.Auth`
references only; no `Praxy.Events`/`Praxy.Realtime` — see Deviations for why):

- `MessagingProvidersService` + `EmailProviderConfig`: CRUD for per-project providers. Only `email`
  ships (`MessagingProvidersService.KnownTypes`), validated against a fixed list the same way
  `FunctionRuntimes.All`/`ApiKeyScopes.All` gate their own enums — SMS/push slot in later by adding
  to that list, not by changing the shape.
- `EmailProviderResolver`: resolves the `IEmailSender` a project's sends should go through — its
  enabled default `email` provider (built via `Praxy.Auth.SmtpEmailSender`, reused verbatim) or the
  instance-wide singleton (`Praxy:Smtp:*`/`LoggingEmailSender`) as a fallback. The one place both
  transactional auth email and composed messages converge.
- `MessagingTopicsService` / `MessagingTargetsService`: topics, and get-or-create app-user email
  targets (`MessagingTarget`, sourced from `User.Email` on first subscribe/send, never backfilled at
  signup).
- `MessagingTemplatesService` + `TemplateText`: the per-project override system behind "auth email
  templates moved here" — `MessagingTemplatesService.Defaults` holds the exact text
  `AppAuthService`/`TeamsService` used to send inline, now parameterized with `{{var}}` placeholders;
  a project with no override row renders the default, so no project needs a backfill migration.
  `TemplateText.Substitute` is split into its own public static class purely so it's unit-testable
  without a database.
- `AuthEmailBridge` (implements `Praxy.Auth.IAuthEmailSender`): renders the effective template, then
  delivers through `EmailProviderResolver` — a direct send, never logged as a `Message` row (see
  Deviations, transactional vs. campaign).
- `MessagesService`: compose + queue. Resolves topic-subscriber targets ∪ explicit user targets,
  deduplicated by target id, into one `MessageTarget` row each, all in the same transaction as the
  `Message` row itself — no outbox involved (see Deviations).
- `MessageSendWorker` (`BackgroundService`): claims `MessageTarget` rows
  (`FOR UPDATE SKIP LOCKED`, same shape as `WebhookDeliveryWorker`/`FunctionExecutionWorker`), sends
  one at a time via `EmailProviderResolver`, finalizes per-target status, and flips the parent
  `Message` to `completed` once every target it fanned out to is terminal.

**Persistence**: `MessagingProvider`, `MessagingTopic`, `MessagingTarget`, `MessagingSubscriber`,
`Message`, `MessageTarget`, `MessagingTemplate` entities. Migration `20260816221021_Messaging`.

**`src/Praxy.Auth`**: `IAuthEmailSender` + `AuthEmailTemplateKeys` (new file,
`AuthEmailSender.cs`) — the seam Messaging implements. `AppAuthService`/`TeamsService` constructors
swapped `IEmailSender` for `IAuthEmailSender`; their three send call sites
(verification/recovery/invitation) now pass a template key + a `Dictionary<string,string>` of vars
instead of building final subject/body text inline. `IEmailSender`/`EmailMessage`/`SmtpOptions`/
`SmtpEmailSender`/`LoggingEmailSender` are otherwise untouched — still the literal transport classes,
now consumed by `EmailProviderResolver` instead of only by DI-singleton binding.

**`src/Praxy.Api`**: `MessagingEndpoints.cs` — console admin surface under
`/v1/console/projects/{projectId}/messaging/...` (providers, topics + subscribers, templates,
messages + per-target delivery status). `Program.cs` wires `MessagingOptions` (every knob
configurable under `Praxy:Messaging:*`), the new services, and `MessageSendWorker`; also registers
`IAuthEmailSender → AuthEmailBridge`, which is what makes the Phase 1 auth flows route through
Messaging without Auth ever referencing Messaging directly. `messaging` flipped `true` in
`/v1/console/capabilities`.

**Console**: `MessagingTabs` (shared tab header — Messages / Topics / Templates / Providers, same
role `FunctionDetailHeader` plays for a function's sub-views), `MessagesPage` (list + compose modal
with topic checkboxes + a live user-search picker for explicit sends + a delivery-status Sheet per
message, mirroring `WebhookDeliveriesPage`'s delivery Sheet), `MessagingTopicsPage` +
`TopicSubscribersPage` (topic CRUD + a drill-in subscriber list with the same user-search "Add
subscriber" picker, reusing the existing `useProjectUsers` console-users search endpoint — no new
backend surface needed for it), `MessagingTemplatesPage` (the three auth templates, default/
customized badge, save/reset), `MessagingProvidersPage` (provider CRUD, make-default, enable/
disable, reveal-once-style password field). All five wired into `router.tsx`; nav entry gated behind
`features.messaging` in `ProjectLayout.tsx`, same pattern every phase since Phase 4 uses.

## Deviations & notes

Real design decisions this phase had to resolve rather than inherit, recorded with their *why* per
CLAUDE.md:

- **Provider config is genuinely per-project, but as its own table — not `Project.Settings` jsonb the
  way `ProjectAuthSettings` handles Google OAuth.** The phase-8 prompt flagged this as the open
  question and suggested the `ProjectAuthSettings` shape as the template if going per-project.
  Diverged from that suggestion on purpose: providers are an independently CRUDable *list*
  (create, name, enable/disable, set-default, delete — each with its own id and lifecycle), not a
  singleton settings record. That's the same reasoning that already put webhook subscriptions and
  functions in their own tables instead of jsonb blobs, and it's what "model providers generically
  now" (multiple types, eventually multiple providers per type) actually needs. The roadmap's
  "SMTP provider config (reuses Phase 1 sender)" is satisfied literally — `EmailProviderResolver`
  constructs a real `Praxy.Auth.SmtpEmailSender` from the resolved provider's decrypted config, the
  exact Phase 1 class, unmodified.
- **Sending never reads `praxy.events`.** Composing and sending a message is an operator action
  (`POST .../messages`), not a reaction to a row write, so there's nothing to consume from the
  outbox — confirmed by re-reading architecture.md §7 with that question in mind before writing any
  code. What *does* transfer from the webhook/function pattern is the claim-and-finalize shape:
  `MessageTarget` rows are queued in the same transaction as the `Message` row, and
  `MessageSendWorker` claims them with `FOR UPDATE SKIP LOCKED` exactly like
  `WebhookDeliveryWorker`/`FunctionExecutionWorker` claim their own tables. Same worker shape,
  different queue, no outbox.
- **Auth templates are a separate concern from Messaging campaigns — verification/recovery/
  invitation sends are never logged as `Message`/`MessageTarget` rows.** Matches Appwrite's own
  split between its "Templates" settings screen and its "Messaging" module. This is also what let
  `VerificationRecoveryTests`/`TeamsTests` (Phase 1) pass with **zero test changes**: their
  `CapturingEmailSender` still replaces the same DI-registered singleton `IEmailSender`, which
  remains the terminal transport step whenever a project hasn't configured its own provider —
  `AuthEmailBridge` renders through the new template system but still hands off to that exact
  singleton for delivery. Confirmed by running the full integration suite (96/96 green) rather than
  assuming the refactor was compatible.
- **No retry/backoff for message sends**, unlike Phase 6's webhook deliveries. Nothing in the
  roadmap's Phase 8 line calls for it (contrast Phase 6's explicit "retries with exponential
  backoff"), and a failed send is already visible per-target rather than silently swallowed — adding
  retry machinery here would be speculative complexity for a requirement that was never stated.
- **Messaging is entirely console-admin, no data-plane endpoints** — same boundary Phase 6 drew for
  Webhooks. No new `ApiKeyScopes` entry, no Flutter SDK changes, subscribing a user to a topic is
  something an operator does from the console (or a server integration via the console API with an
  operator session), not something the app user's own session calls.
- **`EmailProviderConfig.Parse("{}")` doesn't throw, but System.Text.Json still leaves `Host`/`From`
  null** for a missing property on a non-nullable `string` — found while writing
  `EmailProviderConfigTests`, not by inspection. `Parse` now coalesces both fields after
  deserialization so the type's non-null contract actually holds at runtime, not just at compile
  time.

Also, by design rather than by bug:

- **The parent `Message.Status` is a coarse batch indicator (`processing` → `completed`), never
  `failed`.** Individual target failures are already visible per-target
  (`MessageTarget.Status = "failed"` with `Error` populated) — verified live by configuring a
  provider pointed at an unreachable host and watching a real send complete with both targets showing
  `failed` and a real SMTP error message, while the message itself still reached `completed`. Adding
  a message-level `failed` state on top would be redundant with information the console already
  shows, and its semantics (some failed vs. all failed vs. any failed) aren't specified anywhere in
  the roadmap.
- **Providers' `IsDefault` auto-flips to `true` for a project's first provider of a type.** Verified
  live: creating "Primary SMTP" as the only provider set it as default with no extra click; adding a
  second provider left it non-default until explicitly flipped, which correctly cleared the first
  one's flag in the same request.
- **`TopicSubscribersPage`'s "Add subscriber" reuses the existing console users search endpoint**
  (`useProjectUsers`, already built for `UsersPage`) rather than adding a new lookup — a live-search
  picker was free.

## Known gaps (deliberate, next phases or later)

- **No per-attempt delivery log for messages** — unlike webhook deliveries' `WebhookDeliveryAttempt`
  table, a `MessageTarget` only ever gets one attempt with its outcome inline. Matches "no retry"
  above; a retry-with-log design would need both changes together, not one without the other.
- **SMS and push provider types aren't implemented** — the `MessagingProvider.Type` discriminator and
  `MessagingTarget.Type` already support them structurally (roadmap: "additive later"), but no
  provider driver exists for either yet.
- **No self-service subscription API for app users** (Appwrite lets a client SDK manage its own push
  targets). Not called for in the Phase 8 roadmap line; deferred until a real use case asks for it.
- **One `from` address per provider.** A provider that needs to send from several identities needs
  several provider rows today.

## Tests

`tests/Praxy.Tests.Unit`: `TemplateTextTests` (placeholder substitution, unknown-placeholder
passthrough, and a cross-check that every default template's placeholders are satisfiable by the
exact vars `AppAuthService`/`TeamsService` actually supply), `EmailProviderConfigTests`
(JSON round-trip, null-username round-trip, unreadable-JSON fallback, the `Host`/`From`
null-coalescing fix above). `ErrorTypesTests` (pre-existing) automatically covers the new
`Messaging*` error constants.

`tests/Praxy.Tests.Integration/MessagingTests.cs`: no stubbing beyond the same `CapturingEmailSender`
Phase 1's tests already use for the no-provider-configured fallback path.
`Owner_test_flow_topic_subscribe_send_and_delivery_status_per_target`: create topic → subscribe two
signed-up users → compose + send → poll to `completed` → both targets `sent` with the right
addresses → both captured in `Email.Sent` with the right subject.
`Send_to_explicit_users_needs_no_topic` and `A_user_subscribed_and_named_explicitly_is_only_delivered_to_once`
cover the two ways to name recipients and their overlap. `Sending_with_no_topics_or_users_is_refused`
covers the validation edge. `Verification_email_renders_the_default_then_a_project_override` sends a
real verification email through `AppAuthService` before and after setting a template override and
asserts the rendered subject/link change — the automated equivalent of the browser walkthrough below.
`First_provider_becomes_default_automatically_and_setting_a_new_default_clears_the_old_one` covers
provider CRUD, the auto-default rule, the default-flip transaction, and that a stored secret is never
echoed back.

## Commands

New: self-hosters can tune the send loop via `Praxy:Messaging:*` config keys:
`SendPollIntervalSeconds` (2, fallback poll cadence — the worker also wakes immediately on every
compose via `MessageSendSignal`), `MaxSubjectLength` (998), `MaxBodyLength` (65536),
`MaxTargetsPerMessage` (10,000).

No other command changes — Messaging runs automatically as part of the existing `dotnet run
--project src/Praxy.Api` / `npm run dev --prefix console` dev commands (`MessageSendWorker` is a
sixth hosted service starting with the API, same as every prior phase's background workers) and
`dotnet test` already picks up the new test files. No new runtime dependency (unlike Phase 7's Docker
requirement) — Messaging only needs Postgres, which every phase already needs.

## Owner-test checklist (run by this session, all passing)

Run against an isolated throwaway instance (fresh Postgres container on a scratch port `5433`, a
second API instance on `:5095`, the console dev server temporarily repointed at it —
`console/vite.config.ts`'s proxy-target edit reverted afterward, `git diff` on it is empty), driving
the real console UI in the Browser pane, with two app users created via the console's Users screen:

1. **Create topic → subscribe two users** — created "Announcements" from the Topics tab; the topic
   detail's "+ Add subscriber" live-search picker found and added `ada@example.com` and
   `bob@example.com` by typing a few characters; the topic list immediately showed "2" subscribers.
2. **Compose + send** — the composer showed "Announcements · 2 subscriber(s)" as a selectable
   checkbox; sent "Big news" with no provider configured, which fell back to the instance-wide dev
   logger exactly as designed (confirmed in the API process's own log: both addresses, correct
   subject/body).
3. **Delivery status per target** — the message Sheet showed `completed` with both targets `sent`.
   Then, to verify the failure path isn't just theoretical, configured a real (deliberately
   unreachable) SMTP provider via the Providers tab, sent a second message, and watched both targets
   land on `failed` with a real SMTP error message inline — while the parent message still reached
   `completed`, confirming the by-design split documented above.
4. **Auth verification email still renders with the project template** — triggered a real
   `POST /v1/account/verification` for `ada@example.com` (curl, since this is an app-user session
   call, not a console one) with no template override: the API log showed the exact default subject/
   body. Then set a custom "Confirm your {{project}} account" / "Click here: {{url}}" override via
   the Templates tab (which immediately showed a "customized" badge) and triggered verification
   again: the log showed "Confirm your Messaging Test account" with the correctly-substituted
   verify link — the template genuinely changed what got sent, not just what the console displays.

Also verified beyond the literal checklist: **provider CRUD** (create → auto-default, second
provider stays non-default until explicitly flipped, password never echoed back in list/detail
responses), and **template reset-to-default** (deleting the override reverted the "customized" badge
to "default" and the effective text back to the compiled-in default).

Also verified: `dotnet build`/`dotnet test` (373/373: 277 unit + 96 integration, run twice — once
per-project during development, once via `Praxy.sln` for the final pass) and `npm run build --prefix
console` (`tsc -b && vite build`) both clean; the throwaway Postgres container, second API process,
and vite dev server used for the walkthrough were torn down afterward; the persistent dev stack
(`praxy-dev-pg`, the `deploy/` self-host compose stack) was never touched by any of this session's
throwaway resources.

## Next: Phase 9

Messaging is real: providers, topics, subscribers, send-to-topic and send-to-users, per-target
delivery status, and Praxy's own auth emails now render through a project-overridable template
system with the same reliable fallback behavior every project already had. The prompt below is ready
to paste into a fresh session.
