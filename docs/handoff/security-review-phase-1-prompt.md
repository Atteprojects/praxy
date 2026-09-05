# Session task — Security review, Phase 1: the container boundary

> **Status: shipped.** See `docs/handoff/security-review-phase-1-report.md`.

## Why this exists

Storage Phases 1–3 and their follow-up shipped a stored XSS (introduced by a *design document*, not an
implementation slip), a production crash, three naming 500s, two transform bugs found only by testing
against a running instance, and a flaky test. **The test suite caught none of them.** That defect rate
is the case for reading the post-v0.1.0 subsystems adversarially instead of adding to them.

Read `docs/research/security-review.md` first — the threat model, the actor definitions, and the
report format below all come from it — then `CLAUDE.md`. Work on a new branch off `main`.

**This is Phase 1 of three, and it is the one that matters most**: Functions and Sites are the only
place in Praxy where untrusted *code* executes.

## This is a review, not a feature phase — read this before scoping

Every other prompt in `docs/handoff/` lists behaviour to build. This one cannot, because the findings
are unknown until you look. That changes how you work and how you will be judged:

- **The deliverable is findings.** "Implemented everything in the scope list" is the wrong success
  measure here and would reward exactly the wrong behaviour.
- **Record every finding whether or not you fix it** — attack, impact, severity under *both* actor
  models (below), and either the fix or the explicit reason for accepting it.
- **Do not fix everything.** A review that tries to becomes a rewrite and ships nothing. Fix what is
  cheap and clearly right; document-and-accept what is expensive or risky; never redesign a shipped
  subsystem mid-review.
- **Every fix carries a regression test.** Missing tests are the whole reason this initiative exists.
- **No new features.** A feature idea found while reading becomes a note in the report, not a commit.

## The two actors — rate every finding under both

1. **Anonymous network caller** — anyone who can reach the instance, no credentials.
2. **Project developer** — an authenticated operator who can deploy functions and sites. **Today this
   is the owner and people they trust** (`CLAUDE.md` defers multitenancy).

A finding that is harmless under one trusted operator but critical the moment a second untrusted
developer shares an instance is **not "low"** — it is a **multitenancy prerequisite**, and the report
needs a section listing those. That is the durable output of this whole initiative: the thing that
stops multitenancy being designed years later on top of assumptions nobody wrote down.

**Out of scope: a host-root attacker.** `deploy/docker-compose.yml` already concedes that boundary in
a long inline comment — the Docker socket mount is root-equivalent host access, there is no sandboxed
alternative in this release, and removing it disables Functions entirely. **Do not re-litigate it.**
Mapping what *else* becomes reachable because of it is in scope; proposing to remove it is not.

## Two findings already verified — start here, do not stop here

Both were confirmed while scoping this review, one of them against the live praxycore.dev instance.
They are a starting point. **A review that only closes these two has not been done.**

### Finding A — function containers share a Docker network with the database

`deploy/docker-compose.yml` names the Compose `default` network `praxy-functions`. `postgres` declares
no `networks:` key, so it joins `default` — which *is* the network every function container is
attached to via `Praxy__Functions__DockerNetwork`. Confirmed live:

```
praxy-functions  internal=false  containers: praxy-api-1 praxy-caddy-1 praxy-postgres-1
praxy-sites      internal=false  containers: praxy-api-1 <site containers…>
```

Attacker-authored function code can open TCP straight to `postgres:5432`. It still needs the password
to do anything with it — but the network layer, the boundary that is supposed to hold when a
credential leaks or a Postgres CVE lands, is not there at all.

**Sites got this right and Functions did not, by accident**: `praxy-sites` is a genuinely separate
network with no database on it, while Functions inherited `default` purely as a side effect of the
rename. The symmetric fix is the obvious one — give Functions its own network the way Sites already
has one, with `api` joining both — but **check it properly rather than assuming**: `api` must still
reach function containers by IP on that network, and existing deployments need their containers
recreated for a topology change to take effect. Say in the report what an upgrading self-hoster has to
do, and whether any already-running function or site container is stranded on the old network.

Separately: both networks are `internal=false`, so containers have outbound internet egress. That is
probably correct (a function calling an external API is a normal thing to want) — but it is currently
an unexamined default rather than a decision. **Record it as a decision either way**; if you make it
configurable, that is a knob, not a default change.

### Finding B — neither executor sets any isolation flag beyond memory and CPU

`src/Praxy.Functions/DockerExecutor.cs` and `src/Praxy.Sites/SiteDockerExecutor.cs` build
near-identical `HostConfig` objects setting `Memory`, `NanoCPUs`, `AutoRemove` (plus `RestartPolicy`
for Sites). A repo-wide grep finds **zero** matches for `PidsLimit`, `CapDrop`, `SecurityOpt`,
`ReadonlyRootfs`, or `no-new-privileges`, neither executor sets `User`, and neither Dockerfile
template (`RuntimeTemplates.cs`, `SiteRuntimeTemplates.cs`) has a `USER` directive — so containers run
as whatever the base image defaults to.

**These are not equally cheap, and treating them as one checklist is the trap.** Grade them by blast
radius before applying:

- `PidsLimit` — cheap, high value (a fork bomb is currently unbounded), low breakage risk.
- `SecurityOpt: no-new-privileges` — cheap, low breakage risk.
- `CapDrop` — needs testing per runtime; a dropped capability that a runtime actually uses fails at
  run time, not build time.
- `ReadonlyRootfs` — **expect this to break Next.js** (build cache, `/tmp`) without matching tmpfs
  mounts. A perfectly good outcome is documenting why it is deferred.
- Non-root `User` — depends on what each base image expects; verify, don't assume.

**The two executors being near-identical is why they are one phase.** Whatever you decide applies to
both, consistently — or the report says explicitly why they differ.

## The rest of the surface — work through it, don't just close A and B

- **What lands in a container's environment.** `FunctionExecutionService` puts
  `PRAXY_FUNCTION_API_KEY`, `PRAXY_FUNCTION_JWT`, `PRAXY_FUNCTION_USER_ID` and decrypted user secrets
  into `env`. Who can read them back — `docker inspect`, the warm pool, a crash log, an execution
  record? Does a warm-pool container ever outlive the credential it was started with, or serve a
  second execution carrying the first one's env?
- **The warm pool as a boundary.** `WarmPool.cs` reuses containers. Can state from one execution reach
  the next — filesystem, memory, env, an open socket? Is a pooled container ever reused across
  *projects*?
- **Resource limits actually in force.** Memory and CPU are set; confirm they are enforced (a limit
  configured but silently ignored by the daemon is worse than none, because it reads as covered).
  Disk is not limited at all — check what a function writing to its own filesystem can do to the host.
- **Build as a supply-chain surface.** `FunctionBuildWorker`/`SiteBuildWorker` build images from
  caller-supplied source. `GitCliRepositoryCloner` shells out to `git` and already does the right
  obvious thing (`ArgumentList`, not string concatenation, so there is no shell to inject into) — the
  residual question is **option injection**: a repo URL or branch name that itself begins with `-` is
  still passed to `git` as a flag, not a value. Check whether a caller can supply one, and whether
  `--` separators or a validating parse stand between them and `git`. Base image tags: pinned or
  floating?
- **Container lifecycle.** `FunctionPoolSweeper`, `SitePreviewSweeper`, `SiteReconciler` — can a
  developer create containers faster than they are swept, or keep one alive past its quota? The
  per-project caps exist (`MaxPreviewContainersPerProject`); confirm they bind.

## Non-goals for this phase

The HTTP edge (Storage's transform/derivative path, `SiteProxyMiddleware`, `SiteHostPattern`,
preview-URL enumeration, request-log poisoning) is **Phase 2**. The permission model itself (Storage's
additive grants, credential scope, project isolation at every entry point) is **Phase 3**. Note
anything you spot in those areas in the report's "for later phases" section and move on — do not
start on them.

## Tests

Every fix gets a regression test. The shape that matters most: a test that asserts the *isolation
property*, not the config value. Asserting `HostConfig.PidsLimit == 256` passes forever even if the
field stops being honoured; asserting that a container cannot do the thing is what actually holds.
Where an integration test genuinely cannot express that (Testcontainers running the assertion inside
the daemon you are testing), say so in the report and describe the manual check you ran instead —
that is an acceptable answer here, an untested silent fix is not.

## Done means

- `dotnet test` green; console build clean if touched.
- **Every finding demonstrated before the fix and shown closed after**, against a running instance,
  for anything reachable at run time. Findings established by reading alone are marked as such and
  rated lower confidence — the transform bugs are the standing proof that reading is not enough.
- **Owner test, actually run**: deploy a function and a site on a real instance after the changes and
  confirm both still build, run and serve. A hardening change that breaks Functions is worse than the
  finding it closed, and Finding A's fix touches the network every function container joins.
- `git status` clean, conventional commits, branch off `main`.
- Write `docs/handoff/security-review-phase-1-report.md` in this exact shape, so the three phases'
  reports compose into one picture:
  1. **Findings table** — id, one-line title, severity under each actor model, fixed / accepted.
  2. **A section per finding** — what an attacker does, what they get, how it was demonstrated, the
     fix and its test (or the reason for accepting it).
  3. **Accepted risks** — with the reasoning, so each one is a decision on record rather than a
     surprise for whoever finds it next.
  4. **Multitenancy prerequisites** — everything that is fine today under one trusted operator and
     stops being fine with a second untrusted one.
  5. **For later phases** — anything spotted outside this phase's boundary.
- Write `docs/handoff/security-review-phase-2-prompt.md`, and update `CLAUDE.md`'s Commands section if
  any configuration changed.
