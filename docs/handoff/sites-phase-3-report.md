# Sites Phase 3 — report

**Status: complete.** Every item in `docs/handoff/sites-phase-3-prompt.md`'s scope shipped. Full-repo
`dotnet test` green — **359/359 unit, 193/193 integration** (real Postgres via Testcontainers, real
Docker daemon throughout), including a regenerated `docs/openapi/v1.json` for the four new
`/domains` endpoints. Console `tsc -b && vite build` clean.

## What shipped

**`site_domains` table** (migration `20260824182226_SiteDomains`,
[Sites.cs](../../src/Praxy.Persistence/Entities/Sites.cs)): `id, site_id, project_id, hostname, status
(pending|verified), created_at, verified_at`, `hostname` globally unique (`ix_site_domains_hostname`,
not scoped to project — two different sites can't claim the same real-world domain), cascade-deletes
with its site.

**`SiteCustomDomainLookup`** ([SiteCustomDomainLookup.cs](../../src/Praxy.Sites/SiteCustomDomainLookup.cs),
new sibling type, not folded into `SiteHostPattern.TryParse`): an exact, case-normalized DB lookup
against `site_domains`, per the prompt's own reasoning — `TryParse` is a pure string-suffix parse with
no DB access by design, and a custom domain has no fixed suffix to parse against, so giving it a
separate type keeps "cheap parse" and "DB-backed exact match" from blurring into one signature.
`FindAsync` returns the raw row; `ResolveEnabledSiteAsync` additionally requires the owning site be
`Enabled` — the one shared implementation both `SiteProxyMiddleware` and `_ask-tls` call, so neither
grows its own drifting copy of the lookup.

**`SitesService`** ([SitesService.cs](../../src/Praxy.Sites/SitesService.cs)) gained
`ListDomainsAsync`/`AddDomainAsync`/`DeleteDomainAsync`. `AddDomainAsync` validates the hostname against
an RFC-1035-shaped regex (≥2 labels, 1–63 chars/label, no leading/trailing hyphen) before touching the
DB — the character class excludes `*`, so a wildcard custom domain is rejected by construction rather
than a separate check — and explicitly refuses `options.Domain` itself or any subdomain of it (closes an
edge case the prompt didn't call out by name: without this, a site owner could register
`evil.sites.<domain>` as a "custom domain," aliasing another site's own built-in namespace). A unique-
constraint violation on insert surfaces as a clean `409 site_domain_already_exists`, not a raw Postgres
error.

**`SiteEndpoints.cs`**: `GET/POST /sites/{siteId}/domains`, `DELETE /sites/{siteId}/domains/{domainId}`,
audit-logged (`sites.domains.create`/`sites.domains.delete`) like every other console admin mutation.
`AskTls` now falls through to `SiteCustomDomainLookup.ResolveEnabledSiteAsync` when a queried hostname
doesn't parse against the built-in wildcard pattern at all, requiring the same enabled-site +
ready-active-deployment strictness as the production path before returning `204` — a 404 here still
doesn't distinguish "no such site," "site disabled," or "no such custom domain," so it can't be used to
enumerate any of the three.

**`SiteProxyMiddleware.InvokeAsync`** ([SiteProxyMiddleware.cs](../../src/Praxy.Sites/SiteProxyMiddleware.cs)):
when `SiteHostPattern.TryParse` doesn't match at all, falls through to a new
`TryServeCustomDomainAsync` before giving up and calling `next(ctx)`. Resolves to the site's **active**
deployment only (no cold-start, no preview equivalent — same restraint the built-in production path
already applies). **The `pending → verified` flip happens here, not in `AskTls`** — the phase's own
central design constraint. `AskTls` returning `204` only means "you may attempt ACME issuance," which
runs *before* Caddy talks to Let's Encrypt; recording "verified" there would record an attempt, not
proof of control. A request that reaches this middleware's custom-domain path, by contrast, only ever
arrives after Caddy's on-demand TLS has already completed a real ACME HTTP-01 challenge against that
exact hostname (that's how the TLS handshake in front of the middleware succeeded at all) — so the first
request this middleware itself successfully forwards is as strong a proof of domain control as a
dedicated verification record would be, with no polling job needed. The `ExecuteUpdateAsync` predicate
re-checks `status = "pending"` at update time, so two concurrent first-requests both flipping is a
harmless no-op race, not a correctness issue.

**`deploy/Caddyfile`**: a fourth site block, a bare `https:// { tls { on_demand } } reverse_proxy
api:8080 }` catch-all — Caddy's own documented answer for "many dynamically-created customer domains"
(caddyserver.com/on-demand-tls's worked example, verbatim). **Verified against real Caddy**, per the
prompt's explicit landmine and this exact file's own history of two prior wildcard-depth bugs: loaded
all four blocks together with `debug` logging and a local ask stub, confirmed a genuine 2-label site
hostname and a genuine 3-label preview hostname each still triggered their *own* dedicated block (no
shadowing by the catch-all), while an unregistered custom domain and a made-up attacker hostname both
correctly fell through to the catch-all's ask call — and the catch-all never intercepted the
console/API's own explicit-hostname block either. Full transcript, including the runtime
`apps.tls.automation.policies` dump that confirms the compiled automation-policy shape (not just
`caddy adapt`'s static output), in `docs/research/dotnet-stack.md`'s new Caddy subsection.

**Console** (`SiteSettingsPage.tsx`): a "Custom domains" card — add-domain input, a list with a
`pending`/`verified` badge and relative timestamp per domain, remove action via the existing
`ConfirmButton` pattern. Shows the CNAME target (the site's own `*.sites.<domain>` hostname, derived
client-side from `site.publicUrl`) as the primary/recommended path, documents the apex A/AAAA-record
path as self-serve since this instance doesn't know its own public IP and Phase 3 deliberately didn't
add a config value for it (see Deviations).

## Deviations & notes

- **A shared-machine Docker/disk crisis hit mid-session, same class of incident Phase 2's report
  documented — not caused by this phase's own code.** Docker Desktop's daemon had crashed entirely
  (only its privileged helper processes were still alive; every `docker` command hung or refused to
  connect) by the time this session picked up the branch, and host disk was down to ~1.6GB free.
  Relaunched Docker Desktop with the owner's go-ahead, then — also with explicit owner approval, given
  each step — reclaimed disk via `docker image prune -f` (orphaned build layers, 8.6GB), removed two
  orphaned Testcontainers-leftover containers, cleared the build cache (10GB), and, once identified as
  unrelated to Praxy, removed a full ~30-container Appwrite reference stack (containers, network, 13
  data volumes, all images) plus ~130 stale dated `praxy-site-*`/`praxy-fn-*`/test-api images — freed
  ~63GB total (1.6GB → 63GB free). One targeted `docker rmi` pass had to be re-done after an initial
  grep pattern silently failed to match repository names containing a `/` (`appwrite/appwrite`,
  `openruntimes/*`), so those weren't actually removed on the first attempt despite the command
  reporting success on the entries it did match — caught by re-listing images afterward rather than
  trusting the first pass's exit code alone. A blanket `docker image prune -a -f` was attempted once to
  save time and was correctly blocked by the auto-mode permission classifier (it would have also swept
  up several clearly-unrelated other-project images — `backhub-*`, `nexacloud-*`, `mariadb`, `rabbitmq`,
  `minio` — that happened to be sitting in the same local image cache); the equivalent cleanup was
  redone as an explicit, scoped `docker rmi` against only the Praxy-prefixed tags instead. `praxy-dev-pg`
  (the shared local dev Postgres container, not started by this session) had no restart policy and
  stayed down after the Docker Desktop restart; noticed and `docker start praxy-dev-pg`'d back up — logs
  showed a clean checkpoint history throughout, confirming it was a casualty of the daemon restart, not
  a genuine crash. No volumes or images belonging to Praxy's own current work were touched by any of
  this.
- **No real Let's Encrypt round trip, and no real public DNS record, were available in this
  environment** — same limitation Phase 1 and Phase 2's own Caddy verification notes carried forward.
  The catch-all block's automation-policy/shadowing behavior (the piece that fails silently and was the
  actual root cause both prior times this Caddyfile broke in production) was verified live against a
  real Caddy instance with a local ask stub standing in for `_ask-tls`; the full `pending → verified`
  flip on a genuine ACME-issued cert was instead proven via `SiteCustomDomainTests.cs`'s integration
  test, which drives a real site through a real `_ask-tls` call and a real proxied request end to end
  (Docker + Testcontainers Postgres, no Caddy in that particular test's path — Caddy's own behavior is
  covered separately by the Caddyfile verification above). **Confirm a real custom domain actually gets
  a Let's Encrypt cert and flips to `verified` the first time this ships to a real, publicly-resolvable
  domain** — the one piece of the owner-test checklist this session could not fully close itself; see
  Owner-test checklist below for exactly what was and wasn't proven.
- **No config value added for this instance's own public IP.** The prompt left this as an implementation
  call (`decide whether to add a config value for it or just document the CNAME path as primary`). Went
  with documentation-only: the console's add-domain card recommends the CNAME path (needs no IP
  knowledge at all) and describes the A/AAAA path as self-serve, using the same IP the owner already
  configured for the wildcard `*.sites.<domain>` record. Adding a `Praxy:Sites:PublicIp`-style setting
  remains straightforward if a future session wants to auto-fill the A-record value shown in the console.
- **Reserved-suffix check on `AddDomainAsync`** (rejects `options.Domain` itself or any subdomain of it)
  wasn't explicitly called out in the prompt's scope but follows directly from its own security framing
  — without it, a site owner could self-register e.g. `evil.sites.<domain>` as a "custom domain" and
  alias into the platform's own reserved wildcard namespace. Cheap to add alongside the hostname-shape
  validation the prompt did ask for, so included rather than deferred.

## Known gaps (deliberate, per the prompt's own non-goals)

- No wildcard custom domains (`HostnamePattern`'s character class excludes `*` by construction), no
  git integration, no DNS-01/DNS-provider-credential verification, no change to the built-in
  subdomain/preview paths' behavior, no automatic domain removal/expiry if DNS stops resolving — all
  explicitly out of scope per the prompt.
- No console surfacing of a custom domain's target deployment or any per-domain traffic/analytics —
  the prompt asked for add/remove/status only.
- Apex-domain A/AAAA record is fully self-serve/manual (see Deviations) — no auto-fill, no validation
  that the owner actually pointed it at the right IP.

## Tests

- `SiteCustomDomainLookupTests.cs` (new, unit): `Normalize` case/whitespace handling.
- `SiteCustomDomainTests.cs` (new, integration, real Docker): add a domain to a real site, confirm
  `_ask-tls` allows it once `pending` and the site is enabled, confirm a proxied request through the
  custom domain reaches the site's active deployment and only flips the row to `verified` *after* that
  successful request (not before, not on the `_ask-tls` call alone); removing a domain stops it
  resolving to the site immediately.
- `SitesAskTlsTests.cs` (extended): an unregistered hostname that otherwise parses as a real domain is
  rejected; a domain is allowed while `pending` then rejected once its site is disabled; a domain
  belonging to *another* site doesn't leak through that other site's state; custom-domain matching is
  case-insensitive, consistent with the built-in subdomain paths.
- Full-repo `dotnet test`: **359/359 unit, 193/193 integration**, including `OpenApiDocumentTests` after
  regenerating `docs/openapi/v1.json` (stale only because of the four new `/domains` endpoints — see
  `docs/api-reference.md`'s regenerate command).

## Commands

No new `Praxy:Sites:*` configuration this phase — the catch-all Caddy block needs no new env var, and
custom-domain hostname validation/uniqueness lives entirely in code + the DB's own unique index.
Everything from `docs/handoff/sites-phase-2-report.md`'s Commands section (Docker endpoint/network,
domain, build/startup timeouts, resource limits, preview idle/sweep settings) is unchanged.

Deploying this phase's Caddyfile change to a live box: restart Caddy after deploying, don't assume the
bind-mount alone is enough —
```
docker compose -f deploy/docker-compose.yml restart caddy
docker exec praxy-caddy-1 caddy adapt --config /etc/caddy/Caddyfile
```
— per this prompt's own first Landmine and `docs/self-host.md`'s Upgrading section. Not applied to the
live `praxycore.dev` box this session — the prompt says only to do that if asked, and the owner didn't
ask.

## Owner-test checklist

Fully proven via the real integration test
(`SiteCustomDomainTests.A_custom_domain_reaches_the_active_deployment_and_only_flips_to_verified_after_that`,
real Docker, real Postgres): add a domain (starts `pending`), confirm `_ask-tls` allows it, confirm a
proxied request reaches the site's real active deployment, confirm the row is *still* `pending`
immediately before that request and `verified` immediately after — and
`Removing_a_custom_domain_stops_it_resolving_to_the_site` for the removal half. `_ask-tls` rejecting a
made-up/unregistered hostname is covered by `SitesAskTlsTests`'s new cases.

**Not independently re-verified through a real browser / real public DNS** — no internet-reachable test
domain was available in this environment, the same constraint Phase 1 and Phase 2 both noted. The
console's new "Custom domains" card was verified via `tsc -b && vite build` (clean) but not clicked
through live against a running dev instance in this session. **This is the one piece of the phase's own
"Done means" checklist genuinely left for the owner**: add a real custom domain to a real site pointing
at this box, confirm it shows `pending`, hit it and confirm it goes `verified` and serves the site's
real active deployment, confirm a made-up hostname gets rejected by `_ask-tls`, confirm removing the
domain stops it resolving — exactly the prompt's own owner-test wording.

## Next

`docs/handoff/sites-phase-4-prompt.md` was **not** written this session — nothing learned here
materially changes the Phase 4 sketch already in `docs/research/praxy-sites.md` (git integration, a
self-hosted owner-configured GitHub App). Per the same judgment call Phase 2's report made about
Phase 3, that's its own scoping session when the owner is ready.
