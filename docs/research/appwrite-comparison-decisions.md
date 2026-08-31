# Appwrite comparison — decisions log

Working notes from the 2026-08-30/31 informal self-host comparison (a local Appwrite 1.9.6 instance,
walked through Sites/Functions/templates/git integration side by side with Praxy's own) and the four
follow-up sessions it produced. Not itself a permanent doc — a staging record of *what got decided and
why*, kept in one place so the load-bearing choices can be folded into `docs/architecture.md`,
`docs/self-host.md`, or wherever else makes sense once someone does that pass. Each item below points at
its full prompt/report pair in `docs/handoff/` for complete detail; this doc only keeps the reasoning
worth remembering independent of the implementation specifics.

## Status at time of writing

| Finding | Prompt | Report | Landed |
|---|---|---|---|
| Functions starter templates | `functions-templates-prompt.md` | `functions-templates-report.md` | **PR #31 open, not yet merged** |
| Sites build caching | `sites-build-caching-prompt.md` | `sites-build-caching-report.md` | Merged (`main`) |
| Scheduled/event function credentials | `functions-scheduled-credentials-prompt.md` | `functions-scheduled-credentials-report.md` | Merged (`main`) |
| Sites per-request logs | `sites-request-logs-prompt.md` | — | Not started |

## 1. Functions starter templates

- **Scope deliberately narrow**: three curated, Praxy-shaped starters (`http-echo`/Dart,
  `scheduled-cleanup`/Node, `webhook-receiver`/Node) instead of chasing Appwrite's 40-template gallery.
  Matches Appwrite's *idea* (click-to-deploy something real), not its scale.
- **Credential mechanism inside the templates: a user-filled `PRAXY_API_KEY` env var, not a new injected
  one.** This was a conscious workaround, not the final answer — the scheduled-credentials prompt (item 3
  below) hadn't landed yet when this session ran, and its own kickoff prompt explicitly sanctioned this
  fallback ("have the template read it from a required, user-filled env var — don't invent a third
  mechanism"). Once item 3 is available, both templates could drop this env var in favor of the
  auto-injected `PRAXY_FUNCTION_API_KEY` — noted as a live follow-up, not done automatically.
- **No `PRAXY_ENDPOINT` auto-injection.** Investigated and rejected: the right value depends on Docker
  network topology that genuinely varies by deployment (`http://api:8080` inside the self-host compose
  stack's `praxy-functions` network vs `http://host.docker.internal:5090` for `dotnet run` on Docker
  Desktop vs something else again on Linux, where `host.docker.internal` doesn't resolve without an
  explicit `--add-host`). A wrong auto-injected value fails silently and confusingly; requiring the
  operator to set it explicitly (documented in each template's header comment) fails loudly instead.
  **This is the one open, unresolved design question across the whole initiative** — see Cross-cutting
  below.
- **`webhook-receiver` checks a `secret` field in the JSON body, not a header**, even though the prompt
  asked for a header. Traced to a real, pre-existing platform gap rather than a template preference:
  function invocation never forwards the caller's real HTTP headers (`DockerExecutor.InvokeAsync` always
  passes an empty headers dictionary), and the invocation endpoint itself is a structured
  `{method, path, body}` RPC envelope, not a raw-proxied request — so a real external webhook sender
  (GitHub, Stripe) couldn't point at it directly regardless of header support. Fixing that is core
  invocation-contract plumbing, out of scope for a templates task; flagged as its own follow-up rather
  than silently expanded into.
- **Runtime split (Dart for the echo starter, Node for the other two) was deliberate** — exercises both
  of Praxy's two shipped runtimes rather than defaulting everything to one.
- **Templates start with an empty `execute` list, same as a manually created function** — no
  quick-start exception carved out of deny-by-default.

## 2. Sites build caching

- **Root cause was a Dockerfile instruction-ordering bug, not a Docker/BuildKit limitation.**
  `SiteRuntimeTemplates.Dockerfile(...)` did `COPY . .` before `RUN npm install`, so every deployment's
  inevitable app-code change invalidated the install layer even when dependencies hadn't changed. Nothing
  about the daemon or `Docker.DotNet` was disabling caching — the cache was simply never given a chance
  to hit.
- **Fixed only the required scope (reorder the Dockerfile); deliberately did not attempt the deeper
  `.next/cache` (Next.js's own webpack/SWC cache) persistence** that Appwrite's build logs actually
  showed. Both candidate mechanisms were researched and both were rejected for this pass:
  - BuildKit `RUN --mount=type=cache` — the classic (non-BuildKit) builder is what `Docker.DotNet`'s
    `BuildImageFromDockerfileAsync` actually calls; `RUN --mount` is a BuildKit-frontend-only syntax with
    no path through the API this codebase uses. Reaching it means migrating Sites' whole build path off
    the classic builder first — ruled a separate, larger task by the kickoff prompt's own non-goals.
  - A filesystem-level cache carried between builds — possible without BuildKit, but needs a second
    `Target: "builder"` build call to obtain a taggable reference, durable per-site host storage with its
    own lifecycle, and correctness care against staleness/cross-project leaks. Judged a genuinely separate
    feature with real risk (a stale cache silently serving wrong output), not a small addition.
  - Guiding principle, quoted directly from the kickoff prompt and honored: **"a half-working cache is
    worse than no cache."**
- **Verified twice, not once**: the automated integration test (synthetic app, isolated `RUN npm install`
  log segment) and a live console click-through (real bundled `nextjs-starter`, two consecutive
  deployments) both confirmed the fix — 87s → 44s in the report's own run, 1m12s → 1s in the live console
  verification (a full-cache-hit redeploy, since that run redeployed byte-identical content end to end).

## 3. Scoped platform credentials for schedule/event-triggered functions

- **The actual gap was narrower than the original framing suggested.** Praxy already had a scoped
  API-key permission system (`ApiKeyScopes`/`ApiKeyService`) essentially equivalent to Appwrite's own
  per-template scope picker (`databases.read/write`, `functions.read/write`, etc.) — the real gap was
  just that a schedule- or event-triggered execution had no calling user to mint `PRAXY_FUNCTION_JWT`
  for, so it got **no credential of any kind**, not that Praxy lacked a permission model.
- **Mechanism chosen: (a) a persisted, function-owned, revocable `ApiKey` row — over (b) a short-lived
  service-scoped JWT.** The reasoning came from tracing actual authorization call sites, not from reading
  `ApiKeyService`/`AccountJwtService` in isolation: every enforcement point in `Praxy.Api`
  (`RowEndpoints.RequireScopeIfKey`, `RoleResolver`'s switch, `FunctionEndpoints.CallerIdentity`) is
  written in terms of the concrete `RequestPrincipal.Key` case. A JWT-based option would have needed a
  new `RequestPrincipal` shape (a JWT with scopes but no `userId`) threaded through every one of those
  call sites — precisely the "parallel authorization check that could drift from the real one" the
  kickoff prompt's own landmine warned against, just spread across several files instead of one. Backing
  the credential with a literal `ApiKey` row means every existing and future enforcement point just
  works, at the cost of a recoverable (not hash-only) secret at rest — accepted because
  `FunctionEnvVar.ProtectedValue` already establishes that exact pattern for the same reason.
- **No DB-level foreign key from `functions.platform_api_key_id` to `api_keys.id`.** Deliberately mirrors
  the existing `ActiveDeploymentId` → `FunctionDeployment` precedent in the same file: an app-managed
  `Guid?` plus code-level reconciliation, not a cascade. A `SetNull`-on-delete FK was considered and
  rejected — it would only null the FK column, not the sibling `PlatformScopes`/
  `PlatformApiKeySecretProtected` columns next to it, so the self-healing application logic
  (`ApplyPlatformScopesAsync` re-mints a key if the referenced one is gone) was needed regardless. Adding
  the FK on top would have been two half-solutions instead of one real one.
- **The function-owned key is intentionally visible on the project's own API keys page**, named
  `function:<key>`, revocable like any other key. Hiding it (a "system key" flag, or a filtered query)
  was considered and rejected as unnecessary surface for a cosmetic concern — this codebase's existing
  posture is "the operator can see and manage everything in their project," and the self-heal logic
  already makes it safe for an operator to revoke it directly.
- **Platform scopes are Settings-tab-only — not settable at function creation.** Every function starts at
  `PlatformScopes: []`, the same deny-by-default posture `Execute` already has at creation.
- **`bypassRowPermissions` deliberately stays unreachable for a platform key.** The key is always created
  with `bypassRowPermissions: false` — a scheduled function's platform key still needs the target table
  to actually grant its resolved role a permission, not a second escalation path.

## 4. Sites per-request logs (not started — intent worth preserving)

The prompt itself (`docs/handoff/sites-request-logs-prompt.md`) already encodes the decisions that
mattered most to get right before implementation, in case a future session's context doesn't include the
reasoning:

- **Must be wired into the existing `RetentionSweeper` from day one**, not deferred the way
  `function_executions` was deliberately and knowingly left unbounded (`ci-and-retention-prompt.md`).
  Sites request volume — every HTTP request to every deployed site, unconditionally — will likely dwarf
  function-invocation volume, so repeating that deferral here was judged unsafe by default.
  Bump this to top priority — do not build this without also submitting a retention policy — if that
  prompt is picked up.
- **Writes must not block the request path.** A bounded in-memory channel + background drain, not a
  synchronous insert per request — Sites traffic is real end-user web traffic, unlike an explicitly
  invoked function call, so logging pressure must never be able to slow down or fail a site's own
  response.
- **Metadata only, no request/response body capture** — a deliberately smaller privacy/volume footprint
  than `FunctionExecution.RequestBody`/`ResponseBody` already accepts for Functions.
- Whether to log every request or sample under load is explicitly left as an open call for whoever
  implements it, with instructions to record the choice rather than pick one silently.

## Cross-cutting theme

In every case, the response to something Appwrite had was "reuse what Praxy already has," not "port what
Appwrite has": `ApiKeyScopes` over inventing a new scope system, the existing `SiteStarterTemplate`
pattern over new template infrastructure, Docker's already-enabled layer cache over jumping straight to
a BuildKit migration. Two of the three completed items (build caching, scheduled credentials) explicitly
chose to stop short of full parity with what Appwrite showed rather than force a mechanism that didn't
fit Praxy's actual constraints (the classic builder API in use, a single-scoped-key auth model already
threaded through every enforcement point) — each one recorded as a deliberate scope boundary in its own
report, not a shortcut taken silently.

The one genuinely unresolved thread across all four: **functions have no way to discover their own API's
base URL** (`PRAXY_ENDPOINT` or equivalent). It surfaced independently in both the templates report and
the scheduled-credentials report as the reason neither could fully close the loop ("a credential now
exists, but nothing tells the function where to send it"). Worth its own kickoff prompt before either
templates or scheduled credentials can be considered fully load-bearing rather than "the plumbing is
there, wire it up yourself."

## Where this could land in permanent documentation later

- **`docs/architecture.md` §14 "Resolved questions"** — the credential-mechanism choice (`ApiKey` row
  over a service JWT, and why) and the "no DB FK, self-heal in code instead" pattern are the kind of
  load-bearing precedent worth a permanent one-liner there, alongside ID format and product naming.
- **`docs/self-host.md`** — once `PRAXY_ENDPOINT` (or whatever it ends up being called) is actually
  decided and implemented, it belongs in the config reference table next to the other `Praxy:*` knobs.
- **`docs/api-reference.md` / README** — once PR #31 merges, the template catalog endpoint
  (`GET /v1/functions/templates`) deserves a mention alongside Sites' own starter-template deploy path.

## Open items

- **PR #31 (`feat/functions-templates`) is still open, unmerged as of this doc.** The decisions recorded
  in §1 come from that branch, not from `main` — verify and merge it before treating templates as shipped.
- `sites-request-logs-prompt.md` is unstarted.
- The `PRAXY_ENDPOINT` gap (Cross-cutting, above) has no kickoff prompt written yet.
