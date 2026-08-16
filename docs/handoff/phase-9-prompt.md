# Phase 9 — session prompt

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 9 (Hardening → v0.1.0) of Praxy**, a self-hosted BaaS (.NET 10 API +
PostgreSQL + Vite/React console + a Flutter SDK). Phases 0–8 shipped instance claim, full app-user
auth, the dynamic schema engine, the full data plane, realtime, a native Flutter client, an
outbox-consuming webhook delivery pipeline, a Docker-backed function executor, and email messaging
(providers, topics, subscribers, send-to-topic/users, per-target delivery status, and a
project-overridable template system that Praxy's own auth emails now render through). This is the
**last phase before tagging v0.1.0** — it is deliberately not a new feature module. It is quotas,
audit-log correctness, backup/restore, an upgrade test, load tests, a security pass, and docs. The
plan is settled — implement, don't re-plan, and don't invent new product surface along the way.

Read first, in this order:

1. `docs/handoff/phase-8-report.md` — what Messaging actually shipped and its deviations. Not
   directly load-bearing for hardening, but its "known gaps" section and the pattern every prior
   report has followed (claim-loop workers, per-resource console screens, deny-by-default) is the
   baseline this phase audits rather than extends.
2. `docs/roadmap.md`'s Phase 9 scope block (quoted here for convenience, since — unlike every prior
   phase — it has no single "Owner test:" one-liner; this phase's acceptance gate is the checklist
   you build from the bullets below, not a single click-through flow): *"Org-level quotas (`limits
   jsonb`) enforced + surfaced; audit log (admin actions distinguished from user actions);
   backup/restore documented + tested per schema (`pg_dump -n px_<id>`); upgrade test from the
   previous tag against real data (release gate); load tests — 1k schemas, 10k WebSocket
   connections, query compiler fuzzing; security pass — the threat-model table in architecture.md
   verified item by item + the `console`-project guard + SSRF + rate limits; error-type lint
   (`^[a-z0-9_]+$`); docs: self-host guide, API reference from OpenAPI, SDK readme. Tag v0.1.0."*
3. `docs/architecture.md` §11 (Threat model) — the table you verify item by item. Also re-read §3
   (`organizations.limits jsonb` already exists as a column — confirm whether anything currently
   reads it, since "enforced + surfaced" implies it's currently neither) and §10 (Operations —
   backup/restore, migrations, upgrade safety) before assuming what's already built versus what
   this phase adds.
4. `docs/research/dotnet-stack.md` for whatever it already says about rate limiting internals, load
   testing tools, or fuzzing approaches for .NET — check before reaching for a new package; this doc
   holds the machine-verified pins and corrections you must not upgrade past without checking.
5. Every prior phase's report (`docs/handoff/phase-{0..8}-report.md`) has a "Known gaps (deliberate,
   next phases or later)" section — grep them. Several explicitly say "Phase 9 hardening candidate"
   (e.g. Phase 7's "no per-function execute permissions", Phase 6/7's "no retention job yet" for
   `webhook_delivery_attempts`/`praxy.events`/`praxy.audit_log`). Decide which of these the roadmap's
   Phase 9 bullets actually cover versus which are out of scope for v0.1.0 — don't silently adopt
   every flagged gap, and don't silently drop ones the roadmap does cover.

Build exactly the roadmap's Phase 9 scope:

- **Org-level quotas.** `organizations.limits` (jsonb) already exists in the schema — confirm what,
  if anything, currently reads or writes it, then design the actual limit dimensions (projects per
  org? databases/tables/rows per project? already partially covered by Phase 2's "caps on tables,
  columns and indexes per project" — architecture.md §11 — don't duplicate that, extend it to be
  org-configurable if it isn't already) and enforce them with the same "every limit is configurable
  and loud when tripped" rule CLAUDE.md states for every phase — a clear error type, not a silent
  cap. Surface current usage vs. limit somewhere in the console (organizations are hidden in UI per
  the fixed decisions — figure out the least-invasive surfacing that doesn't require building an org
  switcher this phase).
- **Audit log: admin vs. user actions distinguished.** `praxy.audit_log` has been written to since
  Phase 6/7 (`AuditLogEntry` rows on webhook/function console mutations) with `Actor = "user:<id>"`
  for the *console operator* in every case so far — check whether that's actually ambiguous with an
  app user's own actions (which may not be logged at all yet) and design the distinction the roadmap
  line calls for. Decide whether app-user actions get logged at all this phase, or whether "admin
  actions distinguished from user actions" just means the existing admin entries need a clearer
  actor tag — don't assume, check what's already written today across every `AuditAsync` call site
  first (`grep -rn "AuditLog.Add" src/`).
- **Backup/restore, documented and tested.** `pg_dump -n px_<dbid>` per database schema, plus the
  `praxy` system schema itself. Write it up as an actual runbook (self-host guide, see docs below),
  and prove it works: back up a database with real data, drop it, restore, verify data and metadata
  (the `praxy.databases`/`tables`/`columns` rows, not just the raw table) are consistent again.
- **Upgrade test from the previous tag against real data.** There is no previous tag yet — v0.1.0 is
  the first one. Decide what this means in practice for this phase (likely: prove the *mechanism*
  works — run the current migration set against a database seeded by an earlier migration state,
  confirm `CatalogMigrator`'s advisory-lock startup migration handles it cleanly — and document the
  process so it's real for v0.1.1's upgrade from v0.1.0). Don't skip it just because there's no prior
  tag to literally check out.
- **Load tests.** 1k schemas (databases), 10k WebSocket connections, query compiler fuzzing. These
  need actual scripts/tooling, not just a claim — decide where they live (a `tests/` project, a
  `scripts/` directory) and what they report. Check `docs/research/dotnet-stack.md` for any existing
  guidance on load-testing tools before picking one.
- **Security pass.** Walk `docs/architecture.md` §11's threat-model table row by row against the
  actual current implementation (not from memory — grep and read the real code for each mitigation)
  plus the three items called out explicitly: the `console`-project guard (Phase 0's integration
  test — still holding?), SSRF (Phase 6's `SsrfGuard` — still the only place a webhook/provider URL
  reaches out?), and rate limits (still 429 with `Retry-After`/`RateLimit-*`, still tight on auth
  endpoints). Record findings the way Phase 6/7's reports recorded deviations — with the *why*, not
  just a checklist tick.
- **Error-type lint.** `ErrorTypesTests` already asserts every registered type matches
  `^[a-z0-9_]+$` (Phase 0). Confirm it still passes with every type added through Phase 8, and decide
  whether "lint" this phase means something more (a CI-enforced check, a pre-commit hook) versus the
  existing unit test already satisfying the roadmap line.
- **Docs: self-host guide, API reference from OpenAPI, SDK readme.** The self-host guide is the
  backup/restore runbook plus the existing `deploy/docker-compose.yml` walkthrough, written for an
  operator who has never read this codebase. The API reference generates from the OpenAPI document
  `Program.cs` already produces in dev (`app.MapOpenApi()`) — decide how it ships for production
  (architecture.md §8: "OpenAPI generated by .NET 10's built-in document generation, published per
  release"). The SDK readme covers `sdk/flutter/` — confirm what's there today and what's missing.
- **Tag v0.1.0.** Last step, after everything above is verified — this is a real release gate per the
  roadmap ("upgrade test... release gate"), not a formality.

Follow CLAUDE.md's cross-phase rules — identifiers never from request strings, deny-by-default, every
limit configurable and loud when tripped, error `type` strings snake_case and unit-tested. This phase
is explicitly the one that audits those rules against real code rather than introducing new ones — do
not add new product features, console screens beyond what quotas/audit need, or scope not named
above. No changes to `sdk/flutter/`, `src/Praxy.Webhooks/`, `src/Praxy.Functions/`, or
`src/Praxy.Messaging/` this phase unless hardening genuinely needs something from one of them — if it
does, that's a signal to stop and flag it in the report, not to reach back in casually (Phase 7 and
Phase 8's reports both have worked examples of exactly this kind of flagged, justified, minimal
cross-phase touch).

When done: build the owner-test checklist yourself from the roadmap bullets above (there is no single
prescribed flow this phase, unlike every prior one) — at minimum, demonstrate a quota tripping with a
clear error, an audit log entry showing the admin/user distinction, a real backup→restore round trip
with verified data, the load test scripts running and reporting results, the security-pass findings
written down, and the error-type lint passing — then follow the handoff protocol at the bottom of
`docs/roadmap.md`: write `docs/handoff/phase-9-report.md`, update CLAUDE.md's Commands section if it
changed, tag `v0.1.0`, and print a closing summary for the owner (there is no Phase 10 prompt to
write — v0.1.0 is the end of this roadmap).
