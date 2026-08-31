# Sites build caching — report

Branch: `sites-build-caching`. Kickoff: `docs/handoff/sites-build-caching-prompt.md`.

## Root cause

`SiteRuntimeTemplates.Dockerfile(...)` (`src/Praxy.Sites/SiteRuntimeTemplates.cs`) generated:

```
WORKDIR /app
COPY . .
WORKDIR {appDir}
{buildArgs}RUN npm install
RUN npm run build
```

`COPY . .` came before `RUN npm install`. Docker's classic builder (the one `SiteDockerExecutor.
BuildImageAsync` actually calls — `Docker.DotNet`'s `BuildImageFromDockerfileAsync`, obsolete but not
BuildKit) invalidates every layer from the first changed one onward. Since the uploaded tar changes on
literally every deployment (that's the app-code change being deployed), the `COPY . .` layer always
missed, which meant `RUN npm install` right after it always missed too — even when `package.json`/
`package-lock.json` hadn't changed at all. Nothing was disabling Docker's cache (`NoCache` is never set
on `ImageBuildParameters`, and the per-deployment unique `imageTag` doesn't defeat layer caching — the
local image store caches by layer content hash, not by the tag being built); the cache was simply never
given a chance to hit.

## Fix (Scope #1 — required)

Split the dependency-install step out ahead of the full source copy:

```
WORKDIR {appDir}
COPY {pkgPrefix}package.json {pkgPrefix}package-lock.json* ./
RUN npm install
WORKDIR /app
COPY . .
WORKDIR {appDir}
{buildArgs}RUN npm run build
```

`pkgPrefix` is `""` for the default (empty) `rootDirectory`, or `"{rootDirectory}/"` for a subdirectory
app — mirrors how `appDir` itself is already derived, so a root-directory app's dependency files are
addressed by the same tar-relative path the existing full `COPY . .` already uses. Now `package.json`/
`package-lock.json*` alone determine that layer's cache key: a redeploy that only changes app code
reuses the cached `npm install` layer; a redeploy that changes a dependency still gets a real install.

`COPY package-lock.json*` (trailing glob) copies the lockfile when present and is silently a no-op when
absent — the bundled `nextjs-starter` has no lockfile, and that path is exercised by the new integration
test.

## Verification

**Unit** (`tests/Praxy.Tests.Unit/SiteRuntimeTemplatesTests.cs`): a new test,
`Dependency_install_layer_is_ordered_before_the_full_source_copy_for_docker_layer_caching`, asserts the
generated Dockerfile text has `COPY package.json package-lock.json* ./` → `RUN npm install` → `COPY . .`
→ `RUN npm run build`, in that order. The existing root-directory test also now asserts the prefixed
form (`COPY apps/web/package.json apps/web/package-lock.json* ./`). 380 unit tests green.

**Integration** (new `tests/Praxy.Tests.Integration/SiteBuildCachingTests.cs`, real Docker daemon
required): deploys the same site twice — a package.json made unique per test run (so a stale host-level
cache from a previous run can't produce a false pass), byte-identical between the two deployments, plus
one file that only changes in the second deployment (a trivial app-code edit, no dependency change).
Reads each deployment's `buildLog` back through the real API and isolates the `RUN npm install` step's
own slice of the log. Asserts:
- the **first** deployment's install step does **not** say `Using cache` (a genuine first build — this
  guards against the assertion being trivially true)
- the **second** deployment's install step **does** say `Using cache`

This was run against a real Docker daemon locally (Docker Desktop, classic builder) — not just written
and trusted: `Redeploying_with_only_an_app_code_change_reuses_the_cached_npm_install_layer` passes in
~22s.

While writing the test, the exact classic-builder cache-hit log shape was captured directly against the
daemon's `/build` HTTP API (the same one `BuildImageFromDockerfileAsync` calls under the hood): each
Dockerfile instruction streams as its own `"Step N/M : <instruction>"` line, and a cache hit on that
layer immediately follows with `" ---> Using cache\n"`. That's the literal string the test (and this
report's before/after run below) keys off.

### Before/after build time (owner-test evidence)

Built the bundled `Templates/nextjs-starter/` (real `next@16.3.1`/`react@19.2.8`/`react-dom@19.2.8`
dependencies, no lockfile) through the same classic-builder API path twice, timing each with only
`app/page.js` edited between them — no dependency change:

| Build | Change | Wall time | `npm install` step |
|---|---|---|---|
| v1 | first-ever build | **87.1s** | real install (no cache to hit) |
| v2 | `app/page.js` text only | **43.7s** | `Using cache` |

**~50% faster** on the second build, entirely from skipping the real `npm install` of `next`/`react`/
`react-dom`. The remaining ~44s is `next build`'s own compilation — not addressed by this fix; see the
stretch-goal section below for why that's a separate, larger piece of work.

## Stretch goal (Scope #2) — researched, not attempted

Both candidate mechanisms for persisting Next.js's own `.next/cache` (webpack/SWC — what Appwrite's log
lines in the kickoff prompt actually showed) were researched:

**BuildKit `RUN --mount=type=cache,target=.next/cache`.** Confirmed directly against a real daemon while
capturing the log format above: `SiteDockerExecutor.BuildImageAsync` calls `Docker.DotNet`'s
`BuildImageFromDockerfileAsync`, which drives the daemon's *classic* (non-BuildKit) `/build` endpoint —
the same endpoint this report's manual probes hit over the Unix socket. The classic builder's Dockerfile
frontend does not understand `RUN --mount=...` syntax at all; that's a BuildKit-frontend-only feature
(`# syntax=docker/dockerfile:1`), reachable only through BuildKit's session/frontend protocol, which
`Docker.DotNet`'s obsolete method (and the `ImageBuildParameters` shape it accepts) has no support for.
Using it would mean moving Sites' build path off the classic builder entirely first — exactly the
BuildKit migration the kickoff prompt's non-goals section rules out as its own separate task. Stopped
here, per the prompt's own guidance ("if it can't without real new plumbing, that's a strong signal to
stop").

**Filesystem-level cache carried between builds.** Possible without BuildKit, but not cheap: the
multi-stage Dockerfile's `builder` stage (the one that would hold `.next/cache`) isn't tagged or
addressable after a normal build — only the final `runner` stage's image ID comes back. Getting
`.next/cache` out would need a second `Target: "builder"` build call per deployment (Docker.DotNet's
`ImageBuildParameters` does expose `Target`) to obtain a taggable reference, a throwaway container
create + archive-extract to pull the directory out, durable per-site storage on the host with its own
lifecycle (created, grown, evicted — nothing today owns that), and re-injection into the *next* build's
context before the build even starts (which itself only helps if injected before `RUN npm run build`,
i.e. as part of the tar `SiteRuntimeTemplates.BuildContextAsync`/`BuildContextFromDirectoryAsync`
assemble) — plus correctness care so a stale cache is never reused across a dependency change and never
leaks between sites or projects. That's a genuinely separate feature (new host storage, a second build
invocation, container-archive plumbing, its own tests for the leak/staleness cases), not a small addition
to this fix, and its risk profile (a stale-cache correctness bug silently serving wrong output) is worse
than shipping nothing. Per the kickoff prompt's own instruction — "a half-working cache is worse than no
cache" — this was not attempted. Scope #1 stands alone as this session's deliverable.

## Files changed

- `src/Praxy.Sites/SiteRuntimeTemplates.cs` — the Dockerfile reorder (the actual fix) plus an updated
  doc comment explaining why.
- `tests/Praxy.Tests.Unit/SiteRuntimeTemplatesTests.cs` — new ordering test, updated root-directory test.
- `tests/Praxy.Tests.Integration/SiteBuildCachingTests.cs` — new, real-Docker-daemon cache-hit test.
- `docs/roadmap.md` — one paragraph under the Sites section.
- This report.

## Done means

- [x] `dotnet test` green — 380 unit; Sites integration suite (`SiteTests`, `SiteGitDeploymentTests`,
  `SiteCustomDomainTests`, `SitesAskTlsTests`, `SiteBuildCachingTests`) run against a real Docker daemon.
- [x] Before/after build-time numbers captured against the real bundled `nextjs-starter` (table above).
- [x] `git status` clean, conventional commits, on branch `sites-build-caching` off `main`.
- [ ] Owner click-test: deploy the same site twice from the console with only an app-code change between
  them, and confirm the second build is visibly faster / shows a cache-hit line in the build log — the
  automated integration test above exercises the exact same path, but this is explicitly the owner's own
  gate per `CLAUDE.md`.
