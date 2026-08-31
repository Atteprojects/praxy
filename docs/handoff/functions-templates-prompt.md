# Session task — Functions starter templates

## Why this exists

A self-host comparison against Appwrite's own Sites/Functions (2026-08-30, informal research session,
not a written doc — this prompt is the record of it) found Praxy's single biggest gap-per-effort: Sites
already has exactly one bundled "click to deploy something real" template
(`src/Praxy.Sites/SiteStarterTemplate.cs`, a `nextjs-starter` tar shipped next to the assembly and
deployed via `SitesPage.tsx`'s "Starter template" card / `useDeploySiteStarterTemplate`), but Functions
has **zero** — `RuntimeTemplates.cs` in `src/Praxy.Functions/` is internal Dockerfile-generation
machinery, not a user-facing example. Appwrite ships 40 curated real-world function templates (Stripe
payments, scheduled jobs, webhook receivers, etc.); Praxy already has every piece of plumbing this
needs (tar upload, `FunctionBuildWorker`, encrypted `FunctionEnvVars`) and one working precedent to
mirror almost exactly. This is a small, self-contained addition, not a new subsystem.

Read `src/Praxy.Sites/SiteStarterTemplate.cs` and `SitesPage.tsx`'s `CreateSiteModal` in full before
writing anything — this task is that pattern, restructured for one-of-several templates instead of one,
applied to `Praxy.Functions`. Work on a new branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **Not a 40-template gallery.** Ship 3–4 real, Praxy-shaped starters (see Scope). A filterable gallery
  UI, a "Browse all templates" page, use-case/runtime filter dropdowns — all deferred; a flat list of a
  few cards in the existing "Create function" modal is enough for this session.
- **No literal ports of Appwrite's integration templates** (Stripe, ChatGPT, Discord, WhatsApp/Vonage).
  Those need third-party SDKs Praxy doesn't vendor and credentials this task shouldn't need to explain
  how to obtain. Pick starters that demonstrate Praxy's own primitives instead — see Scope.
- **No new runtimes.** Templates target the two runtimes that exist today, `dart` and `node`
  (`FunctionRuntimes.cs`). Do not add Python/PHP/Ruby support to make room for a template — that's a
  separate, much bigger task if it ever happens.
- **No template marketplace/contribution system.** Templates are bundled with Praxy itself, the same way
  `SiteStarterTemplate` is — not user-submitted, not fetched from an external registry.
- **No changes to `Praxy.Vcs` or git-sourced deployments.** Templates deploy via the existing tar-upload
  path only, exactly like `SiteStarterTemplate` does for Sites.

## Scope

1. **3–4 bundled template tars** under `src/Praxy.Functions/Templates/<template-key>/`, added as a
   `Content` item in `Praxy.Functions.csproj` (mirror `Praxy.Sites.csproj`'s existing item for
   `Templates/nextjs-starter/`). Suggested starters, each demonstrating a real Praxy primitive rather
   than a generic "hello world":
   - **HTTP echo starter** (`node` or `dart`) — the true minimal starter, closest analogue to Appwrite's
     "Starter function".
   - **Scheduled cleanup job** (`node`) — a `schedule`-triggered function reading/writing Tables data.
     Note while building this one: as of this prompt, a schedule-triggered execution gets no
     `PRAXY_FUNCTION_JWT` (see `FunctionExecutionService.BuildEnvAsync` — that env var is only minted for
     `TriggeredBy` starting with `"user:"`), so this template currently has no built-in way to
     authenticate a Tables call. If `docs/handoff/functions-scheduled-credentials-prompt.md` hasn't
     landed yet when you pick this up, either write this template to demonstrate the env-var-only path
     (a user-supplied `PRAXY_API_KEY`-shaped value, manually created via the existing `ApiKeysPage.tsx`
     flow) or swap this starter for one that doesn't need standing platform credentials — don't block this
     task on that one.
   - **Webhook receiver** (`node`) — an `http`-triggered function that validates a signature header and
     writes the payload to a Table, exercising `PRAXY_FUNCTION_JWT`'s user-triggered path where relevant.
2. **A template registry** (`FunctionTemplates.cs`, mirroring `SiteStarterTemplate`'s shape) exposing
   each template's key, display name, description, runtime, and entrypoint. A new
   `GET /v1/functions/templates` endpoint (unauthenticated read, same posture as any other static
   catalog) returns this list for the console to render.
3. **Console**: in `FunctionsPage.tsx`'s "Create function" modal, add a template picker alongside the
   existing manual-create path — structurally the same choice `SitesPage.tsx`'s `CreateSiteModal` already
   offers (`start === "template"` vs manual). A `useDeployFunctionTemplate` mutation hook
   (`console/src/api/functions.ts`) calls a new `FunctionsService.CreateFromTemplateAsync(projectId,
   templateKey, name, ...)` that builds the template's tar via `FunctionTemplates`, creates the function,
   and hands the tar to the existing deployment path unchanged.

## Landmines — read before writing code

- **Every bundled template must actually build.** `SiteStarterTemplate`'s tar-generation is exercised
  indirectly by the Sites owner-test; write a real integration test (Docker daemon required) that builds
  each template through the actual `FunctionBuildWorker`/`DockerExecutor` path and asserts success —
  don't just assert the tar bytes look reasonable.
- **Don't auto-fill secret-shaped env vars.** Appwrite's template env vars are safe to auto-fill because
  they're Praxy's own public connection details (project id, endpoint) — there's currently no
  `PRAXY_ENDPOINT`-equivalent env var Praxy injects into a function's runtime (only
  `PRAXY_FUNCTION_ID`/`PRAXY_PROJECT_ID`/the conditional JWT — see `FunctionExecutionService.BuildEnvAsync`).
  If a template needs to know its own endpoint, decide whether to add that env var as part of this task
  or have the template read it from a required, user-filled env var — don't invent a third mechanism.
- **`FunctionsService.CreateFromTemplateAsync` should reuse the existing create + deploy calls, not
  duplicate their validation.** Same discipline `SitesService`'s starter-template path already follows.

## Tests

`tests/Praxy.Tests.Integration/` — a new `FunctionTemplateTests.cs`: each bundled template builds
successfully via a real Docker daemon; `GET /v1/functions/templates` returns the expected keys; creating
a function from a template produces a function + an activated deployment, same as a normal upload would.

## Done means

- `dotnet test` green (real Docker daemon).
- `tsc -b && vite build` clean.
- Owner click-tests creating a function from each new template and invoking it for real.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/functions-templates-report.md`.
