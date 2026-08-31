# Session task — Sites build caching

## Why this exists

A self-host comparison against Appwrite (2026-08-30, informal research session) watched a real Appwrite
Sites build stream structured log lines confirming real cache reuse between deployments of the same
site: `Build cache miss.` on the first build, then on a later one, `Pruned build-only cache
.next/cache/webpack (31.1M)`, `Pruned build-only cache .next/cache/swc (12.0K)`,
`Build cache saved. (125575168 bytes)`. Praxy's Sites builds every deployment from scratch every time.

The root cause is concrete and small: `SiteRuntimeTemplates.Dockerfile(...)`
(`src/Praxy.Sites/SiteRuntimeTemplates.cs`) generates a builder stage that does `COPY . .` *before*
`RUN npm install` (lines ~125–128). Every deployment changes at least one file in the uploaded tar, so
this ordering invalidates Docker's own `npm install` layer on every single build, even when
`package.json`/`package-lock.json` haven't changed — even though nothing in `SiteDockerExecutor.
BuildImageAsync`'s `ImageBuildParameters` disables Docker's build cache (no `NoCache: true` is set, and
the per-deployment unique `imageTag` doesn't defeat layer caching — Docker's local image store caches by
layer content hash, not by the tag being built). This is a Dockerfile-ordering bug, not a Docker-daemon
or `Docker.DotNet` limitation.

Read `src/Praxy.Sites/SiteRuntimeTemplates.cs` (the `Dockerfile` method) and `SiteDockerExecutor.cs`
(`BuildImageAsync`) in full before writing anything. Work on a new branch off `main`. Read `CLAUDE.md`
first.

## Non-goals — do not build these

- **No BuildKit migration in this task.** `SiteDockerExecutor.BuildImageAsync` calls `Docker.DotNet`'s
  `BuildImageFromDockerfileAsync`, which the code itself already flags as obsolete
  (`#pragma warning disable CS0618`) — it's the classic builder API, not BuildKit. Whether Praxy should
  move to a BuildKit-based build path is a real question but a separate research task (per `CLAUDE.md`'s
  package-pinning rule, anything touching how images are built needs its own look at
  `docs/research/dotnet-stack.md`-style verification before landing) — don't fold it into this one.
- **Not required: persisting `.next/cache` (Next.js's own webpack/SWC cache) between deployments.** This
  is what Appwrite's log lines actually showed and is a deeper win than the Dockerfile fix below, but it
  needs either BuildKit cache mounts or a filesystem-level mechanism to carry `.next/cache` forward
  between builds of the *same* site — genuinely more work and more risk (stale cache correctness) than
  the required fix. Treat it as an optional stretch goal (see Scope #2) and land the required fix (Scope
  #1) regardless of whether you attempt it.
- **No changes to Sites' env-var-at-build-time behavior, root-directory support, or anything else in
  `SiteRuntimeTemplates`/`SiteBuildWorker` unrelated to caching.**

## Scope

1. **Required: reorder the generated Dockerfile.** In `SiteRuntimeTemplates.Dockerfile(...)`, split the
   dependency-install step out ahead of the full source copy:
   ```
   COPY package.json package-lock.json* ./
   RUN npm install
   COPY . .
   {buildArgs}RUN npm run build
   ```
   (Adjust for the actual root-directory handling already in that method — `rootDirectory`/`appDir`
   prefixing applies the same way it does today.) This alone lets Docker's already-enabled local layer
   cache skip `npm install` whenever the lockfile is unchanged between two deployments of the same site,
   with no new infrastructure. Verify concretely, not just by reading the diff: deploy the same site
   twice with only an app-code change (no dependency change) between builds, and confirm the second
   build's log stream shows the install layer as cached (Docker's classic builder emits a status/stream
   line indicating a cache hit — `SiteDockerExecutor.BuildImageAsync` already parses `stream`/`status`
   fields out of the NDJSON response; capture and surface what that line actually looks like once you
   have a real build to look at, since this prompt is written without one in hand).
2. **Optional stretch — persist `.next/cache` across deployments of the same site.** Only attempt this
   after #1 is done and verified. Research both candidate mechanisms before picking one, and record the
   choice and why in the report:
   - BuildKit `RUN --mount=type=cache,target=.next/cache` — requires confirming whether `Docker.DotNet`
     (or a raw call to the daemon's build API) can invoke a BuildKit build at all from this codebase's
     current dependency surface. If it can't without real new plumbing, that's a strong signal to stop
     here rather than force it.
   - A filesystem-level approach fully under Praxy's control: after a successful build, copy that
     deployment's container's (or a build-context scratch dir's) `.next/cache` directory somewhere
     durable per-site, and inject it into the next build's context before invoking `docker build` — no
     BuildKit dependency, but needs its own correctness care (don't reuse a stale cache across a
     dependency change; don't let this leak between different sites/projects).
   If you attempt this and it doesn't clearly work by the time you're done, land #1 alone and write up
   what you tried and why it didn't land — a half-working cache is worse than no cache.

## Tests

`tests/Praxy.Tests.Integration/` — extend the existing Sites build tests (or add
`SiteBuildCachingTests.cs`) with a real-Docker-daemon test: build the same site twice with only a
trivial app-code diff between builds, and assert the second build's log output evidences a cache hit on
the dependency-install layer (don't just assert both builds succeed — that would pass even with no
caching at all).

## Done means

- `dotnet test` green (real Docker daemon required for the new test).
- Owner click-tests two consecutive real deploys of the bundled `nextjs-starter` (or their own app) with
  only an app-code change between them, and confirms the second build is visibly faster / shows a
  cache-hit log line.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/sites-build-caching-report.md` — include the before/after build-time numbers from
  the owner test; that concrete evidence is worth recording even for a small fix.
