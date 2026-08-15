# Research — Appwrite API surface (v1.9.6), and what Praxy adopts

Source: study of `appwrite/appwrite` 1.9.6 source (not docs — the docs proved consistently vaguer), web SDK
26.2.0, node SDK 28.0.0. Distilled to Praxy's wire-format decisions.

---

## Praxy wire-format decisions

### Headers

```
X-Praxy-Project        project id, required on every request
X-Praxy-Key            API key (server-side)
X-Praxy-Session        session secret (alternative to cookie)
X-Praxy-Api-Version    response compatibility version (Appwrite: X-Appwrite-Response-Format)
X-Praxy-Request-Id     response-only; echoed in error bodies
```

Cookie: `praxy_session_<projectId>`. Appwrite's downgrade-filter chain (older declared versions apply
cumulative response transforms) is the mechanism to copy when v2 responses first diverge — not before.

### Permission grammar — adopt Appwrite's string form verbatim

```
<action>("<role>")      action := read | create | update | delete | write
```

`write` is an **aggregate**: expanded to create+update+delete at write time, never stored, never returned.
Roles (from research/supabase-vs-appwrite.md): `any`, `guests`, `users`, `users/verified`, `user:<id>`,
`user:<id>/verified`, `team:<id>`, `team:<id>/<role>`, `member:<id>`, `label:<name>`. One dimension max.
Compact, battle-tested, and Appwrite-familiar developers keep their mental model.

### Query DSL — adopt the JSON-per-query wire format

```json
{"method":"equal","attribute":"title","values":["Hello"]}
```

Sent as repeated `queries[]` params (GET) or a `queries` array (body). Nested logical queries embed query
*objects*, not strings. Praxy caps where Appwrite doesn't: **max 100 queries × 4096 chars** (Appwrite's real
enforced limits), **nesting depth 3** (Appwrite: unbounded — their validator recurses without a depth
counter), **max limit 100** (Appwrite's documented 5000 cap is not actually enforced in the OSS validator —
`Range(1, PHP_INT_MAX)`). Praxy v1 methods:

```
equal notEqual lessThan lessThanEqual greaterThan greaterThanEqual between
isNull isNotNull startsWith endsWith contains search
select orderAsc orderDesc limit offset cursorAfter cursorBefore and or
```

(Appwrite 1.9 also has notBetween/notStartsWith/regex/spatial/vector families — additive later, don't
constrain for them now.)

### TablesDB shapes — adopt with corrections

Base path: `/v1/databases/{db}/tables/{table}/...` (Appwrite moved to `/v1/tablesdb`; the split vocabulary —
path says `tablesdb`, channels say `databases...tables...rows` — is rename debris, not design. Praxy keeps one
vocabulary everywhere).

- Column creation per type: `POST .../columns/{type}` with `{key, required, default?, array?, ...}` plus
  type-specifics (`size` for string, `min/max` for numerics, `elements` for enum). **Wire key is `default`** —
  Appwrite's SDKs say `xdefault` only because of JS reserved words; Dart/C# don't need that.
- Status enum: `available | processing | failed` (+ `deleting` transiently). Appwrite's fourth state `stuck`
  exists because their worker queue loses jobs — with synchronous DDL Praxy structurally can't need it. Don't
  add it.
- Row system fields: `$id`, `$createdAt`, `$updatedAt`, `$permissions`, `$tableId`, `$databaseId`.
  `$sequence` (Appwrite's int64-as-string, which forces their web SDK through json-bigint) is **omitted**.
- List responses: `{"total": n, "rows": [...]}` with `total: false` opt-out to skip the count query.
- PATCH row is genuinely partial (constraint already recorded in research/flutter-sdk.md).

### Realtime protocol — adopt message-mode only

```
wss://<host>/v1/realtime?project=<id>          (+ ticket=<t> for non-browser clients)
```

- **No URL-mode channels.** Appwrite supports both `channels[]` in the URL and post-connect subscribe
  messages; ship only the message mode — one protocol path.
- Client→server: `{"type":"ping"}` (20s), `{"type":"subscribe","data":[{subscriptionId, channels, queries?}]}`
  (batched, client-generated ids), `{"type":"unsubscribe","data":[{subscriptionId}]}`.
- Server→client: `connected` (carries user or null), `response`, `event`, `pong`, `error`.
- Event envelope carries `events[]` (wildcard-expanded names), `channels[]`, **`subscriptions[]`** (which of
  the caller's subscriptions matched — this is what lets the SDK fire exact callbacks instead of re-matching
  channel strings; Appwrite only added it in 1.9), `timestamp`, `payload`.
- Close codes: `1003` bad message format, `1008` policy violation, `1013` slow consumer / too many messages.
- **Praxy divergence:** a subscribe arriving before auth settles is *queued*, not `1008`-closed — Appwrite's
  close-then-reconnect-then-resend produces infinite loops in their own SDK (recorded in flutter-sdk.md).
- Channel grammar: `account`, `databases.<db>.tables.<t>.rows[.<rowId>]`, `teams.<teamId>`, plus
  action-suffixed variants (`...rows.create`) subscribable directly. Server rewrites `account` →
  `account.<userId>` at subscribe.
- **API keys can subscribe** (scope-checked). Appwrite bars server credentials from realtime entirely.

### Event grammar — adopt

```
<resource>.<id>[.<subresource>.<id>].<action>[.<attribute>]
```

`*` wildcards any id segment; action ∈ `create | update | delete`. Examples:
`users.*.create`, `users.*.update.email`, `users.*.sessions.*.create`,
`databases.*.tables.*.rows.*.create`, `teams.*.memberships.*.update.status`.
One event vocabulary shared by realtime, webhooks, and function triggers.

### Webhooks — adopt the shape, fix the crypto

Appwrite delivers with `X-Appwrite-Webhook-{Id,Events,Name,User-Id,Project-Id,Signature}` headers, 15s
timeout, max 5 redirects. **Their signature is `base64(HMAC-SHA1(url + body))`** — SHA-1, in 2026. Praxy:
`X-Praxy-Webhook-Signature: v1=<hex HMAC-SHA256(timestamp + "." + body)>` with a separate
`X-Praxy-Webhook-Timestamp` header (Stripe's scheme — replay-resistant and verifiable without URL
canonicalization games).

### Error envelope — adopt + extend

```json
{"message":"...", "code":400, "type":"user_invalid_credentials",
 "version":"0.1.0", "requestId":"...", "fields":{"email":["must be a valid email"]}}
```

`type` strings are snake_case `<entity>_<error>`, stable, public API. Appwrite's registry has 289 of them —
and exactly two are accidentally UPPERCASE (`COLUMN_TYPE_NOT_SUPPORTED`), which every case-sensitive client
matcher misses. Praxy: a unit test asserts every registered type matches `^[a-z0-9_]+$`. `requestId` and
`fields` are Praxy additions (required by the SDK design, flutter-sdk.md).

Adopt the vocabulary where it fits: `user_invalid_credentials`, `user_already_exists`,
`user_session_not_found`, `team_invalid_secret`, `row_not_found`, `row_invalid_structure`,
`table_not_found`, `column_already_exists`, `index_dependency`, `general_rate_limit_exceeded`,
`general_query_invalid`, `general_unknown_origin`...

### Auth flows — adopt selectively

- Token→session exchange as the universal converging point: `POST /v1/account/sessions/token`
  `{userId, secret}` — magic URL, email OTP, OAuth token flow, and team-invite acceptance all end here.
- OAuth token-flow callback secret: Appwrite wraps it in a 60s HS256 JWT (`{secret, provider}`) so the raw
  token never rides a redirect. Adopt, combined with PKCE.
- Team invitations: identifier precedence userId > email; client-SDK calls send an invite email (`url`
  required), API-key calls add the member immediately, confirmed=false until
  `PATCH .../memberships/{id}/status {userId, secret}` — which auto-creates a session. Adopt all of it.
- JWT minting `POST /account/jwts {duration}` for server-to-server — Phase 1 optional, Phase 7 required
  (functions receive a scoped user JWT).

### Deliberately not adopted

- `/v1/tablesdb` vs `/v1/databases` dual surface, dual channel vocabulary, dual `document_*`/`row_*` error
  sets — all rename debris. One vocabulary from day one.
- `stuck` status, URL-mode realtime subscribe, SHA-1 webhook signatures, `$sequence`.
- `X-Fallback-Cookies` localStorage fallback (Praxy web SDK: cookies or explicit session header, nothing in
  localStorage).
- OpenAPI as an afterthought: Appwrite no longer commits specs and their public spec endpoint 404s; their
  generated SDKs are the only machine-readable truth. Praxy generates OpenAPI in CI from day one and treats a
  missing/broken spec as a build failure.
