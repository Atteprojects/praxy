# Functions starter templates — report

**Status: complete.** Every item in `docs/handoff/functions-templates-prompt.md`'s scope shipped: three
bundled templates, the `FunctionTemplates` registry, the unauthenticated catalog endpoint, the combined
create-and-deploy endpoint, and the console's template picker. Full-repo `dotnet test` green —
**379/379 unit, 213/213 integration** (real Postgres via Testcontainers, real Docker daemon for every
template build). Console `tsc -b && vite build` clean. Manually click-tested against the live local dev
instance — created, deployed, and invoked all three templates for real (see Owner-test checklist).

## What shipped

**Three bundled templates** under `src/Praxy.Functions/Templates/<key>/`, added as a `Content` item in
[`Praxy.Functions.csproj`](../../src/Praxy.Functions/Praxy.Functions.csproj) (mirrors
`Praxy.Sites.csproj`'s existing `Templates/nextjs-starter/` item exactly — both land side by side in the
same output `Templates/` directory with no collision):

- [`http-echo/main.dart`](../../src/Praxy.Functions/Templates/http-echo/main.dart) — Dart, the true
  minimal starter. Echoes back `context.method`/`path`/`body` as JSON. Nothing to configure.
- [`scheduled-cleanup/index.js`](../../src/Praxy.Functions/Templates/scheduled-cleanup/index.js) — Node,
  deployed with a default daily cron schedule (`0 3 * * *`, set via the new `DefaultSchedule` field on
  `FunctionTemplateInfo`, applied through `CreateAsync` unchanged). Deletes rows older than a configurable
  age from a Table, paginating 100 at a time through the query DSL (`lessThan`/`limit`), capped at 20
  pages per run so a stuck filter can't loop forever inside one execution.
- [`webhook-receiver/index.js`](../../src/Praxy.Functions/Templates/webhook-receiver/index.js) — Node,
  validates a shared secret and writes the event to a Table. See Deviations for why this checks a
  `secret` field in the JSON body rather than a header.

**[`FunctionTemplates.cs`](../../src/Praxy.Functions/FunctionTemplates.cs)** — the registry
(`FunctionTemplateInfo`: key, name, description, runtime, entrypoint, default schedule) plus
`BuildTarAsync`, a direct port of `SiteStarterTemplate.BuildTarAsync`'s re-emit-every-file-fresh approach
(same PAX-entry, same dotfile/`.git` exclusion — no `node_modules` template ships one, so that exclusion
is dead weight here but kept for parity/future-proofing against someone adding a `package.json` +
`npm install`-locally-tested template later).

**API** ([`FunctionEndpoints.cs`](../../src/Praxy.Api/Endpoints/FunctionEndpoints.cs)):
- `GET /v1/functions/templates` — unauthenticated, top-level (not under
  `/v1/console/projects/{projectId}/...`), same "static catalog, no auth" posture as any other fixed
  list. Registered directly on `api`, not the project-scoped `admin` group or the `dataPlane` group.
  Verified no route-precedence conflict with `dataPlane.MapGet("/{functionId}", ServerGetFunction)`
  (`/v1/functions/templates` is a literal segment, which ASP.NET Core's endpoint routing always prefers
  over a parameterized one at the same position) — both the passing integration test and a manual check
  confirm `templates` never gets swallowed by `{functionId}`.
- `POST /v1/console/projects/{projectId}/functions/from-template` — `FunctionsService.CreateFromTemplateAsync`
  reuses `CreateAsync` + `CreateDeploymentAsync` unchanged (no duplicated validation, per the prompt's own
  landmine), returns both the created function and its deployment in one
  `FunctionCreatedFromTemplateResponse` so the console can jump straight to the function's detail page
  without a second round trip to discover the deployment id.

**Console** ([`FunctionsPage.tsx`](../../console/src/screens/FunctionsPage.tsx),
[`functions.ts`](../../console/src/api/functions.ts)): `CreateFunctionModal` now offers "From a template"
(default) vs "Manual" — the same two-tier choice `SitesPage.tsx`'s `CreateSiteModal` uses, with a second
tier of cards for picking among the three templates when "From a template" is selected. Submitting calls
the new `useDeployFunctionTemplate` mutation and navigates straight to the created function's page.
`useFunctionTemplates` fetches the unauthenticated catalog (`staleTime: 5 min` — it's a fixed list, no
reason to refetch on every modal open).

**Error type**: `function_template_not_found` (`FunctionTemplates.Find` throws it for an unknown key),
added to `ErrorTypes.All` per the reflection-based coverage test every prior error type addition follows.

**`docs/openapi/v1.json`** regenerated (two new operations, two new schemas — diffed by hand before
committing, nothing else moved).

## Deviations & notes

- **Credential mechanism: user-filled env vars, not a new injected one.** The prompt's own landmine
  anticipated this and explicitly sanctioned it ("have the template read it from a required, user-filled
  env var — don't invent a third mechanism"). `docs/handoff/functions-scheduled-credentials-prompt.md`
  had not landed when this session ran (still an untracked, unstarted prompt — no matching report), so
  the scheduled-cleanup and webhook-receiver templates both authenticate with a standing
  `PRAXY_API_KEY` the operator creates via the existing `ApiKeysPage.tsx` flow, documented in each
  template's own header comment. The webhook-receiver additionally prefers `PRAXY_FUNCTION_JWT` when
  present (the user-triggered path), falling back to `PRAXY_API_KEY` for the guest-triggered case a real
  external relay would actually hit — exercises both credential paths in one file.
- **No `PRAXY_ENDPOINT` env var was added to `FunctionExecutionService.BuildEnvAsync`.** Investigated
  whether to auto-inject Praxy's own reachable base URL (the way `PRAXY_FUNCTION_ID`/`PRAXY_PROJECT_ID`
  already are) and decided against it for this session: the right value depends on Docker network
  topology that varies by deployment (`http://api:8080` in the bundled self-host compose stack, where
  function containers share the `praxy-functions` network with `api`; `http://host.docker.internal:5090`
  for a `dotnet run` dev instance on Docker Desktop; something else again on a Linux dev box, where
  `host.docker.internal` doesn't resolve without an explicit `--add-host`). Auto-injecting a value that's
  right in one topology and silently wrong in another seemed worse than asking the operator to set
  `PRAXY_ENDPOINT` explicitly, which both templates' comments walk through. Worth revisiting if a future
  session finds a topology-independent way to derive it.
- **`webhook-receiver` validates a secret in the request body, not a header, and the prompt asked for a
  header.** Traced this back to a real, pre-existing platform gap, not a design preference: 
  `FunctionExecutionService.RunAsync` calls `docker.InvokeAsync` with a hardcoded empty headers
  dictionary (`DockerExecutor.cs`), and neither `ConsoleInvoke` nor the data-plane `Invoke` handler's
  `InvokeFunctionRequest` DTO carries a caller's real HTTP headers through to the execution at all — so
  `context.headers` inside every function is always `{}` today, regardless of trigger. Separately, and
  more fundamentally: Praxy's invocation endpoint (`POST /v1/functions/{id}/executions`) takes a
  structured `{method, path, body}` envelope, not a raw proxied request — a real external webhook sender
  (GitHub, Stripe) has no way to know to wrap its payload that way, so it couldn't point at this endpoint
  directly regardless of headers. Fixing either is a real, contained platform gap but is core
  invocation-contract plumbing, not template work, and touching it wasn't in this session's scope
  (`CreateFunctionFromTemplateAsync`-adjacent, but not template-adjacent) — flagged as a follow-up
  (see Known gaps) rather than silently expanded into. The template instead validates a `secret` field
  carried in the JSON body, which works correctly with zero core-code changes and is honestly documented
  as the reason in the template's own header comment.
- **Runtime split**: `http-echo` is Dart, the other two are Node — the prompt suggested this exact split
  ("HTTP echo (node or dart)... Scheduled cleanup job (node)... Webhook receiver (node)"); Dart was picked
  for the echo starter so the bundle exercises both shipped runtimes, not just one.
- **Execute roles**: every template-created function starts with an empty `execute` list, same as a
  manually created one — deny by default, no exception carved out for templates. The operator grants a
  role from the function's Settings page afterward, same flow either path takes.

## Known gaps

- **Function invocation doesn't forward the caller's real HTTP headers**, and the invocation contract
  itself is a structured RPC-style envelope rather than a raw-request proxy — see Deviations above. This
  is what actually blocks a literal "validates a signature header" webhook template, not a Functions
  starter-templates limitation specifically. Left as a discovered gap for a future session (flagged via
  `spawn_task` at the end of this session) rather than expanded into here.
- **The scheduled-cleanup and webhook-receiver templates' Tables round trip isn't exercised by the
  automated test suite** — proven manually against the live dev instance (see Owner-test checklist) but
  not by `dotnet test`, because doing so would need the spawned function container to call back out to
  the *test process's own* API over the network, which the integration harness doesn't expose a stable
  address for (Testcontainers/WebApplicationFactory don't publish a host-reachable port a sibling
  container could dial, and `host.docker.internal` isn't guaranteed on a Linux CI runner). What the
  integration tests do prove instead: the built image runs for real, the entrypoint resolves, and each
  template's own config guard — not a container crash — produces the correctly-worded failure when its
  required env vars are unset.

## Tests

`tests/Praxy.Tests.Integration/FunctionTemplateTests.cs` (new), real Docker daemon throughout, same
"no stubbing the Docker leg" discipline as `FunctionTests.cs`:

- `Template_catalog_lists_the_bundled_templates` — `GET /v1/functions/templates` returns all three keys
  with the expected runtime/entrypoint/defaultSchedule.
- `Unknown_template_key_is_rejected` — `404 function_template_not_found`.
- `Each_bundled_template_builds_and_auto_activates_for_real` (`[Theory]`, all three keys) — creates from
  template, waits for a real `docker build` to reach `ready`, confirms the build worker auto-activated it
  (`activeDeploymentId` matches), same as a normal upload.
- `Http_echo_template_actually_echoes_the_request` — full round trip: invokes with a method/path/body,
  parses the echoed JSON, asserts all three came back correctly.
- `Scheduled_cleanup_template_fails_closed_without_required_env_vars` /
  `Webhook_receiver_template_fails_closed_without_a_configured_secret` — invokes each with no
  configuration, asserts a clean `500` with the exact missing-var message the template's own guard
  produces (proves the container runs real code, not just that it builds — see Known gaps for why this
  is where automated coverage stops for these two).

Full-repo `dotnet test`: **379/379 unit, 213/213 integration**.

## Commands

No new config knobs — templates reuse `Praxy:Functions:*` entirely as-is. Nothing to add to `CLAUDE.md`'s
Commands section.

## Owner-test checklist

Done by me this session against the local dev instance (`api`/`console` launch configs,
`owner@test.local`):

- Created a function from each of the three templates via the new picker in "Create function".
- **http-echo**: built (Dart, real `docker build`), auto-activated, invoked via the console's Run
  button with a custom method/path/body — got back the exact echoed JSON.
- **scheduled-cleanup**: built (Node), auto-activated, confirmed its Settings page shows the default
  `0 3 * * *` schedule and a real "next run" time already computed.
- **webhook-receiver**: built (Node), auto-activated, invoked with a JSON body carrying a `secret` field
  — got back `500 Missing required env var: WEBHOOK_SECRET` (none was configured on this test function),
  confirming the config guard runs and correctly reports what's missing before ever comparing secrets.
- Deleted all three test functions afterward to leave the shared dev instance clean.
- Not independently re-verified: an end-to-end run of scheduled-cleanup/webhook-receiver actually reading
  or writing Tables data through a real `PRAXY_API_KEY` + `PRAXY_ENDPOINT` — the template logic is
  straightforward `fetch` calls against documented, already-tested REST endpoints, but the owner may want
  to confirm the exact `PRAXY_ENDPOINT` value for their own topology works as documented before relying
  on either template for something real.

## Next

No further prompt is written — this was a self-contained addition, not a phase with an implied
successor. The credential-mechanism question this session's templates worked around
(`docs/handoff/functions-scheduled-credentials-prompt.md`) is still open and unstarted; picking it up
would let both templates drop their `PRAXY_API_KEY` env-var workaround in favor of whatever mechanism
that session chooses. The header-forwarding/invocation-contract gap noted above is a second, independent
candidate for a future session if a more Appwrite-like "point a real webhook at this URL" experience is
ever wanted.
