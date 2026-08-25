# Self-hosting Praxy

This is the operator's guide: running the stack, configuring it, and — the part that actually
matters when something goes wrong — backing it up and getting it back. It assumes nothing about the
codebase; if you're reading Praxy's source to understand how to run Praxy, that's a bug in this
document.

## Requirements

- A host running Ubuntu (or any Debian-family distro) if you want `./up.sh` to install Docker for
  you; otherwise install [Docker + the Compose plugin](https://docs.docker.com/engine/install/)
  yourself first, on whatever OS.
- A host that can reach the internet if you want Google OAuth or outbound SMTP to work, though
  neither is required to run the instance.
- If you plan to use Functions: a reachable Docker daemon at runtime, not just at build time (see
  [Functions and the Docker socket](#functions-and-the-docker-socket) below).
- If you plan to use Sites: the same reachable Docker daemon, plus — for a public deployment — a
  **wildcard DNS record** (`*.sites.your.domain.com` → this host's IP) in addition to the plain
  `your.domain.com` record `up.sh` already asks for. See
  [Sites and the wildcard subdomain](#sites-and-the-wildcard-subdomain) below.
- If you plan to connect a Site to a GitHub repository (push-to-deploy): your own GitHub App (there's
  no shared Praxy-provided one) and a public, internet-reachable instance — GitHub delivers webhooks
  to a real URL, so `localhost` needs a tunnel. See [Git integration](#git-integration) below.

## Quick start

```bash
git clone https://github.com/<your-fork-or-org>/praxy.git
cd praxy/deploy && ./up.sh
```

First run only, `up.sh` asks one question and then handles everything else:

```
Public domain for this instance, e.g. praxy.example.com (leave blank to run over plain HTTP / localhost):
```

- **Leave it blank** for local use or an internal network — plain HTTP on `PRAXY_PORT` (8080),
  identical to every earlier version of this script.
- **Give it a domain** (with DNS already pointed at this host's IP) for a real public deployment —
  `up.sh` installs Docker if it isn't already there, generates secrets, brings up a
  [Caddy](https://caddyserver.com) reverse proxy that gets you a Let's Encrypt certificate
  automatically, binds the plain-HTTP `api` port to `127.0.0.1` only (never reachable from the
  internet directly — Caddy reaches it over the internal Docker network regardless), and does a
  best-effort `ufw` firewall lockdown (22/80/443 only). One command, no manual `.env` editing. This
  also derives Sites' public wildcard subdomain (`sites.your.domain.com`) automatically — add its own
  wildcard DNS record (`*.sites.your.domain.com` → this host) if you plan to deploy any sites; see
  [Sites and the wildcard subdomain](#sites-and-the-wildcard-subdomain).

Either way, once it's up, open the console (`http://localhost:8080`, or `https://your.domain.com`)
and claim the instance — the first account created becomes the owner, and sign-up closes immediately
afterward (enforced by the API, not just hidden in the UI). In the domain case, claiming requires a
setup token printed to the `api` container's logs (`docker compose logs api`) — otherwise anyone who
reaches the instance before you do could claim it first.

The console is served at the root path for whatever hostname reaches the `api` container — there's
no separate console deployment or CORS to think about. If you'd rather reach it at a dedicated
subdomain (e.g. `console.your.domain.com`) instead of, or alongside, the main domain, see
[Serving the console on its own subdomain](#serving-the-console-on-its-own-subdomain) below.

Re-running `./up.sh` later (to restart the stack) reuses the `.env` from first run and remembers
which mode you picked — it won't ask again.

## Public deployment with HTTPS — how it works, and the manual path

The interactive flow above is the intended way to get HTTPS. What it's actually doing, if you'd
rather set it up by hand (non-interactive provisioning, an OS `up.sh` can't auto-install Docker on,
etc.) — in `deploy/.env`:
```
PRAXY_DOMAIN=your.domain.com
PRAXY_PUBLIC_URL=https://your.domain.com
PRAXY_TRUST_FORWARDED_HEADERS=true
PRAXY_BIND=127.0.0.1
PRAXY_SITES_DOMAIN=sites.your.domain.com
```
then `docker compose --profile https up -d` instead of `./up.sh`. `PRAXY_SITES_DOMAIN` isn't strictly
required (it defaults to `sites.localhost`, which is simply never reachable from the public internet),
but set it — and its own wildcard DNS record — if you plan to deploy sites; see
[Sites and the wildcard subdomain](#sites-and-the-wildcard-subdomain).

`PRAXY_TRUST_FORWARDED_HEADERS` matters: without it, the `api` container (which only ever sees plain
HTTP from Caddy) marks the session cookie insecure and logs every request as coming from Caddy's own
IP rather than the real client's. `PRAXY_BIND=127.0.0.1` is what actually keeps the plain-HTTP `api`
port off the public internet — more reliable than a host firewall alone, since Docker's own iptables
rules are known to bypass tools like `ufw` for ports a container explicitly publishes. `up.sh` still
runs `ufw` too, as a second layer covering everything else on the host (SSH brute-forcing, stray
ports), but the port lockdown itself doesn't depend on it.

Verified end to end as part of Phase 9 hardening and again live on a real droplet: HTTP redirects to
HTTPS, the reverse proxy forwards correctly to `api`, the session cookie's `Set-Cookie` header
carries `secure`, and the plain-HTTP port is confirmed unreachable from outside the host (not just
firewalled — genuinely unbound on any externally-reachable interface).

### Serving the console on its own subdomain

`up.sh` only ever asks for one domain, and that's still the right default. If you'd rather reach the
console at `console.your.domain.com` instead of (or alongside) the main domain, set one more variable
by hand in `deploy/.env` after the `https` profile is already configured:

```
PRAXY_CONSOLE_DOMAIN=console.your.domain.com
```

then point that hostname's DNS `A`/`AAAA` record at the same host and `docker compose --profile https
up -d` again. `deploy/Caddyfile` lists it as a second site address alongside `PRAXY_DOMAIN` on the
same block, so Caddy provisions it its own Let's Encrypt certificate automatically — no other config
needed, since the console is served at the root path for whatever hostname reaches `api` regardless.
Leave it blank (the default) to skip this entirely; `PRAXY_DOMAIN` alone continues to work exactly as
before.

## Configuration

Everything is either an environment variable on the `api` container or a `Praxy:*` configuration key
(both work — `Praxy:RateLimits:Auth:PermitLimit` and `Praxy__RateLimits__Auth__PermitLimit` as an env
var are the same setting, standard ASP.NET Core config binding). The compose file only wires through
`PRAXY_SECRET_KEY` and `PRAXY_PUBLIC_URL` by default; add others to `deploy/docker-compose.yml`'s
`environment:` block for the `api` service as needed.

| Key | Default | Purpose |
|---|---|---|
| `PRAXY_SECRET_KEY` | *(generated by `up.sh`)* | Signs OAuth JWTs, encrypts provider tokens/credentials at rest. |
| `PRAXY_PUBLIC_URL` | unset | Requires the setup token to claim (see above). |
| `PRAXY_DOMAIN` | unset | The `https` profile's Caddy gets a certificate for this domain (see [Public deployment with HTTPS](#public-deployment-with-https)). |
| `PRAXY_CONSOLE_DOMAIN` | unset | Optional extra hostname (e.g. a console subdomain) the `https` profile's Caddy also gets a certificate for and proxies to the same `api` container — see [Serving the console on its own subdomain](#serving-the-console-on-its-own-subdomain). |
| `PRAXY_SITES_DOMAIN` | `sites.localhost` | The wildcard suffix a site's public hostname must end in (`<site key>.<project id>.<this value>`). `up.sh` sets it to `sites.$PRAXY_DOMAIN` automatically; the default only matters for local/plain-HTTP use — see [Sites and the wildcard subdomain](#sites-and-the-wildcard-subdomain). |
| `Praxy:TrustForwardedHeaders` (`PRAXY_TRUST_FORWARDED_HEADERS`) | `false` | Trust `X-Forwarded-For`/`-Proto` from the `https` profile's Caddy. Only enable alongside that profile — never if `api`'s port is also directly internet-reachable. |
| `PRAXY_BIND` | `0.0.0.0` | Host interface `PRAXY_PORT` binds to. `up.sh` sets this to `127.0.0.1` whenever a domain is configured — the actual mechanism that keeps the plain-HTTP port off the public internet. |
| `Praxy:Auth:SessionCacheSeconds` | 60 | In-memory session cache TTL. |
| `Praxy:Database:StatementTimeoutSeconds` | 30 | `statement_timeout` applied to every connection in the shared pool (Postgres-side, via the connection string's `Options`). DDL/schema-job connections `SET` their own longer value per session and are unaffected. |
| `Praxy:Smtp:Host`/`Port`/`Username`/`Password`/`From`/`UseTls` | unset (logs instead) | Instance-wide fallback email transport — used for auth emails and Messaging sends on any project that hasn't configured its own provider. |
| `Praxy:Smtp:AllowPrivateNetworkTargets` | `false` | Same shape as Webhooks' own flag below — an SMTP `Host` (instance-wide **or** a per-project Messaging provider) is otherwise blocked from resolving to a private/loopback/link-local address (SSRF guard). Set `true` if you run your own internal mail relay. |
| `Praxy:Webhooks:AllowPrivateNetworkTargets` | `false` | Same SSRF guard for webhook delivery targets. |
| `Praxy:RateLimits:Auth:PermitLimit` / `:WindowSeconds` | 10 / 60 | Login, signup, OAuth-start, token-exchange, membership-accept. |
| `Praxy:RateLimits:AuthEmail:PermitLimit` / `:WindowSeconds` | 5 / 600 | Verification and recovery email sends. |
| `Praxy:RateLimits:DataPlane:PermitLimit` / `:WindowSeconds` | 600 / 60 | Row CRUD (`/v1/databases/.../rows`). A ceiling against runaway clients, not a per-app throttle — raise it if a legitimate client needs more. |
| `Praxy:RateLimits:Functions:PermitLimit` / `:WindowSeconds` | 60 / 60 | Function invocation (`POST /v1/functions/{id}/executions`). Deliberately tighter than the rest of the data plane: each permitted request can start a container. |
| `Praxy:RateLimits:Realtime:PermitLimit` / `:WindowSeconds` | 60 / 60 | Realtime ticket minting. Complements `Praxy:Realtime:MaxConnectionsPerProject`, which bounds live sockets rather than the rate they're requested at. |
| `Praxy:Quotas:MaxProjects` | 100 | Projects per organization (org-overridable, see below). |
| `Praxy:Quotas:MaxDatabasesPerProject` | 20 | Databases per project (org-overridable). |
| `Praxy:Quotas:MaxTablesPerDatabase` | 200 | Tables per database (org-overridable). |
| `Praxy:Quotas:MaxColumnsPerTable` | 200 | Columns per table (org-overridable). |
| `Praxy:Quotas:MaxIndexesPerTable` | 64 | Indexes per table (org-overridable). |
| `Praxy:Quotas:MaxSitesPerProject` | 20 | Sites per project (org-overridable). |
| `Praxy:Quotas:MaxPreviewContainersPerProject` | 10 | Concurrent on-demand preview containers per project (org-overridable) — see [Preview URLs and idle sweep](#preview-urls-and-idle-sweep). |
| `Praxy:Tables:SchemaJobs:PollIntervalSeconds` | 2 | `CREATE INDEX CONCURRENTLY` / type-change job runner cadence. |
| `Praxy:Tables:SchemaJobs:IndexBuildTimeoutSeconds` | 1800 | Job wall-clock timeout before it's marked failed. |
| `Praxy:Realtime:MaxConnectionsPerProject` | 1000 | WebSocket connection quota. |
| `Praxy:Webhooks:*` | see `docs/handoff/phase-6-report.md` | Dispatch/delivery cadence, timeout, retry backoff, auto-disable threshold, SSRF allowlist. |
| `Praxy:Functions:*` | see `docs/handoff/phase-7-report.md` | Docker endpoint, base images, build/execution timeouts, warm pool size, resource limits. |
| `Praxy:Functions:DockerNetwork` | `""` | Docker network function containers join instead of publishing a host port; required when `api` itself runs in a container (this repo's own compose file sets it to `praxy-functions`) — see [Functions and the Docker socket](#functions-and-the-docker-socket). |
| `Praxy:Sites:*` | see `docs/handoff/sites-phase-1-report.md` | Docker endpoint, base image, build/startup timeouts, reconciliation cadence, resource limits. |
| `Praxy:Sites:DockerNetwork` | `""` | Same shape as `Praxy:Functions:DockerNetwork`, but a separate network (this repo's own compose file sets it to `praxy-sites`) — a site's container and a function's container can't reach each other by default. See [Sites and the wildcard subdomain](#sites-and-the-wildcard-subdomain). |
| `Praxy:Sites:PreviewIdleSeconds` / `PreviewSweepIntervalSeconds` | `600` / `60` | How long an on-demand preview deployment's container may sit with no proxied request before it's stopped, and how often that sweep runs — see [Preview URLs and idle sweep](#preview-urls-and-idle-sweep). Never applies to a site's active/production container. |
| `Praxy:Vcs:GitHub:AppId` / `ClientId` / `ClientSecret` / `PrivateKey` / `WebhookSecret` | unset | This instance's own GitHub App — see [Git integration](#git-integration). Unset means the feature is off (a clean, typed error, not a crash) until you create and configure one. |
| `Praxy:Vcs:CloneTimeoutSeconds` | 60 | Hard ceiling on a single `git` subprocess call while cloning a pushed commit for a build. |
| `Praxy:Messaging:*` | see `docs/handoff/phase-8-report.md` | Send-loop cadence, subject/body/target caps. |
| `Praxy:Retention:SweepIntervalSeconds` | 3600 | How often the retention sweep runs. |
| `Praxy:Retention:EventsMaxAgeDays` | 90 | Age past which a `praxy.events` row is deleted — only once **both** `WebhooksDispatchedAt` and `FunctionsDispatchedAt` are set; an unclaimed row past this age is left for the next sweep rather than force-deleted. |
| `Praxy:Retention:WebhookDeliveriesMaxAgeDays` | 90 | Age past which a `praxy.webhook_deliveries` row is deleted — only in a terminal `succeeded`/`failed` status; cascades to its `webhook_delivery_attempts` at the FK level. Never touches `queued`/`delivering` rows regardless of age. |
| `Praxy:Retention:AuditLogMaxAgeDays` | 90 | Age past which a `praxy.audit_log` row is deleted. |

Every rate-limit bucket is partitioned on **project + caller identity**, falling back to the source
address only for callers that present neither an API key nor a session. That matters behind a NAT,
a corporate proxy or a mobile carrier gateway, where thousands of unrelated clients share one
address and would otherwise share one budget. A tripped limit is always loud: `429` (never the
framework's `503` default) with `Retry-After` and the `RateLimit-Limit`/`-Remaining`/`-Reset`
triplet. Those headers appear on the 429 only, not on successful responses — see the known
limitation in [api-reference.md](api-reference.md).

**Org-level quotas** (`Praxy:Quotas:*` above) are the instance-wide defaults. An individual
organization's `organizations.limits` jsonb column can override any of them per-dimension
(`{"maxDatabasesPerProject": 5}`) — there's no console UI for this yet (organizations are hidden in
the console until multi-org ships), so it's a direct SQL edit today:

```sql
UPDATE praxy.organizations SET limits = '{"maxDatabasesPerProject": 5}'::jsonb WHERE id = '<org-id>';
```

Every project's current usage against its effective limit is visible in the console on that
project's Overview page.

## Functions and the Docker socket

`deploy/docker-compose.yml` mounts the host's `/var/run/docker.sock` into the `api` container so
Functions can build and run function containers as siblings. This is root-equivalent access to the
host from inside the `api` container — anyone who can execute code in that container can, in
principle, control every other container and the host filesystem through the socket. There is no
sandboxed alternative in this release. If that tradeoff is unacceptable, comment the volume mount out
— Functions becomes unusable (every build/invoke call fails closed) but the rest of Praxy is
unaffected.

**Reaching invoked function containers.** Because `api` itself runs inside a container in this
stack, it can't reach a sibling function container via a host-published `127.0.0.1` port — its own
loopback is a different network namespace than the host's. `deploy/docker-compose.yml` instead names
its Compose network explicitly (`praxy-functions`, not Compose's default project-derived name) and
sets `Praxy__Functions__DockerNetwork=praxy-functions` on the `api` service; function containers join
that network directly and are reached by container IP, never touching the host's network stack at
all. `Praxy:Functions:DockerNetwork` defaults to empty, which falls back to the old host-port-publish
behavior — correct only when `api` runs bare on the host (`dotnet run`, e.g. local development), not
inside Compose. If you run `api` in a container outside this repo's own compose file, set this to
whatever Docker network that container and its Docker daemon's function containers share.

### Who can invoke a function

Each function carries an `execute` list of roles (`any`, `guests`, `users`, `users/verified`,
`user:<id>`, `team:<id>[/<role>]`, `member:<id>`, `label:<name>` — the same vocabulary table
permissions use). A caller reaching `POST /v1/functions/{id}/executions` must resolve to one of
them, and **an empty list denies everyone**, which is the state every newly created function starts
in.

- **API keys** need the `execution.write` scope *and* a matching role. A key resolves to the role
  `any`, so a key can only reach functions that grant `any` — unless it was created with
  `bypassRowPermissions`, the existing "trusted server, skip the permission layer" flag, which skips
  this gate too.
- **Not gated:** invoking from the console (the operator is already authenticated on the project —
  this is what keeps a deny-by-default function testable), event triggers, and cron schedules. Those
  are operator-configured server-side paths with no external caller to authorize.
- Authorization runs *before* the enabled/deployed checks, so an unauthorized caller gets the same
  `401` whether or not the function exists in a runnable state.

## Sites and the wildcard subdomain

Sites reuses the same `/var/run/docker.sock` mount Functions does (see above — same root-equivalent
access tradeoff, same escape hatch: comment the volume mount out and both features fail closed
together, Sites has no separate opt-out) but on its own Docker network
(`Praxy:Sites:DockerNetwork`, this repo's own compose file sets it to `praxy-sites`, separate from
`praxy-functions` — a site's container and a function's container can't reach each other by default).

**Every hosted site needs a public hostname to be reachable at all**, and that hostname is a
subdomain Praxy invents per site (`<site key>.<project id>.<PRAXY_SITES_DOMAIN>`), not one you
register per site yourself. That means a **wildcard DNS record** is required for a public
deployment — a single static `A`/`AAAA` record, same as `PRAXY_DOMAIN` itself, but with `*.` in
front:

```
*.sites.your.domain.com  ->  <this host's IP>
```

`up.sh` derives `PRAXY_SITES_DOMAIN=sites.$PRAXY_DOMAIN` automatically from the one domain question it
already asks — no second prompt — but it cannot create the DNS record for you. **Without this
wildcard record, every site's public URL will fail to resolve, and Caddy's on-demand TLS ask
endpoint will never even be reached** (the request never arrives) — nothing in `api`'s own logs will
point at a missing DNS record, since `api` has no way to observe a request that never reached it, so
this is worth getting right before deploying a site and wondering why it's unreachable.

Local/plain-HTTP use (no `PRAXY_DOMAIN` configured) needs no DNS setup at all: the default
`PRAXY_SITES_DOMAIN=sites.localhost` relies on every modern browser and OS resolver sending
`*.sites.localhost` straight to `127.0.0.1`.

**TLS is on-demand, not one static wildcard certificate.** `deploy/Caddyfile`'s single host-less
`https:// { tls { on_demand } }` block issues a separate Let's Encrypt certificate per exact hostname,
lazily, the first time each one is actually requested — this needs no DNS-provider API credentials
(unlike a real wildcard cert, which requires DNS-01), but it does need the global
`on_demand_tls { ask ... }` option Caddy calls before minting each one
(`GET /v1/sites/_ask-tls?domain=<host>`, unauthenticated because Caddy calls it, but a strict
allow-list against real enabled+deployed sites only — anything else is `404`, refusing the cert) and
the global `cert_issuer acme` option, which is **not optional**: without it, Caddy's own Automatic
HTTPS logic silently downgrades this block's certificates to its self-signed internal CA instead of
real Let's Encrypt ones, with no error anywhere (found and fixed 2026-08-25, after an earlier fix that
used per-site `issuer acme` directives instead of this global one looked correct but wasn't — see
`docs/research/dotnet-stack.md`'s Caddy section for the full failure signature and root cause if you
ever see self-signed certs after hand-editing the Caddyfile). This one block correctly serves every
hostname depth — site subdomains, preview URLs, and custom domains alike — precisely because it has no
wildcard-depth pattern of its own to violate; an earlier Caddyfile revision used separate
wildcard-depth-specific blocks per hostname shape, which turned out to be the root cause of the
issuer bug above and were removed. `docs/research/dotnet-stack.md`'s Caddy section has the full
verified directive syntax if you're adapting this by hand.

### Preview URLs and idle sweep

Every `ready` deployment — not just the one currently live on the site's production URL — gets its own
reachable preview URL: `<deploymentId>.<site key>.<project id>.<PRAXY_SITES_DOMAIN>`, a third label in
front of the production pattern above. **No extra DNS record is needed** — the same
`*.sites.your.domain.com` wildcard already covers it, since a DNS wildcard matches any number of
labels below its owner name, not just one. `deploy/Caddyfile` needs no extra block for this either —
the single host-less `https://` block described above matches any hostname depth uniformly, so the
three-label preview pattern is already covered by the same block the two-label production pattern
uses.

A preview's container is **not** started until the first request actually hits its URL (bounded by
`Praxy:Sites:StartupTimeoutSeconds`, same as any other cold start), and is stopped automatically once
nobody's requested it for `Praxy:Sites:PreviewIdleSeconds` (`SitePreviewSweeper`, re-checked every
`Praxy:Sites:PreviewSweepIntervalSeconds`) — unlike a site's production container, which always stays
up. `Praxy:Quotas:MaxPreviewContainersPerProject` bounds how many of these can be running for one
project at once, so a project that's accumulated many stale `ready` deployments can't be previewed all
at once into exhausting the host's Docker/memory capacity.

### Custom domains

A site's built-in `<key>.<projectId>.<PRAXY_SITES_DOMAIN>` URL always keeps working — a custom domain
is an additional way to reach a site's **active** deployment, not a replacement (no preview-URL
equivalent for custom domains). Add one from the site's Settings page in the console; it needs no
extra config on this box's side, just a DNS record you configure at your own domain's registrar/DNS
host:

- **A subdomain of your own domain** (e.g. `app.example.com`): add a `CNAME` record pointing at the
  site's own `<key>.<projectId>.<PRAXY_SITES_DOMAIN>` hostname (shown on the Settings page next to the
  add-domain form). This is the recommended path — no need to know or track this box's own IP.
- **An apex/root domain** (e.g. `example.com`): DNS doesn't allow `CNAME` at a zone apex, so point an
  `A`/`AAAA` record directly at this box's own public IP instead — the same IP your `*.sites.<domain>`
  wildcard record already resolves to. Praxy doesn't know or auto-fill this IP for you; it's the same
  one you already configured for `PRAXY_DOMAIN`/`PRAXY_SITES_DOMAIN`.

**`pending` vs `verified`**: a newly added domain starts `pending`. It flips to `verified` on its own,
with no separate step or polling job, the first time a real request through that hostname actually
completes — which only happens after Caddy's on-demand TLS has already gotten it a real Let's Encrypt
certificate (the same on-demand mechanism Phase 1's wildcard hostnames use, generalized to arbitrary
hostnames via an exact `site_domains` lookup — see `docs/research/dotnet-stack.md`'s Caddy section for
the verified automation-policy/shadowing behavior). Until your DNS record resolves to this box,
requests to a `pending` domain simply won't arrive here at all — nothing to debug on the Praxy side; fix
the DNS record and it resolves the next real visit. An unregistered or made-up hostname is rejected by
the `_ask-tls` endpoint before Caddy ever attempts a certificate, so it never gets one.

Removing a domain from the console stops it resolving to the site immediately. It does not touch or
remove the DNS record itself — take that down separately at your registrar if you're retiring the
domain entirely, or it'll keep pointing at this box with nothing behind it.

Same Caddyfile landmine as the section above applies here too: this feature adds a new catch-all
`https://` site block to `deploy/Caddyfile`. **After deploying it, restart Caddy** (`docker compose -f
deploy/docker-compose.yml restart caddy`) — bind-mounting the new file alone isn't enough, Caddy only
reads it at its own startup. See the Upgrading section below.

## Git integration

Connect a site to a GitHub repository so a push to its production branch builds and goes live
automatically, and a push to any other branch builds a preview (its own Phase 2 preview URL, above)
without ever touching production. This needs **your own GitHub App** — there is no shared
Praxy-provided one, the same self-host story Appwrite's own git integration follows. Nothing here
works until you create one and set the five `Praxy:Vcs:GitHub:*` config values above; until then the
console's GitHub settings page shows a clean "not configured" message rather than an error.

**Your instance must be internet-reachable for this to work at all** — GitHub delivers the webhook
that drives the whole feature to a real public URL, so a bare `dotnet run` on `localhost` needs a
tunnel (ngrok or equivalent) to receive it. If you're running the `up.sh`/`docker-compose.yml` stack
with a real `PRAXY_DOMAIN`, you already have what you need.

### Create the GitHub App

From your GitHub account or organization: **Settings → Developer settings → GitHub Apps → New GitHub
App**.

1. **GitHub App name** — anything; it only shows up in GitHub's own UI and in the install URL
   (`github.com/apps/<your-app-name-slug>`).
2. **Homepage URL** — anything reachable; GitHub requires a value but Praxy never calls it.
3. **Callback URL** — `https://<your domain>/v1/vcs/github/callback`. This is GitHub's own
   installation-flow "Setup URL" field, not an OAuth login callback — check **"Redirect on update"**
   so re-configuring an existing installation also lands back here.
4. **Webhook** — check **Active**. **Webhook URL**: `https://<your domain>/v1/vcs/github/webhook`.
   **Webhook secret**: generate a random value yourself (e.g. `openssl rand -hex 32`) and remember it
   — this becomes `Praxy:Vcs:GitHub:WebhookSecret`, never entered anywhere on GitHub's side beyond this
   one field.
5. **Repository permissions** — **Contents: Read-only** (needed to receive push events and clone).
   **Metadata: Read-only** is on by default and can't be turned off.
6. **Subscribe to events** — check **Push**. Nothing else is needed; Praxy ignores every other event
   type it might receive (see the Landmines note in `docs/handoff/sites-phase-4-report.md` for why
   commit statuses/PR comments were deliberately left out of this phase).
7. **Where can this GitHub App be installed?** — your call; "Only on this account" is the tighter
   default if you're the only one who'll ever connect a repository.

Create the App, then on its settings page: note the **App ID** (`Praxy:Vcs:GitHub:AppId`), the
**Client ID** (`Praxy:Vcs:GitHub:ClientId`), and **Generate a new client secret**
(`Praxy:Vcs:GitHub:ClientSecret`). Under **Private keys**, **Generate a private key** — this downloads
a `.pem` file.

### The private key value

A `.pem` file's real newlines don't survive a single-line `.env` value or `docker-compose.yml`
`environment:` entry cleanly. `Praxy:Vcs:GitHub:PrivateKey` accepts the key either way, but base64 is
the recommended path:

```bash
base64 -i your-app-private-key.pem | tr -d '\n'
```

Set the result as `Praxy:Vcs:GitHub:PrivateKey` (or `PRAXY_VCS_GITHUB_PRIVATEKEY` as an env var). If
you'd rather paste the raw PEM text, that works too — Praxy tries a base64 decode first and falls back
to using the value as-is when that fails (PEM's own `-----BEGIN...` framing isn't valid base64, so the
fallback is automatic, not something you need to configure).

Set all five values, restart `api`, then in the console go to **Settings → GitHub** and click
**Connect GitHub** — this redirects to GitHub's own installation flow; approve it for the account/org
and repositories you want reachable, and GitHub redirects back to the Callback URL above, which
records the installation. From there, connect a specific site to a specific repository and pick its
production branch from the site's own Settings page, the same place custom domains live.

**Repository access is always checked live against GitHub**, never cached — Praxy doesn't store which
installation covers which repository, only that *some* installation exists (a cheap "has GitHub been
connected to this instance at all" gate). Revoking the App's access to a repository on GitHub's side
takes effect on the very next push or console action that touches it, with no separate step on the
Praxy side.

### Cloning

A git-sourced build clones the exact pushed commit by shelling out to the system `git` CLI (not a
library) — the `api` container image installs it for this reason; if you're running `api` bare
(`dotnet run`, not the Docker image), make sure `git` is on `PATH` yourself. The clone happens inside
the build worker, in a fresh temporary directory deleted once the build finishes — success or
failure — the same discipline an uploaded tar's bytes already follow.

## Backup and restore

Two things need backing up, because they live in different Postgres schemas and neither one alone is
useful without the other:

- **`praxy`** — the system catalog: organizations, projects, users, sessions, and every database's
  metadata (`praxy.databases`/`tables`/`columns`/`indexes`/…). One schema, whole-instance.
- **`px_<database-id>`** — the actual rows and tables for one database, one schema per database
  (architecture.md §4.1). A project with three databases has three of these.

Restoring `px_<id>` without `praxy` leaves real tables in Postgres that the API has no metadata for
— invisible and unreachable. Restoring `praxy` without `px_<id>` leaves metadata pointing at tables
that don't exist — every request against that database 404s or 500s. Back up both, restore both.

### Backing up

```bash
cd deploy && ./backup.sh
```

Writes `praxy.dump` and one `px_<id>.dump` per existing database schema into
`deploy/backups/<UTC timestamp>/` (pass a directory as `$1` to choose your own — e.g. one outside the
repo, on a volume that actually gets backed up elsewhere). Each `pg_dump` runs in its own consistent
snapshot transaction, so this is safe to run against a live instance — no downtime, no need to stop
the `api` container first. Run it on a schedule (cron, systemd timer, whatever the host already has)
pointed at storage that isn't the same disk as the Postgres volume.

### Restoring

```bash
docker compose stop api          # see why, below
cd deploy && ./restore.sh backups/<the timestamp you want>
docker compose start api
```

`restore.sh` restores `praxy.dump` first, then every `px_<id>.dump` alongside it, using
`pg_restore --clean --if-exists` — safe to run against an instance that already has (possibly
corrupted or partial) copies of these schemas, not just an empty database.

**Stop the `api` container first.** The schema engine keeps an in-memory catalog cache per project
(architecture.md §4.6), invalidated by schema-change events published on the write path — a raw
`pg_restore` doesn't go through that path, so a running API process has no way to know its cached
metadata just went stale underneath it. Restart it after restoring so it boots with a cold, correct
cache.

### Verified

This procedure was run end to end as part of Phase 9 hardening, not just written down: a database
with a real table and a real row was backed up with `backup.sh`'s exact commands, then **both**
schemas were dropped entirely (`DROP SCHEMA … CASCADE` on `praxy` and the database's `px_<id>` —
total loss, not a partial failure) to prove the backup is actually sufficient on its own, not
leaning on anything left behind. Both dumps were restored, the API was restarted cold, and the
console showed the exact same project, database, table, and row — same ids, same `physical_name`s,
same row content and `_created_at` timestamp — as before the simulated disaster. See
`docs/handoff/phase-9-report.md` for the full transcript.

## Upgrading

> **Breaking change when upgrading past v0.1.0 — function execute permissions.**
> Functions now carry an `execute` role list and **a function nobody has been granted is invokable
> by nobody**, the same deny-by-default posture a new table has. Before this change, any caller
> holding a project id could invoke any enabled function; the migration backfills existing
> functions to *deny* rather than migrating that hole forward silently.
>
> After upgrading, open **Functions → (a function) → Settings → Execute access** and grant the
> roles each one needs — `any` reproduces the old behaviour, `users` restricts to signed-in app
> users. Functions with nothing granted are flagged `no execute access` in the functions list.
> Invoking from the console, event triggers and cron schedules are **not** affected and keep
> working through the upgrade untouched.

Upgrading is: take a backup (above — migrations are forward-only, there is no `down`), then
`docker compose pull && docker compose up -d` (or rebuild from a newer tag). That's the whole
procedure. `CatalogMigrator` runs every pending EF Core migration at startup, in order, under a
session-level Postgres advisory lock (`SELECT pg_advisory_lock(...)` on a dedicated connection held
for the whole migration, not `pg_advisory_xact_lock` — migrations span multiple transactions, so a
transaction-scoped lock would release too early). No manual migration step, and if you're running
more than one `api` replica, they won't race each other: whichever one wins the lock runs the
migrations while the others block, then find nothing pending and continue straight to serving
traffic.

> **If your upgrade touches `deploy/Caddyfile`, also restart Caddy — `docker compose up -d --build`
> alone does not.** Found live on praxycore.dev: the Sites Phase 2 release added a third wildcard
> block to `deploy/Caddyfile` (preview URLs), but every deploy since then only recreated `api` — the
> documented, intentional behavior ("rebuilds and recreates only the `api` container; Caddy/Postgres
> stay up untouched"). Caddy bind-mounts the file (`./Caddyfile:/etc/caddy/Caddyfile:ro`) and parses
> it once at startup; a bind mount makes the new bytes visible inside the container immediately, but
> Caddy itself doesn't watch the file and re-read it on its own. The result: the file on disk was
> correct, but the *running* Caddy process kept serving its original config for days, so every
> preview URL silently failed TLS (`tlsv1 alert internal error`, no ask-endpoint call at all — the
> same symptom as a genuinely wrong Caddyfile, indistinguishable from the outside). `caddy reload
> --config /etc/caddy/Caddyfile --adapter caddyfile` (graceful, meant for exactly this) did **not**
> reliably pick up the change either in this environment — only a full
> `docker compose restart caddy` did. Confirmed via `docker exec praxy-caddy-1 caddy adapt --config
> /etc/caddy/Caddyfile` before/after: the automation policy's `subjects` list only gained the new
> wildcard pattern after the restart. **Safest habit: `docker compose restart caddy` after any
> upgrade, whether or not you think the Caddyfile changed** — it's a cheap, near-zero-downtime
> operation for a reverse proxy, and confirming "did the Caddyfile change" by eye is exactly the kind
> of check that's easy to get wrong.

**Verified against real data for v0.1.0** (there being no previous tag to literally check out yet,
per the roadmap this proves the mechanism rather than replaying a specific past release):

1. A fresh database was migrated only as far as `20260815184658_InitialCatalog` — the very first
   migration, Phase 0's schema — via `dotnet ef database update InitialCatalog`, then seeded with
   realistic pre-existing data by hand at that schema shape: an organization, a console operator
   user with a membership, an app project, and an audit log entry, all dated weeks in the past.
2. The **current** `Praxy.Api` binary (all 7 migrations through Phase 8's `Messaging`) was pointed at
   that database and started normally — the real startup path, not a manual `dotnet ef` invocation.
   `CatalogMigrator` logged `Applying 6 catalog migration(s): [...]` and applied
   `AuthTables`/`SchemaEngine`/`DataPlane`/`Webhooks`/`Functions`/`Messaging` in order; the process
   came up healthy on the first try.
3. All 35 current-schema tables existed afterward, and every seeded row was intact and unchanged —
   same ids, same values, same timestamps. `POST /v1/console/claim` against the upgraded instance
   returned `409 instance_already_claimed`, proving the app's real query path (not just direct SQL)
   correctly recognizes the pre-upgrade operator account through the new schema.
4. Restarting the same binary against the now-current database logged `Catalog is up to date` and
   applied nothing — the idempotent no-op a multi-node rollout depends on.

## Health and logs

- `GET /v1/health` — liveness/readiness, used by the compose file's own healthcheck-adjacent startup
  ordering.
- `docker compose logs -f api` — structured Serilog output; the instance setup token (if
  `PRAXY_PUBLIC_URL` is set and the instance is unclaimed) is printed here.
- The OpenAPI document and its interactive Scalar UI (`/scalar/v1`) are **development-only** — they
  disclose the full API surface and are not mounted in the production image. See
  `docs/api-reference.md` for how the reference ships for a production instance instead.
