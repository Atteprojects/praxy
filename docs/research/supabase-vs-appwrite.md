# Research — Supabase vs Appwrite, and what Praxy takes from each

Teardown of both platforms from their shipping compose files, source repos, docs and support threads.
Snapshot Aug 2026: Supabase on Postgres 17.6.1 / PostgREST 14.12 / GoTrue 2.189 / Realtime 2.102.3;
Appwrite on the 1.8.x line.

This file records **what Praxy adopts and why**. The full comparison is condensed to the decisions.

---

## Decisions this research confirmed

### Synchronous transactional DDL — confirmed, and more important than assumed

Appwrite creates attributes asynchronously through a worker queue; the attribute sits in `processing` until a
worker completes it. This has produced a **multi-year tail of "attribute stuck in processing" bugs**
(appwrite#8595, #9048, #10032, #10037) including attributes that cannot be deleted and that block row updates.
On their Cloud, processing can lag up to two hours behind backups.

Praxy's plan — metadata and DDL committing in one Postgres transaction, with only `CREATE INDEX CONCURRENTLY`
and backfills taking the job queue — avoids this class entirely. **The rule to hold: DDL is synchronous and
transactional; genuinely long operations are explicit, queryable, cancellable jobs, never a silent background
mutation of the schema.**

### Realtime: Appwrite's architecture wins, decisively

Supabase's WALRUS re-derives authorization *per subscriber per WAL record inside the database* — it assumes each
subscriber's role, sets claims, re-`SELECT`s the row by primary key to test RLS, then filters columns. That is
O(subscribers) in-database work per change, single-threaded to preserve ordering. Their published numbers:

- ~**64 changes/sec at 500** subscribers → ~**1 change/sec at 30,000**
- "Larger compute add-ons don't meaningfully increase Postgres Changes throughput" — the bottleneck is the
  ordering thread, not CPU
- Their own docs now say use Broadcast instead above ~3,000 subscribers

Appwrite resolves roles **once at handshake** and indexes subscriptions as project → roles → channels →
connections, plus a reverse connection map so disconnect cleanup doesn't scan. Fan-out is a hash lookup.
Published: **10,000 subscribers in 0.022 ms / 11 MB**; 5,000,000 in 19.3 ms / 4.3 GB.

Praxy's design already matches Appwrite's. Two additions taken from this research:

- **Permission-changed invalidation as a flag on the event**, triggering revalidation of live connections —
  rather than re-authorizing every event.
- **Materialize visibility at write time.** The outbox row carries `visible_to: [role…]` computed in the same
  transaction as the mutation. One log read, N hash lookups. This also **fixes DELETE authorization for free** —
  the visibility set is computed while the row still exists. Supabase explicitly does not apply RLS to deletes
  and ships every replica-identity column to every subscriber, pushing the risk onto the user.

### One datastore — confirmed

Appwrite's default compose is **31 services, 15 volumes, 4 datastores** (MariaDB + MongoDB + PostgreSQL +
Redis), 1,432 lines. Supabase is 11 services and one datastore. Every extra datastore multiplies the backup /
restore / point-in-time matrix, which is the thing self-hosters reliably get wrong.

Praxy: Postgres only. Relational, JSONB, pgvector later, and a job table with `SKIP LOCKED` instead of a broker.

### Keyset pagination by default — confirmed

Both platforms default to offset (Appwrite 25 rows with `limit`/`offset`; PostgREST Range headers). Deep offsets
are O(n) scans that per-row permission filtering makes dramatically worse. Praxy derives a cursor from the sort
key by default and makes offset opt-in and bounded.

---

## Adopted from Appwrite

1. **Row-attached permissions as data**, with a real role vocabulary. Praxy extends its planned set with the
   verification-aware roles, which are genuinely well designed:

   ```
   any · guests · users · users/verified
   user:<id> · user:<id>/verified
   team:<id> · team:<id>/<role> · member:<id> · label:<name>
   ```

   `member:<id>` is particularly good — access dies with the membership row.

2. **A per-user session cap with oldest-session eviction** (Appwrite defaults to 10, max 100). Almost nobody
   ships this and everybody needs it. Cheap to add in Phase 1.

3. **Batteries-included credential hygiene:** Argon2, a 10k common-password dictionary, rejection of passwords
   containing the user's own name/email/phone, password history, and session alerts on new login. Supabase makes
   you build most of this.

4. **Opt-in relationship loading.** Nothing related comes back unless requested — structurally prevents the
   payload explosion PostgREST's embedding syntax invites.

5. **The open-runtimes contract** for Phase 7 functions: a runtime is *an HTTP server in a container that
   validates a shared secret header*, with build phase (`build.sh`) and start phase (`start.sh`) separated and
   the build artifact packed. That contract is language-agnostic by construction — it is why Appwrite has 18
   runtimes and Supabase has one. Steal the contract; the orchestration is negotiable.

6. **An honest hard cap on synchronous execution** (Appwrite: 30 s) with a real queue behind async, rather than
   letting people build 400-second request/response calls.

7. **Declarative, round-trippable resource config** covering auth settings, buckets and function config — not
   just the database.

---

## Adopted from Supabase

1. **Refresh-token rotation with reuse detection**, if and when Praxy mints refresh tokens: single-use opaque
   tokens, a ~10 s reuse window plus parent-token tolerance to survive retries and races, and reuse outside that
   window revoking the entire token family. Exactly the right shape.

2. **Asymmetric signing keys with JWKS and a real key lifecycle** — standby → current → previously used →
   revoked, every transition reversible except deletion, rotation without logging anyone out, clients verifying
   locally. This is the best-engineered component across both products. Praxy uses opaque sessions on the auth
   plane, so this applies to the JWTs minted for functions and service-to-service calls — but design the key
   table and JWKS endpoint when JWT minting first appears, not after.

3. **Assurance level as a claim** (`aal`, `amr`). Appwrite expresses MFA state as an *error code*, so "this data
   requires MFA" cannot be an authorization rule — it is a login gate only. Praxy puts an assurance level on the
   session from Phase 1 (even before MFA exists) so `role:user:<id>/mfa` style rules stay possible later.

4. **Versioned, ordered, replayable migrations with a `reset` that replays from zero.** The replay is what makes
   the history trustworthy. Appwrite's `push`/`pull` is state synchronization — no ordered history, no down
   path, no proof the history reconstructs the database.

5. **Codegen emitting distinct `Row` / `Insert` / `Update` types per table**, derived from real constraints:
   generated columns typed `never` on insert, not-null-without-default required, nullable optional. Most tools
   emit one flabby interface; three is correct. Applies to the Flutter SDK's codegen tier.

---

## Doing differently from both

1. **Deny by default, with an explicit API manifest.** Supabase's dominant failure mode is that exposing a
   schema publishes every table in it — misconfigured RLS is the most common critical finding in third-party
   pentests of Supabase apps. In Praxy nothing is client-reachable until permissions are explicitly set, and the
   console says so loudly on any table with none.

2. **Never leak the storage engine's physical limits as an opaque error.** Appwrite's attributes-as-columns
   model means InnoDB's ~8,126-byte row budget surfaces as a raw SQL error at roughly 100 utf8mb4 varchars, and
   fulltext index creation fails outright because InnoDB can't add its hidden `FTS_DOC_ID` column. Postgres is
   far more forgiving, but Praxy still **computes a row byte budget at schema-definition time** and rejects with
   a message naming the offending columns and the remaining budget.

3. **Ship one realtime primitive, not four.** Supabase has Postgres Changes, Broadcast, Broadcast-from-Database
   and Presence, with three different authorization models between them — one of which the vendor now advises
   against. Praxy ships the outbox-backed broadcast, exposes "watch this table" as the ordinary case, and makes
   presence a channel type on the same primitive.

4. **Every credential can subscribe to realtime.** Appwrite flatly bars server SDKs and API keys from realtime,
   which forces awkward architectures for anything server-side. Derive the authorization key set from whatever
   credential is presented.

5. **Async executions must return something.** Appwrite's async function executions discard the response body
   and headers entirely, so an async function can't return a result. Store the execution record with its output.

6. **Limits are configurable, observable, and loud.** The top "why is my app broken" reports for both platforms
   are limits that fail silently — Supabase's default 2 auth emails/hour, Appwrite's 10 JWTs/hour/account, and
   IP-bucket rate limiting that misfires behind a proxy. Every limit is project-configurable, responses carry
   `RateLimit-Remaining` / `RateLimit-Reset`, and a tripped limit logs a distinguishable error.

7. **The self-hosted build is the product.** Supabase self-hosted lacks a storage explorer, logs view,
   branching, PITR and any documented HTTPS story — and its default compose has destroyed people's databases by
   recreating the volume. Appwrite's upgrade path forbids skipping minor versions, is serial, and its own
   support threads document it emptying databases in the field.

   Praxy's bar: **one compose file that boots to a working instance with persistent volumes by default;
   `backup`, `restore` and `upgrade --dry-run` as first-class CLI verbs; forward-only migrations that are
   idempotent and resumable from any interruption point; every console feature present in self-host.**

---

## Warnings worth remembering

- **RLS performance is a cliff, not a slope.** Supabase's own troubleshooting table shows an 11,000 ms → 7 ms
  improvement from wrapping `auth.uid()` in `(select …)`, and 178,000 ms → 12 ms from a `SECURITY DEFINER`
  wrapper. Nothing in the system tells you which version you wrote. Praxy filters permissions in the application
  layer against an indexed side table, which is more predictable — but the lesson generalizes: **any per-row
  function call in a hot filter is a landmine.** Keep the permission predicate a plain indexed join.

- **Appwrite's permissions have no in-database backstop.** They are enforced in the application layer only, so
  anything reaching the database directly sees everything. Praxy has the same property by design — which is
  exactly why the per-project low-privilege Postgres role hardening (v1.1 in the architecture doc) matters more
  than it first appeared.

- **A diff engine must be total or fail loudly.** Supabase's declarative diff silently omits `ALTER POLICY`,
  column- and schema-level privileges, materialized views, view `security_invoker`, publications, partitions and
  comments — that is, most of the security-relevant DDL. A partial migration that looks complete is worse than
  no differ at all.
