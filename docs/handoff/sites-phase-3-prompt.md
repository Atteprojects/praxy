# Session task — Sites Phase 3 (custom domains)

## Why this exists

Sites Phase 1 (subdomain hosting) and Phase 2 (preview URLs, graceful redeploy) are shipped. Every site
is only reachable at `<key>.<projectId>.{Praxy:Sites:Domain}` today. The owner wants to close that: let a
site owner point their own domain (`myapp.com`, or a subdomain of it) at a site instead.

Read `docs/research/praxy-sites.md`'s "Phase 3 — custom domains" section in full before writing any code
— it's the design spec this phase follows, including why Praxy is in a *better* position here than
Appwrite's own self-hosted docs admit to being (their custom-domain certs need either a manual
`ssl --domain=` command per site or a Traefik DNS-challenge setup; Praxy's Phase 1 on-demand-TLS choice
already solves this generically). Also read `docs/handoff/sites-phase-2-report.md`'s "Deviations & notes"
and this prompt's own Landmines section below — both describe real, previously-hit bugs in exactly the
subsystems this phase extends.

This is Sites Phase 3 of 4 the owner has committed to. Phase 4 (git integration) comes after; framework
presets beyond Next.js are deferred indefinitely. Work on a new branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **No git integration.** That's Phase 4. Deployment stays console tar upload / starter-template deploy
  only.
- **No wildcard custom domains** (`*.myapp.com` pointed at a site). One hostname per `site_domains` row,
  exact match only — a wildcard custom domain reopens the exact "how many labels does this actually
  match" class of bug this phase's own Landmines section is about, for no clearly-asked-for benefit.
- **No DNS-01 / DNS-provider-credential-based verification.** The whole point of the on-demand-TLS design
  is avoiding that class of setup. Don't reach for it even as a fallback.
- **No changes to the production/preview subdomain paths' existing behavior.** `<key>.<projectId>.{Domain}`
  and `<deploymentRef>.<key>.<projectId>.{Domain}` must keep working exactly as they do today — a custom
  domain is a new, additional way to reach a site's active deployment, not a replacement for the existing
  one.
- **No automatic domain removal/expiry.** If a custom domain stops resolving to the box, leave its
  `site_domains` row as-is (`verified` stays `verified`) rather than trying to detect and prune it —
  that's a monitoring concern, not something this phase should invent.

## Scope

1. **`site_domains` table** (new EF migration): `id, site_id, project_id, hostname, status
   (pending|verified), created_at, verified_at`. Unique on `hostname` (globally — two different sites,
   even in different projects, can't claim the same custom domain). Console CRUD: add/remove a domain per
   site, shown on the site's Settings page alongside its existing `*.sites.<domain>` URL. Show `status`
   clearly — a `pending` domain hasn't served real traffic through the ask-endpoint flow yet.
2. **Extend `SiteHostPattern`** (`src/Praxy.Sites/SiteHostPattern.cs`) — or add a clearly-named sibling,
   your call once you're looking at the real shape — with a **custom-domain lookup path**. This is
   structurally different from the existing `TryParse`: that method is a pure string-suffix parse against
   `options.Domain` with no DB access, by design (both its callers need a cheap first-pass check before
   ever touching the database). A custom domain has no fixed suffix or label count to parse against — it's
   an **exact match against the `site_domains` table**, which means it's inherently a DB lookup, not a
   parse. Keep those two concerns (cheap structural parse vs. DB-backed exact lookup) clearly separated
   rather than trying to force custom domains through the existing `TryParse` signature.
3. **Extend `SiteProxyMiddleware.InvokeAsync`** (`src/Praxy.Sites/SiteProxyMiddleware.cs`): when
   `SiteHostPattern.TryParse` doesn't match (host isn't shaped like `*.{Domain}` at all), fall through to
   the new custom-domain lookup before giving up and calling `next(ctx)`. A verified custom domain resolves
   to a site's **active deployment only** — same production-path semantics `SiteContainerRegistry` already
   has via `site.ActiveDeploymentId`, no preview-URL equivalent for custom domains in this phase.
4. **Extend `AskTls`** (`src/Praxy.Api/Endpoints/SiteEndpoints.cs`): same fallback — if the queried domain
   doesn't parse against `options.Domain`, check `site_domains` for an exact, enabled-site match before
   404ing. This is the security-critical half of this phase (see Landmines) — a permissive version here
   turns the box into a cert-issuance oracle for arbitrary attacker-supplied hostnames, not just within the
   existing wildcard suffix.
5. **Verification = first successful cert issuance, not a separate polling job.** When `AskTls` allows a
   `pending` custom domain through and Caddy's subsequent ACME issuance actually succeeds, flip that
   `site_domains` row to `verified` (this needs a way for the app to *know* issuance succeeded — Caddy's
   on-demand TLS calls `_ask-tls` **before** issuing, not after, so "flip to verified" most plausibly
   happens on the **first successful proxied request** through the new custom-domain path in
   `SiteProxyMiddleware`, not inside `AskTls` itself, since `AskTls` returning 200 only means "allowed to
   try," not "the cert was actually issued." Get this ordering right — it's the one place this phase's
   design doc's "no polling job" claim actually has to be earned by real code, not assumed.
6. **`deploy/Caddyfile`**: add a second, non-wildcard on-demand-TLS site block for custom domains. Caddy
   needs *some* address pattern that matches "any hostname not already claimed by the two existing sites
   blocks" — research the actual current Caddy syntax for this (a bare `:443` catch-all site block, or
   listing it last with lower specificity — **verify against real Caddy docs, don't guess from memory or
   from how the existing two blocks look**, per this exact file's own scar tissue, see Landmines). Order it
   so it never shadows the console/API's own domain block or the two sites-wildcard blocks.
7. **Console**: on `SiteSettingsPage.tsx`, a "Custom domains" card — add-domain form (shows the required
   DNS record: A/AAAA to the box's IP for an apex domain, or CNAME to the site's own `*.sites.<domain>`
   hostname for a subdomain — the app doesn't know the box's own public IP today, so decide whether to add
   a config value for it or just document the CNAME path as primary and leave apex/A-record as
   self-serve/manual), a list of configured domains with their `status` badge, remove action.

## Landmines — read before writing code

- **Caddy does not hot-reload `deploy/Caddyfile` — a bind mount makes new bytes visible inside the
  container, but the running Caddy process only parses the file once, at its own startup.** Found live,
  2026-08-24: Phase 2's own 3-label wildcard block landed correctly on disk and was bind-mounted correctly,
  but sat completely inert for days across three separate deploys, because the standard deploy procedure
  (correctly) only recreates the `api` container, never Caddy. Every affected preview URL failed TLS
  (`tlsv1 alert internal error`, ask endpoint never even called) — indistinguishable from the outside from
  a genuinely wrong Caddyfile. `caddy reload --config ... --adapter caddyfile` did **not** reliably fix
  this either in this environment; only a full `docker compose restart caddy` did. This phase adds a new
  Caddyfile block — **after deploying it, always run `docker compose -f deploy/docker-compose.yml restart
  caddy` and verify with `docker exec praxy-caddy-1 caddy adapt --config /etc/caddy/Caddyfile` that the new
  automation policy actually appears**, not just that the file on disk looks right. Full writeup:
  `docs/self-host.md`'s Upgrading section.
- **Caddy's on-demand-TLS automation-policy subject matching is exactly as strict about wildcard depth as
  a real TLS wildcard certificate.** Both existing sites blocks needed this lesson learned the hard way
  (Phase 1: one wildcard label short; Phase 2: reproduced the identical bug one label short again). A
  catch-all/custom-domain block is a different shape of the same risk — prove whatever pattern you land on
  against a real Caddy instance and a real non-wildcard hostname actually getting a cert, not a clean
  `caddy validate` run (which only checks syntax, not automation-policy subject matching).
- **`_ask-tls` is the entire security boundary for custom domains, more so than for the existing
  subdomain-wildcard paths.** Today it only has to defend one fixed suffix. Once it also accepts arbitrary
  hostnames looked up against `site_domains`, it becomes the only thing standing between the box and
  answering an on-demand-TLS "ask" for **any** hostname an attacker points DNS at — which can also burn
  through Let's Encrypt's real rate limits. The lookup must be an exact, case-normalized match against a
  `verified`-or-legitimately-`pending` row for an *enabled* site, nothing looser.
- **`SiteHostPattern`'s existing doc comment exists specifically so nobody adds a second, slightly
  different hostname parser somewhere else.** Whatever you build for custom domains, make sure
  `SiteProxyMiddleware` and `AskTls` both go through it (or its natural sibling), not two independently
  drifting implementations.
- **The "verified" flip must not happen inside `AskTls`.** `AskTls` returning `204` only tells Caddy "you
  may attempt ACME issuance for this hostname" — it runs *before* Caddy actually gets a certificate from
  Let's Encrypt, not after. Flipping `site_domains.status` to `verified` at that point would be recording
  "we allowed an attempt," not "this domain actually proved control." Do the flip on the first successful
  proxied request through `SiteProxyMiddleware`'s custom-domain path instead, where a completed TLS
  handshake already proves the cert was actually issued.

## Tests

`tests/Praxy.Tests.Integration/` — extend `SiteTests.cs` or add `SiteCustomDomainTests.cs`: add a domain
to a real site, confirm `_ask-tls` allows it once `pending` and the site is enabled, confirm a proxied
request through the custom domain reaches the site's active deployment, confirm the domain flips to
`verified` only after that successful request (not before). Extend `SitesAskTlsTests.cs`: a domain
belonging to a disabled site is rejected; an unregistered hostname is rejected; a domain belonging to
*another* site doesn't leak through; case-insensitivity is handled consistently with the existing
subdomain paths. Unit tests for whatever new hostname-lookup helper you add, mirroring
`SiteHostPatternTests.cs`'s style.

## Done means

- `dotnet test` green (unit + integration, real Docker daemon).
- Console build clean (`tsc -b && vite build`).
- **Owner test, actually run**: add a custom domain to a real site pointing at a real reachable host (or
  the closest faithful equivalent your test environment allows — document exactly what you actually proved
  if a real public DNS record isn't available to you), confirm it shows `pending`, hit it and confirm it
  goes `verified` and serves the site's real active deployment, confirm a made-up/unregistered custom
  hostname gets rejected by `_ask-tls` (no cert, no proxy), confirm removing the domain in the console makes
  it stop resolving to the site.
- `git status` clean, conventional commits, on a new branch off `main`.
- `docs/research/dotnet-stack.md` updated with whatever real Caddy catch-all/custom-domain directive syntax
  you verify, the same discipline the existing Caddy section already follows.
- `docs/self-host.md` gets a "Custom domains" section (DNS records to configure, what "pending" vs
  "verified" means, that apex domains need an A/AAAA record the owner points at the box's own IP).
- Write `docs/handoff/sites-phase-3-report.md`. Only write `sites-phase-4-prompt.md` if something learned
  this session materially changes the existing Phase 4 sketch in `docs/research/praxy-sites.md` — otherwise
  leave it for its own scoping session, per the same judgment call Phase 2's report made about Phase 3.

## Deploying (only if the owner asks)

This touches `deploy/Caddyfile` again — the exact file already responsible for one real, multi-day
production bug this year. Do not apply changes to the live `praxycore.dev` box without being asked, and
when you do, follow the restart-and-verify discipline in this prompt's first Landmine exactly — don't
assume "the deploy script ran" means "Caddy is actually serving the new config."
