# Untrusted multitenancy — architecture assessment

## Context

The owner decided (2026-09-06) that Praxy will **eventually** host *untrusted* tenants — a hosted
product where people who don't know each other deploy code onto one instance — rather than only
multiple trusted teams inside one organization.

That decision doesn't start the work. It does change what the work *is*, and it makes some
architectural choices urgent well before any of it is built, because several current designs are
things a later multitenancy release would have to undo rather than extend.

This document records what untrusted tenancy actually requires, established by reading the running
system rather than by speculation — every claim below was checked against the code or the live
praxycore.dev instance, and the check is quoted. It is deliberately **not** a phase plan: producing a
phase-1 prompt now would invite someone to start building, and the owner said "eventually."

Companion to `docs/research/security-review.md`, whose three phases produced the prerequisite list
this builds on (`docs/handoff/security-review-phase-3-report.md` §5). That review rated every finding
under two actor models precisely so this document could exist.

## The good news: the tenant seam is already there

`Organization` and `OrganizationMember` (`owner`/`member`) have been modeled since **Phase 0**.
Projects belong to organizations, org-level quotas are real and enforced
(`QuotaService.GetOrgLimitsAsync`), and authorization already joins through `OrganizationMembers` in
the console, project and realtime endpoints. This is load-bearing code, not a vestigial table.

What's missing is the lifecycle around it — the entity's own doc comment says it: *"creating,
renaming and multi-org switching still do not exist."* Plus operator OAuth, which `CLAUDE.md`
explicitly defers to "future multitenancy work."

So multitenancy is **not** a data-model migration. That is the cheapest part of this, and it is not
what should drive the schedule.

## Four structural blockers

None of these is a bug. Each is a design that is correct for one trusted operator and wrong for
untrusted tenants — which is exactly the class the security review kept flagging, now collected.

### 1. The Docker socket — root-equivalent host access, per build

`deploy/docker-compose.yml` mounts `/var/run/docker.sock` into the api container so Functions and
Sites can build and run sibling containers. The compose file has documented this as root-equivalent
host access since Phase 7, and all three security-review phases explicitly declined to re-litigate it.

Under untrusted tenancy it stops being a tradeoff and becomes fatal: a tenant's build has the same
access as the daemon, so any tenant can take the host and therefore every other tenant. No permission
work fixes this — it needs the execution mechanism replaced.

**This is the long pole, and the only one that constrains what should be built now** (see "What this
constrains today").

### 2. One Postgres identity, and it is a superuser

Verified live:

```
SELECT rolname, rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname='praxy';
praxy|t|t|t
```

The entire application connects as one role, and that role is a **superuser**. Tenant data is
separated by schema (`px_<hex32>`, `PhysicalNaming.SchemaName`), not by database role — so isolation
between tenants is enforced **entirely in application code**: the query compiler, the metadata
lookups, and `CLAUDE.md`'s "SQL identifiers never come from request strings" rule.

For one operator that is proportionate. For untrusted tenants it means a single SQL-injection or
query-compiler flaw is not a tenant-boundary break but a total one — and a Postgres superuser can
`COPY ... FROM PROGRAM`, read server files, and load extensions, so it reaches past the database
entirely. There is no second layer.

### 3. Tenant containers share a flat network, and can reach each other

Verified live on `praxy-functions`: `ICC=` (unset, so Docker's default *enabled*) and
`internal=false`. Every function container on the instance sits on one bridge network, as does every
site container on `praxy-sites`.

Security-review Phase 1 removed Postgres from the functions network — that was Finding A, and it was
the right fix — but tenant-to-tenant reachability was never in question then, because every container
belonged to the same operator. Under untrusted tenancy, one tenant's function can connect directly to
another tenant's running function or site container, and both can reach `api`.

### 4. Tenant content is served from the console's own origin

`InlineTypes`' own remarks say it plainly: a file's MIME type is whatever the uploader sent, and *"the
console is served from the API's own origin with a `SameSite=Lax` operator cookie."* That is why
inline serving is two gates against a hard-coded safe list with `text/html` and `image/svg+xml`
permanently excluded, and why a stored XSS was possible in the first place (Storage Phase 2).

The file goes on to name the real fix: *"serving user content from a separate origin, the way Sites
already does — an owner decision recorded in docs/research/storage.md rather than something this
phase assumes."* Under untrusted tenancy that stops being risk management and becomes structural: an
allowlist is a bet that no entry on it is ever renderable-and-scriptable, made against attackers who
are now strangers rather than the operator themselves.

## Carried from the security review, still open

Both are contained fixes that don't constrain anything else, and can land whenever
(`docs/handoff/security-review-phase-3-report.md` §5):

- **`PRAXY_FUNCTION_API_KEY` never expires** and can hold any scope an operator grants (Finding M) —
  acceptable when the only party who can grant it is the only party harmed by leaking it; not once
  scopes can be granted by or on behalf of one tenant in a way that touches another.
- **No installation-to-repository ownership** in the git integration (Finding E) — a hard prerequisite
  specifically for letting untrusted developers connect their own repositories.

## What this constrains *today*

This is the actionable part, and the reason this document exists now rather than when the work starts.

**Only blocker 1 constrains present work.** Blockers 2–4 are additive: a per-tenant database role, a
segmented network, and a separate content origin can each be introduced later without unwinding
anything built in the meantime. The Docker socket cannot — every Functions and Sites feature is built
on "the api process can drive the daemon directly," and replacing that mechanism changes the shape of
build, deploy, warm pooling, log streaming and container lifecycle at once.

So: **new Functions/Sites work is fine; work that spreads Docker knowledge is rework waiting to
happen.** That has a concrete test rather than a judgement call — see the next section: the Docker
client is currently confined to two files, and the standing rule is to keep it there.

Nothing else here should slow current feature work down, including geo Phases 4–5, which touch none
of it.

## The decision to make early

Not now, but before Functions/Sites gain much more surface: **what replaces the Docker socket.** The
realistic families, with what each costs:

- **Sandboxed runtimes** (gVisor, Kata) — closest to a drop-in: containers stay containers, the
  isolation boundary gets stronger. Cheapest to adopt, weakest of the three, and gVisor's syscall
  surface has its own escape history.
- **MicroVMs** (Firecracker, Cloud Hypervisor) — a real hardware boundary per tenant, the model every
  serious multi-tenant FaaS converged on. Materially more operational work: image pipeline, network
  plumbing, and cold starts stop being a Docker concern.
- **A remote build/run service** — keeps the api process away from any daemon entirely and moves the
  problem behind an API. Best separation of concerns, most infrastructure, and it changes self-hosting
  from "one compose file" into something with a second moving part — which is a product decision, not
  just an architectural one, given `deploy/up.sh`'s one-question setup is a deliberate selling point.

**Recommendation: this decision can wait, and that is a verified answer rather than a hopeful one.**
The obvious worry is that daemon assumptions have leaked across both subsystems, making the eventual
swap a rewrite. They haven't. Every reference to the Docker client in the entire codebase:

```
$ grep -rl "IDockerClient\|DockerClientBuilder\|Docker.DotNet" src/ --include='*.cs'
src/Praxy.Functions/DockerExecutor.cs
src/Praxy.Sites/SiteDockerExecutor.cs
```

Two files. Fifteen other files consume Functions/Sites container behaviour, and all fifteen go through
those two classes — the same seam discipline `IFileStore` gets in Storage, arrived at without anyone
naming it that.

What a replacement has to satisfy is therefore not "everything Docker does" but the narrow contract
those two expose: build an image from a tar, start something reachable at a `host:port`, stop it, and
do the start/stop fast enough that a warm pool and on-demand preview cold starts still make sense
(`RunningContainer`/`RunningSiteContainer` are the whole shape). **Firecracker and a remote build
service can both satisfy that contract** — microVMs get their own addresses, and a remote builder
returns one — so none of the three families is architecturally excluded by anything already built.

The practical consequence: **don't pick now, and don't spike now.** Instead keep the seam honest —
any change that would put a Docker client call, or a Docker-specific assumption, outside those two
files is the thing to refuse in review. That is a cheap standing rule rather than a project, and it
preserves every option until the work is actually scheduled.

## What this document does not decide

- **When** any of this happens. The owner said eventually; nothing here argues otherwise.
- **The org lifecycle** (create/rename/switch, invites, operator OAuth) — ordinary feature work
  against a seam that already exists, and deliberately not scoped here so it isn't mistaken for the
  hard part.
- **Pricing, plans, or quota policy.** `QuotaService` already enforces org-level limits; what those
  limits should *be* for paying strangers is a business question.
- **Whether Praxy should host untrusted tenants at all**, versus shipping self-host and letting others
  run their own. That is the owner's call and it is worth making explicitly, because "self-host only"
  makes every blocker above permanently acceptable rather than deferred.
