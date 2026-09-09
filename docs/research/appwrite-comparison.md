# Appwrite 2.0 self-hosted, measured against Praxy — what 1.0 is missing

Notes from installing **Appwrite 2.0.0 self-hosted** locally (2026-09-09) and reading its running
stack, its `.env`, its install flow and its console, alongside the Appwrite Cloud console.

The point of this document is to scope Praxy 1.0 honestly: what a self-hoster gets from the
incumbent that they would not get from us, and — equally — where we already give them more, so we
don't spend 1.0 rebuilding things we've already beaten.

## Where Praxy is already ahead

Worth stating first, because it is easy to look at Appwrite Cloud's console and conclude we are
behind everywhere.

**Organizations.** Appwrite's own self-hosting docs are explicit:

> "A self-hosted Console starts with one organization. Appwrite creates it the first time you sign
> in… **The Console does not offer a way to create more**… **Members you invite to the organization
> get owner access to every project on the instance. There are no organization roles on a
> self-hosted instance.**"

Praxy ships multi-organization creation, `owner`/`member` roles enforced through one choke point,
email invites with a seat quota, and a last-owner guard. On this axis a self-hosted Praxy is
materially more capable than a self-hosted Appwrite, and it is the axis that matters most for a team
rather than an individual.

**Console SSO** is a Cloud-only feature for them too — the reason we removed ours still holds, and
their line is drawn in the same place for the same reason (see `console-oauth-removal-report.md`).

## Stack and architecture

| | Appwrite 2.0 | Praxy |
|---|---|---|
| default database | **PostgreSQL** (MariaDB, MongoDB also offered) | PostgreSQL only |
| usage metrics | **ClickHouse** | none |
| containers (default topology) | 13–15 | 3 |
| worker topology | install-time choice: Combined (one worker) or Separate (one container per queue) | hosted services inside the API process |
| extra services | `geo`, `browser`, `embedding`, `assistant` | — |

Two things stand out.

**PostgreSQL is now their default.** Our Postgres-only constraint, which read as a limitation
against MariaDB-era Appwrite, is now the same choice the incumbent leads with. That is a bet that
aged well and it should be said plainly in our positioning rather than treated as a compromise.

**ClickHouse is how they do usage charts.** Bandwidth and request graphs are not free; they are a
whole OLAP store next to the primary database. This is the direct answer to the decision recorded on
the project overview screen: we chose not to fabricate a "requests" number because nothing meters
data-plane traffic. Metering it properly is a dedicated store plus a rollup pipeline — a real
initiative, not a screen.

## Install and first run

Appwrite 2.0 replaced its terminal installer with a **browser setup wizard** on `:20080`: hostname
and HTTPS, database engine, worker topology, HTTP/HTTPS ports, SSL certificate email, an optional
OpenAI key, a generated secret key with a "you won't see this again" warning, and an **optional**
first account — you may leave it blank and create one from the console later.

Praxy's `./up.sh` asks one question. For the common case ours is better: fewer decisions, faster to a
running instance. Theirs earns its extra steps by covering cases we don't have (engine choice,
topology, non-default ports as first-class rather than post-hoc `.env` editing).

Worth stealing: the **secret-key step**. Ours generates `PRAXY_SECRET_KEY` silently into `.env`. An
operator who never learns that key exists is one `rm .env` away from unrecoverable encrypted data,
and `docs/self-host.md` is the only place that says so.

## Configuration surface

**211 `_APP_*` knobs** against Praxy's 96 config keys (45 documented in `docs/self-host.md`). Their
largest groups, and what they imply we're missing:

| group | count | what it covers that we don't |
|---|---|---|
| `_APP_STORAGE` | 27 | S3/DO/Backblaze/Linode adapters — we are Postgres-only by design |
| `_APP_VCS` | 21 | broader git provider support |
| `_APP_CONSOLE` | 15 | console allow-lists (emails, IPs, hostnames), session alerts |
| `_APP_MAINTENANCE` | 11 | retention/cleanup schedules per resource |
| `_APP_GRAPHQL` | 4 | a GraphQL API we don't have at all |
| `_APP_SMTP` | 5 | per-instance SMTP (they also do per-project) |

`_APP_CONSOLE_WHITELIST_EMAILS` / `_WHITELIST_IPS` are notable: a self-hosted instance can restrict
who may even reach the console. We have nothing equivalent, and for a self-hosted admin surface it
is a cheap, high-value control.

## The console, once claimed

Screens that needed a logged-in instance to see.

**The project dashboard is onboarding-first, not inventory-first.** It leads with five metered stats
carrying percentage deltas — Bandwidth, Requests, Storage, Executions, and **Compute in GB-hours** —
over a 15m/1h/1d range selector, then a "Bandwidth over time" chart and a "Top bandwidth consumers"
breakdown. Below that: **Apps** ("Connect your first app" — Web, React Native, Flutter, Apple,
Android, Windows, Linux) and **API keys** with quickstarts in twelve languages.

Ours is the opposite emphasis: what exists in the project, and where to go. Theirs is better on day
one, ours is better on day one hundred. The honest read is that we are missing the day-one half
entirely — a new Praxy project tells you nothing about how to connect anything to it.

Note this also corrects an earlier assumption in this document: **usage metering is present on
self-hosted**, not Cloud-only. It is ClickHouse-backed and it is the reason `Compute (GBH)` exists —
that is billing infrastructure, whatever else it is.

**An in-browser terminal.** The console embeds the Appwrite CLI — "Session and project context are
configured automatically", non-interactive commands only. `appwrite users list --json` without
installing anything. That is a genuinely strong console feature and not expensive to be inspired by.

**MCP, as a distribution play.** Project settings carry a copy-pasteable MCP setup with tabs for
Claude Code, Codex, Cursor, Claude Desktop, VS Code and OpenCode:

```
claude mcp add appwrite --env APPWRITE_PROJECT_ID=… --env APPWRITE_API_KEY=… -- uvx mcp-server-appwrite
```

plus example prompts to confirm it works. This is the item I would think hardest about. It is not a
feature so much as a bet that the next developer to evaluate a BaaS will ask their coding agent to
drive it, and Appwrite has made itself the path of least resistance for that. For a project whose
current constraint is that nobody has heard of it, that is worth more than most of the feature gaps
below.

**Git providers**: GitHub, GitLab *and* Bitbucket. We support GitHub only.

**Self-hosted settings tabs**: Overview, Custom domains, Variables, Webhooks, Migrations, SMTP. No
Labels, no "change organization", no Usage tab (usage lives on the dashboard) — those are Cloud-only
or single-org consequences.

Two small validations of work landed this week: their project header carries an **API endpoint chip**
beside the project id, and their organization page is **Projects | Settings tabs with an Invite
action**. We arrived at both independently, days earlier.

## By subsystem

Walked the console with a claimed instance, creating a database and table to reach the real dialogs.

### Auth

Their **auth methods** are individually toggleable: Email/Password, Phone, Magic URL, Email OTP,
Anonymous, Team Invites, JWT. We have email/password and Google OAuth, by a fixed decision.

Three of those are worth reopening that decision for:

- **Anonymous sessions.** Let someone use the app before registering, then convert the account.
  This is table stakes for mobile, it is how Firebase and Appwrite both onboard, and nothing in
  Praxy models it.
- **Email OTP** and **Magic URL** — passwordless, and increasingly what users expect over a
  password form.

Beyond methods, `Auth → Policies → Sessions` has four controls; we have one (session limit):

| | Appwrite | Praxy |
|---|---|---|
| session limit per user | ✅ | ✅ |
| session length | ✅ (max 365d) | ❌ |
| **session alerts** — email on new session | ✅ | ❌ |
| **invalidate all sessions on password change** | ✅ | ❌ |

That last one is a genuine security gap rather than a nicety: today a Praxy password change leaves
every stolen session alive.

Also present and absent from us: **MFA with recovery codes** (`/account/mfa`), **per-user activity
logs** (`/account/logs`), **Presences**, and mock phone numbers for App Store review.

### Databases

Column types line up better than expected. Theirs: Text, Mediumtext, Longtext, Varchar, Integer,
Bigint, Float, Boolean, Datetime, Email, IP, URL, Enum, Relationship, Point, Line, Polygon. Ours
covers all of it except geometry beyond a point — and our `integer` is already 64-bit
(`bigint` in Postgres), so their Integer/Bigint split is a MySQL artifact rather than a capability.
Their four text sizes are our `string` plus `size`.

The real gaps:

- **Line and Polygon**, with `within`/`intersects`/`contains`. This is precisely our deferred **geo
  Phase 4** (`docs/research/geo-nearby.md:157`) — the comparison independently landed on the same
  scope we already wrote down and postponed.
- **Encrypted columns.** A per-column toggle: "Values are encrypted at rest (AES-128-GCM). No plain
  text is stored. Encrypted columns cannot be used for queries." We have nothing equivalent, and for
  a self-hosted product holding other people's PII it is an easy thing to be asked for.
- **Visualizer** (schema/ERD), **Monitor**, and **Export / Import** — three database-level tabs with
  no Praxy equivalent. Export/Import matters most: our only story is instance-wide `backup.sh`.
- **Generate sample data** on an empty table. Small, and exactly the kind of thing that makes an
  empty console feel less like homework.

### Console-wide

- **Explorer.** A full API explorer *inside* the console: Client/Server API toggle, every endpoint
  grouped by service, required scopes, an "Act as Guest / User" switch, Copy as cURL, and **Send
  request** against the live project. Ours is Scalar at `/scalar/v1`, dev-only, with no project
  context.
- **In-browser terminal** running the Appwrite CLI with session and project pre-configured.
- **Usage** as a first-class nav item.
- An onboarding checklist — "Get started · 0 of 14 completed".

## Product surface we lack entirely

Ordered by what I would actually put in 1.0:

1. **Per-project service toggles.** Their Settings → Services disables a service *for client SDKs
   while server SDKs keep working* (13 of them). A security control, not a preference. Our
   `CapabilitiesResponse.features` looks similar but is eight hardcoded `true` literals used to hide
   nav items — there is no enforcement anywhere.
2. **Console access controls** — `_APP_CONSOLE_WHITELIST_EMAILS`/`_IPS`. Cheap; directly reduces the
   blast radius of the console being internet-reachable.
3. **Per-project SMTP.** Our auth mail goes through one instance-wide `SmtpEmailSender` singleton, so
   every project on an instance mails from the same sender. The machinery already exists — Messaging
   has per-project `EmailProviderConfig` and an `EmailProviderResolver`; auth just doesn't use it.
4. **Project-level environment variables.** We have per-function and per-site env vars; this is a
   defaults layer over plumbing that already exists.
5. **Usage/metering.** Only worth starting if we accept the ClickHouse-shaped cost above.
6. **GraphQL**, **Avatars**, **Locale**, **Migrations** (Firebase/Supabase importers), **project
   labels**, **API custom domains**, **MCP server**. Each is real, none is 1.0-blocking.

## What not to copy

MongoDB/MariaDB support (our single-datastore constraint is a feature, and their default agrees with
us now); Premium Geo DB and the Upgrade CTAs (commercial); the Assistant and embedding services (a
different product bet). Their console also carries a permanent "Introducing the new Appwrite Console"
banner and an Upgrade button — self-hosted software advertising its cloud tier is exactly the
texture a self-hoster resents, and it is free for us not to do.

## The gap that is ours alone

Nothing in Appwrite's comparison surfaced it, but it is the most urgent 1.0 item found this week:
**Praxy never reclaims Docker images or build cache.** Production had 42.31GB of build cache on a
77GB disk (see the container-reclamation work of 2026-09-08). Old images are rollback targets, so the
fix is bounded retention, not a blanket prune. A self-hosted instance that deploys regularly will
fill its disk with no warning, and `docs/self-host.md` does not mention pruning. That ships before
1.0 or 1.0 has a time bomb in it.
