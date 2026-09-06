# Self-hosted and managed — what a two-product model requires

## Context

The owner decided (2026-09-06) to follow Appwrite's shape: **ship the self-hosted product and also
run a managed version of it.** That means two products from one codebase, with genuinely different
security requirements — the self-hosted instance is run by the person who trusts everyone on it, and
the managed one hosts strangers who don't trust each other.

This document works out what that actually costs. Every claim is checked against the code or the live
praxycore.dev instance, and the check is quoted. It is deliberately **not** a phase plan and there is
no handoff prompt for it, because nothing here is scheduled — producing one would invite someone to
start building.

Companion to `docs/research/security-review.md`, whose three phases rated every finding under two
actor models precisely so this document could exist
(`docs/handoff/security-review-phase-3-report.md` §5).

## The reframe: most of what looks like debt is a self-host feature

Four designs in Praxy today are correct for one trusted operator and wrong for strangers: the
**Docker socket** (root-equivalent host access per build), a **single Postgres superuser** with
tenant isolation enforced only in application code, a **flat container network** where one tenant's
container reaches another's, and **tenant content served from the console's own origin**.

Read as a multitenancy backlog, that's four architectural blockers. Read correctly, it is **four
decisions that stay right for the self-hosted product forever** and are only problems for the managed
one. `deploy/up.sh`'s one-question setup and a single compose file are a deliberate selling point;
none of the hardening below should land in the self-hosted path if it costs that.

This is also what Appwrite does: the open-source product keeps the simple execution model, and the
Cloud offering adds isolation that isn't in the OSS repo. Following that shape means the question is
never "how do we fix these four things" but **"which of these does the managed deployment solve, and
does it need code or just configuration?"**

## The fork that decides almost everything

Before any of the four, one product/infrastructure decision determines whether they matter at all:

**A. One instance per tenant.** Each customer gets their own Praxy — own Postgres, own Docker daemon,
own containers — with a thin control plane for provisioning and billing. Isolation is at the
infrastructure layer, where it is strongest and least novel. **All four blockers evaporate**, because
no two tenants ever share the thing being contended: the codebase needs essentially nothing, and the
managed product is the self-hosted product plus automation. The cost is per-customer footprint —
every tenant carries a Postgres and an idle API — which is real money at the free-tier end.

**B. Many tenants per instance.** Tenants share Postgres, the Docker daemon, and networks. Far cheaper
per customer and the only model that makes a free tier comfortable. **All four blockers must be solved
in code**, and they are the hard kind: a tenant-aware executor, a non-superuser runtime with per-tenant
database roles, per-tenant networks, and a separate content origin.

**C. Cells — many tenants per instance, many instances.** The middle most SaaS converges on
(AWS calls it cell-based architecture): roughly 50-200 tenants per instance, N instances. A thousand
tenants becomes ~10 cells rather than 1,000 instances or one shared everything. Fleet operations stay
a script rather than a platform team, blast radius is bounded to one cell, rolling cell-by-cell gives
canary deploys for free, and cost amortises across the cell. **The catch: inside a cell tenants share
a Postgres and a daemon, so C needs the same isolation work as B.** Cells bound the four blockers;
they do not remove them.

Appwrite Cloud is closer to B. That does not automatically make B right for Praxy — Appwrite had a
team and a funding round when they built it, and A is the model most managed-database products start
with precisely because it is boring and safe.

**This fork is worth deciding before the other four, because it can make them moot.** Deciding it
does not commit anyone to building anything.

## The operational cost of A, which is not the obvious one

The obvious cost of A is money — a Postgres and an idle API per customer. The one that actually bites
a small team is **fleet upgrades**, and it scales with customers rather than with usage.

One thing works strongly in Praxy's favour here: **migrations run themselves at startup**, guarded by
a `pg_advisory_lock` in `CatalogMigrator`, so updating an instance is "pull the new image, restart" —
the container migrates its own database. There is no separate migration step to orchestrate across
the fleet, which is the part that usually makes this miserable.

What remains hard is real:

- **A failed migration is a failed startup.** Because migration runs on boot, one that works on every
  dataset you tested and dies on tenant #47's leaves that tenant down, discovered from monitoring
  rather than from a deploy failing in front of you. On one shared instance you find out once; across
  N you find out N times, asynchronously, mid-rollout.
- **Version skew becomes permanent.** Some tenants land on the new version and some don't. Support
  burden forks, and so does the API surface you have to keep answering questions about.
- **Backward compatibility stops being optional.** On one instance a breaking change can be
  coordinated. Across a fleet it cannot — every migration has to work against every version still
  rolling out.

Rough shape of when this matters, for a small team: **at tens of tenants A's fleet ops is a cron job
and some scripts; in the low hundreds it becomes a real job**; past that you want C, and C wants the
isolation work.

**So A defers the isolation work rather than escaping it.** That is still worth a great deal — it
buys time, and time spent learning what customers actually need beats time spent building isolation
for a product nobody has bought yet — but it is a deferral, and worth saying plainly rather than
discovering at tenant 200.

## What already exists, in Praxy's favour

Checked, not assumed — and each of these makes B cheaper than it looks:

- **The tenant seam is already there and load-bearing.** `Organization`/`OrganizationMember`
  (`owner`/`member`) date from Phase 0, projects belong to orgs, org-level quotas are enforced
  (`QuotaService.GetOrgLimitsAsync`), and authorization already joins through `OrganizationMembers`
  in the console, project and realtime endpoints. Missing is only the lifecycle the entity's own
  comment names — *"creating, renaming and multi-org switching still do not exist"* — plus operator
  OAuth, which `CLAUDE.md` already defers to exactly this work. That is ordinary feature work and it
  is needed for **either** fork, since both need orgs, invites and billing identity.

- **The Docker client is confined to two files.** The whole codebase:

  ```
  $ grep -rl "IDockerClient\|DockerClientBuilder\|Docker.DotNet" src/ --include='*.cs'
  src/Praxy.Functions/DockerExecutor.cs
  src/Praxy.Sites/SiteDockerExecutor.cs
  ```

  Fifteen other files consume container behaviour and all fifteen go through those two — the same
  seam discipline `IFileStore` gets in Storage, arrived at without anyone naming it. The contract a
  replacement must meet is narrow: build an image from a tar, start something reachable at a
  `host:port`, stop it, fast enough that the warm pool and preview cold starts still make sense.
  gVisor/Kata, Firecracker and a remote build service all satisfy that.

- **The daemon endpoint and network are already configuration**, on both executors
  (`Praxy:Functions:DockerEndpoint`/`DockerNetwork`, and the Sites equivalents). They are currently
  *instance-wide*; the change B needs is to make them resolve **per tenant** — a lookup at a seam that
  already exists, not a new mechanism.

- **Superuser is needed for exactly one statement, at migration time.** The only `CREATE EXTENSION` in
  the codebase is PostGIS, in `20260902022849_EnablePostGis`, whose own comment records that it works
  because the compose file's `praxy` role happens to be the bootstrap superuser. Runtime DDL —
  `CREATE SCHEMA px_<hex32>`, table and index creation — needs `CREATE` on the database, which is
  grantable. **So "run the application as a non-superuser" is plausibly a configuration and migration
  change rather than a redesign**, and it is the single largest defence-in-depth win available for a
  shared-instance managed product, because today application code is the only thing between one
  tenant's SQL and every other tenant's data.

## What each fork costs

| | A — instance per tenant | C — cells | B — one shared instance |
|---|---|---|---|
| Docker socket | untouched | tenant-aware executor | tenant-aware executor |
| Postgres superuser | untouched | per-tenant roles | per-tenant roles |
| Flat network | untouched | per-tenant networks | per-tenant networks |
| Content origin | untouched | separate origin | separate origin |
| Org lifecycle | **needed** | **needed** | **needed** |
| Per-customer cost | a Postgres + an API each | amortised per cell | shared |
| Fleet upgrades | **O(tenants)** | O(cells) | one operation |
| Blast radius of a bad release | one tenant | one cell | everyone |

The row that matters: **the org lifecycle is required either way**, and it is the only line item both
forks share. It is also the least risky thing on this page.

## Recommendation

> **Working direction, taken 2026-09-06: fork A (one instance per tenant), and start the org
> lifecycle now.** Recorded so future sessions build against a consistent assumption, not because
> anything is committed — nothing is scheduled and no infrastructure exists. Taken knowing A is a
> *first* answer: its fleet cost grows per tenant, so success pushes toward C, which needs the same
> isolation work as B. **Revisit at the low hundreds of tenants, or sooner if free-tier economics
> demand it.** The lifecycle work below is fork-neutral, so this direction can change without wasting
> it.

**Build the org lifecycle when you want managed hosting; decide the fork before anything else.**

The lifecycle work — create/rename/switch, member invites, operator OAuth — is needed under both
models, is ordinary feature work against a seam that already exists, and commits you to neither fork.
It is the only part of this whole document that can be started without deciding anything first.

**On the fork: start with A, expecting to end at C.** A makes the managed product the self-hosted
product plus provisioning, so the two versions stay the same software — the thing that keeps
self-host honest and lets one team maintain both. It is also the only model that needs none of the
four blockers solved, which is why it is the right *first* answer.

Be clear-eyed that it is a first answer. A's fleet cost grows per tenant (above), so success pushes
toward C — and C needs the same isolation work as B. **A buys time, it does not remove the work.**
Choosing it deliberately, knowing that, is very different from choosing it because the four blockers
looked expensive.

**On the executor specifically: don't pick a replacement, and don't spike one.** The seam is already
contained to two files; the whole near-term discipline is refusing any change that puts a Docker call
or Docker-specific assumption outside them. That is a standing review rule, not a project, and it
preserves every option.

**Nothing here should slow current feature work**, geo Phases 4–5 included — they touch none of it.

## What this document does not decide

- **When.** Nothing here is scheduled.
- **A or B.** That is the owner's call and it is a business decision (free-tier economics) at least as
  much as a technical one.
- **Pricing and quota policy.** `QuotaService` already enforces org-level limits; what they should be
  for paying strangers is a business question.
- **Whether the managed product is worth building at all.** Self-host-only remains a coherent answer,
  and it makes all four designs permanently correct rather than deferred.
