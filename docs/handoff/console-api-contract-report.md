# Console/API contract — report

**Shipped 2026-09-07.** Branch `console-api-contract`. Closes the one-phase initiative
`docs/handoff/console-api-contract-prompt.md` / `docs/research/console-api-contract.md`.

## What shipped

1. **`console/src/api/generated/schema.ts`** (committed, ~14k lines, never hand-edited) — raw
   TypeScript types generated from `docs/openapi/v1.json` by `openapi-typescript@7.13.0`, via
   `console/scripts/generate-api-types.mjs`. `npm run generate:api --prefix console` regenerates it;
   `npm run build` never runs codegen, so the console still builds from a clean checkout with no
   database, no API, and no network — exactly as before.
2. **`console/src/api/ids.ts`** (hand-written) — `WireId`/`GuidId`, two nominally-branded string
   types (real strings at runtime, incompatible at the type level via a `unique symbol` brand), plus
   `wireId()`/`guidId()` cast helpers for genuine trust boundaries (route params, response headers).
3. **`console/src/api/types.ts`** — cut from 897 hand-written lines to a ~460-line re-export layer
   over the generated schema, plus a handful of documented hand-narrowings. See "Migrating
   `types.ts`" below for exactly what stayed hand-written and why.
4. **`src/Praxy.Api/Infrastructure/OpenApiWireNullability.cs`** (new backend file) — a document
   transformer that corrects the OpenAPI schema's nullability to match what `WhenWritingNull`
   actually puts on the wire. This is the fix that makes generation from the snapshot safe at all.
5. **Two small backend DTO fixes**, both zero-wire-shape-change: `OrganizationMemberResponse.UserId`
   (`string` → `Guid`) and `AppUserResponse.Prefs` (`JsonNode?` → `JsonNode`) — see "What the id
   encoding turned out to need" and "A second nullability wrinkle" below.
6. **Two backend response DTOs that didn't exist before**: `CreatedWebhookResponse` and
   `WebhookDeliveryDetailResponse` — a real bug found along the way, see "A third kind of
   document/reality mismatch" below.
7. **CI**: `npm run check:api-types --prefix console` added as a console-job step (regenerates and
   `git diff --exit-code`s the committed output), and `docs/openapi/v1.json` added to the console
   job's path filter so a backend-only PR that changes the snapshot still runs the check.

## What the OpenAPI document turned out to say about nullability

This is the question the prompt asked to answer explicitly, because it decides whether this work
closed PR #55's bug class or just moved it.

**It said the wrong thing, everywhere, and the fix needed two different shapes.** `Program.cs` sets
`DefaultIgnoreCondition = WhenWritingNull`: a null-valued property is dropped from the JSON, never
sent as `null`. But .NET 10's schema generation (`JsonSchemaExporter`) has no awareness of that
option — it derives `type`/`required` purely from the C# type and constructor shape:

- A nullable property (`string? Foo`) got `{"type":["null","string"]}`.
- **163 of 164** such properties were *also* marked `required`, because nearly every Praxy request/
  response DTO is a positional record, and record parameters are "required" to `JsonSchemaExporter`
  regardless of nullability (a missing key still satisfies a nullable constructor parameter — STJ
  passes `null`). The one non-record exception (`ErrorEnvelope.fields`, a mutable class) was already
  correctly optional but still carried the stray `| null`.
- A nullable property whose type was itself a named schema — `JsonNode?`, `JsonElement?` (5
  occurrences: `ColumnResponse.default`, `CreateColumnRequest.default`, `UpdateRowRequest.data`,
  `AppUserResponse.prefs`, and the new `WebhookDeliveryDetailResponse.payload`) — can't carry `null`
  as a sibling of a `$ref`, so the generator used `oneOf: [{"type":"null"}, {"$ref": ...}]` instead.
  Same lie, different encoding, and it needed separate handling — verified against a running
  instance after the first version of the fix (a schema transformer touching only `type`) silently
  left every one of these five untouched, because a schema transformer fires on a property's own
  node *before* its `oneOf` branches (each a schema in their own right, one a `$ref` to a named
  component) are attached. `OpenApiWireNullability` is a *document* transformer instead, run last,
  once the whole document exists — verified by regenerating and inspecting `docs/openapi/v1.json`
  directly, not assumed from reading the transformer pipeline's shape.

**If a generator had been pointed at the unfixed document, it would have reproduced PR #55 exactly.**
Confirmed directly: `openapi-typescript` against the *original* (unfixed) `docs/openapi/v1.json`
generated `password: null | string;` (required, nullable) for
`AcceptOrganizationInviteRequest.password` — the exact shape the design doc warned about, with
machine-generated authority behind it.

**The fix is in the document, not a post-processing step**, per the prompt's own instruction: a new
`IOpenApiDocumentTransformer` (`OpenApiWireNullability`) strips `null` from `type` (or unwraps the
`oneOf`) and drops the property from `required`, for every property in every schema, uniformly. No
per-schema opt-out is needed, because the one case that must stay untouched — a `Row`'s dynamic
column values, which bypass `WhenWritingNull` entirely (`JsonObject` content copied verbatim) — has
no `properties`/`required` for this to touch: `JsonObject`'s own schema is a bare `{"type":"object"}`
with no declared members. Structural difference, not a special case.

## A second nullability wrinkle: a property that's never actually null

While cataloguing the five `oneOf`-null cases, `AppUserResponse.Prefs` turned out not to belong in
that list at all. Its factory (`ParseOrEmpty`) never returns `null` — it falls back to `new
JsonObject()` on both a null and an unparseable value — so the property is unconditionally present.
The *type* said `JsonNode?` anyway, just because every other `JsonNode`-typed property in the API
happens to be genuinely nullable and this one was declared the same way out of habit. Fixed at the
source (`JsonNode?` → `JsonNode`), the same principle as the `oneOf` fix one level up: don't paper
over a wrong type with a generator-side special case, correct the C# that produced it. Confirmed
zero behavior change — `ParseOrEmpty`'s contract didn't move, only its declared nullability did.

## What the id encoding turned out to need

The prompt was explicit that shape-correctness alone doesn't catch the Organizations Phase 2 bug —
both `ConsoleAccount.Id` and `OrganizationMemberResponse.UserId` were (and are) `string`, correctly.
What was wrong was the *encoding*: one dashed, one 32-hex.

**`format: "uuid"` is a real, mechanical signal — but only two properties had it.** .NET's schema
generator emits `format: "uuid"` for a genuine C# `Guid` property. Before this session, only
`ConsoleAccount.Id` was actually typed `Guid`; `OrganizationMemberResponse.UserId` was `string`
populated with `m.Member.UserId.ToString()` — a comment at the call site explained *why* it had to
match `ConsoleAccount.Id`'s dashed format, but nothing enforced it. Changed to `Guid` (zero
wire-shape change: `Guid.ToString()` and System.Text.Json's default `Guid` converter both emit
lowercase dashed). The schema now correctly shows `format: "uuid"` on both, and the generator maps
that straight to `GuidId` — no naming heuristic involved for this half.

**Every other id in the API is a plain `string` with no format** — `Ids.Wire(...)`'s 32-hex output is
indistinguishable, in JSON Schema, from any other free-text string. There is no schema-level signal
to mechanically detect it. `console/scripts/generate-api-types.mjs` uses a naming heuristic instead
(property named `id`, or ending in `Id`/`Ids`, with no `format`) — deliberately *not* baked into the
OpenAPI document itself, unlike the nullability and numeric fixes: doing that would publish an
already-known-imperfect heuristic as canonical metadata for every consumer of the document, not just
this generator. Checked by hand against every matching property's origin in `src/Praxy.Api`, this
heuristic had **two real false positives**, each recorded with its reasoning in the script's
`WIRE_ID_EXCLUDE` set:

- `AuthSettingsResponse`/`UpdateAuthSettingsRequest.googleClientId` — a third-party Google OAuth
  client id, never `Ids.Wire`.
- `FunctionRuntimeResponse.id` — the runtime's fixed key (`"dart"`/`"node"`), built from
  `FunctionRuntimes.All` (a static in-memory list), the one other place in the API that builds a
  response via `.Select` over something other than a database entity. (The other such place,
  `IdentityResponse`, was checked and does use `Ids.Wire` — confirming this is the only exception of
  its kind, not an unchecked assumption.)

**A third was excluded first and then put back, in review** — `CreateProjectRequest.projectId`, the
operator's optional custom project id (`Ids.IsValidCustomId`: 1-36 lowercase alphanumerics and
hyphens, not `Ids.Wire`'s fixed 32 hex). Excluding it looked right in isolation and was wrong in
context: the very same value comes back out as `ProjectResponse.id`, `AuditLogEntryResponse.projectId`
and `PingResponse.projectId`, none of which the heuristic excludes — so the identical string was
`string` going in and `WireId` coming out, and `WireId`'s own doc comment ("32 lowercase hex") was
false for every custom-id project. The brand's real job is separating the wire family from a dashed
`Guid`, which a custom id belongs to just as much as a generated one, so it is branded like the rest
and the console's create-project form casts at the boundary with `wireId(...)` — the same
trust-boundary pattern route params and response headers already use. `WireId`'s definition now says
both forms explicitly. (`CreateRowRequest.rowId` looks like the same shape — also client-suppliable —
but is parsed with `Ids.TryParseWire`, confirmed by reading `RowsService.cs`, so it was never in
question.)

This list is a curated judgment call recorded in code, not a fact machine-verified from the document
— the honest cost of branding an encoding the wire format itself doesn't expose. Note the direction:
**the heuristic fails open**, branding any new `*Id`-named string automatically, so a future property
that isn't a Praxy id is wrong until a human adds it here. `ids.ts`'s header and the script's own
comment both say so.

## A third kind of document/reality mismatch, found along the way

Neither of the above — the `POST /v1/console/projects/{id}/webhooks` and
`GET .../deliveries/{id}` endpoints returned anonymous objects (`new { webhook, secret }` and
`new { delivery, payload, attempts }`) while `.Produces<WebhookResponse>()` /
`.Produces<WebhookDeliveryResponse>()` documented something else entirely — a bare delivery/webhook,
with no `secret`, no `payload`, no `attempts`. The document wasn't imprecise here, it was **wrong**:
a generator reading it would never learn `secret` exists at all. Grepped the rest of the API
(`Results.Ok(new {`/`Results.Created(..., new {` patterns) to confirm this was the only such gap —
every other anonymous-object usage found is an internal LINQ projection, never an HTTP response body.

Fixed at the source: two new named records, `CreatedWebhookResponse` and
`WebhookDeliveryDetailResponse`, replacing the anonymous objects with identical JSON output, and the
`.Produces<>()` calls updated to match. Verified live in the browser (see "Verification" below) —
created a real webhook and confirmed the reveal-once secret still renders correctly end to end.

## A disclosed generator-side simplification: numeric fields

Unrelated to nullability or ids: **every one of the 96 integer properties in the document** is typed
`["integer","string"]` with a digit-only `pattern` — .NET 10's default modelling, letting a numeric
string satisfy the schema too. Nothing in this API sets `JsonNumberHandling.AllowReadingFromString`
(checked: `Program.cs` configures no `NumberHandling` at all), so the real wire behavior is
unconditionally a bare JSON number. Left as-is in the document (not the kind of mismatch that masks
a bug — a real `number` always satisfies `number | string`, so nothing was silently broken by
trusting it, unlike nullability), but collapsed to plain `number` in the generator
(`isIntegerOrStringUnion` in `generate-api-types.mjs`), matching what `types.ts` already modelled by
hand and what every consumer actually receives.

## Migrating `types.ts`

**Mostly one replacement, with a small, permanent, disclosed hand-written residue** — not staged in
the sense of "finish this later." Of roughly 90 exported types, the large majority are now direct
aliases (`export type Project = Schemas["ProjectResponse"];`) or thin `Omit<...> & {...}` overrides.
What stays hand-written, and why:

- **`Row` and `QueryFilter`** — genuinely have no backing schema. A row's shape is dynamic (one
  property per column); `QueryFilter` is a client-side concept (the query DSL's wire format), not a
  response shape.
- **Every enum-shaped field's literal union** (`ColumnType`, `IndexType`, `FunctionRuntime`,
  `AuthTemplateKey`, every `*Status`/`*Source` type, `Organization.role`, `OrganizationMember.role`)
  — because the C# DTOs behind them declare these as bare `string`, not real C# enum types, so
  nothing in `docs/openapi/v1.json` records the closed set of values. This is a **finding, not a
  gap papered over**: `types.ts` narrows each one by hand against the server code that actually
  produces the strings, in exactly the same `Omit<Schemas[...], "field"> & {field: Union}` pattern
  used for id-branding, and says so inline everywhere it's used. A future backend phase could close
  this by switching these DTO properties to real C# enums — genuinely a wire-shape question (a
  default `enum` serializes as its ordinal number, not the string values already in use, so it would
  need a `JsonStringEnumConverter` too), which is exactly why it's recorded here rather than
  attempted in a "no wire-shape changes" phase.
- Three `*List`/`*Detail` composite shapes (`UserListEntry`/`UserList`/`UserDetail`,
  `WebhookDeliveryDetail`, `FunctionCreatedFromTemplate`, `MessageDetail`) are explicit interfaces
  over this file's own narrowed item types (`AppUser`, `WebhookDelivery`, etc.) rather than raw
  aliases of the wrapping schema — a raw alias would have re-exposed the *unnarrowed* nested type
  (e.g. `ConsoleUserRow`'s `user: AppUserResponse`, not this file's `AppUser` with `prefs` retyped to
  `Record<string, unknown>`), which is exactly the class of drift this initiative exists to prevent.
  Caught by `tsc`, not by inspection — every one of these was a real compile error until fixed.

`AppUser.prefs` is the one property redeclared purely for ergonomics, not correctness: the generator
maps the `JsonNode` reference to `unknown` (correct — it's opaque JSON), but every consumer indexes
into it by key, so it's redeclared `Record<string, unknown>` here rather than pushed back onto every
call site.

## Verification

Compiling was never the bar — both known bug classes were reverted against the *generated* types and
confirmed to fail, then restored:

- **Nullability.** `Bucket.allowedMimeTypes` (optional, no `null`) with the PR #55 pattern —
  `b.allowedMimeTypes === null ? "" : b.allowedMimeTypes.join(", ")` — compiles clean under the old,
  reverted-on-purpose modelling (`allowedMimeTypes: string[] | null`, hand-edited into the generated
  file for the test) and **fails** (`TS18048: possibly undefined`) under the real, fixed modelling.
  Confirms the type change, not just its presence, is what closes the bug.
- **Id encoding.** Assigning a `WireId` (`Project.id`) where a `GuidId` (`Account.id`) is expected,
  and vice versa, both fail `TS2322`. More specifically, reproducing the *actual* Organizations
  Phase 2 shape — `OrganizationMember.userId` hand-reverted to `WireId` (its pre-this-session type)
  compared against `Account.id` (`GuidId`) via `===` — fails `TS2367: no overlap`, and the same
  comparison against the real, fixed types (both `GuidId`, since the backend fix landed) compiles
  clean, because it's now a legitimate comparison. Both directions demonstrated, not assumed.

**A ratchet added in review.** The nullability fix had no test of its own: delete
`OpenApiWireNullability` from `Program.cs`, regenerate the snapshot, and every gate in the repo still
passed while the generated console types silently went back to `foo: T | null`. `OpenApiDocumentTests.
No_schema_anywhere_documents_a_property_as_nullable` now asserts the property directly — no schema
anywhere in the document says a value may be null — walking the whole document rather than only the
components the transformer visits, so a future endpoint whose nullable property lands in an *inline*
schema fails there too. Verified the way the rest of this work was: unregistered the transformer,
confirmed the test fails naming all eight offending properties, restored it, confirmed it passes.

Browser-verified live against the local dev instance (`owner@test.local`, Postgres pre-seeded from
earlier phases): the Columns grid (exercises `ColumnSchema.type`/`.status` narrowing and the now
correctly-optional `default`), the Organization Members screen (exercises `GuidId` — the "(you)"
label and Leave button are exactly what Organizations Phase 2's bug broke, both render correctly),
and Webhooks end-to-end (created a real webhook, confirmed the reveal-once secret renders — the
`CreatedWebhookResponse` fix's actual runtime behavior, not just its type).

## Done means — checklist

- [x] `npm run build --prefix console` clean from a fresh checkout with no codegen run.
- [x] `npm run generate:api --prefix console` works; `npm run check:api-types --prefix console`
      fails on a deliberately-staled committed output (demonstrated: appended a marker line to the
      committed file, confirmed the check fails with a real diff, reverted) and passes clean
      otherwise.
- [x] Both verification cases demonstrated failing to compile (see above), then restored.
- [x] `dotnet test` — see below.
- [x] `git status` clean, conventional commits, branch `console-api-contract` off `main`.

**`dotnet test`**: 626/626 unit tests, 364/365 integration tests (all four `OpenApiDocumentTests`,
including the snapshot-match test, pass). The one integration failure —
`StatementTimeoutTests.The_shared_connection_pool_honors_a_configured_statement_timeout` — is a
pre-existing, unrelated flake: that test sets a 1-second statement timeout on its whole connection
pool, which can also cancel its own host's *startup migration* under I/O contention from the rest of
the 31-minute, Docker-heavy suite running concurrently. Confirmed by re-running it alone
(`dotnet test --filter FullyQualifiedName~StatementTimeoutTests`): passes in 4s, no contention,
nothing to do with this session's changes (nowhere near OpenAPI generation, nullability, or ids).

## Commands (for `CLAUDE.md`)

- `npm run generate:api --prefix console` — regenerate `console/src/api/generated/schema.ts` from
  `docs/openapi/v1.json`. Needs the snapshot to already be current (regenerate it first per
  `docs/api-reference.md` if the backend changed).
- `npm run check:api-types --prefix console` — regenerates and fails if the committed output would
  change; this is the CI gate (also gated by `docs/openapi/v1.json` in the console job's path
  filter, not just `console/**`, since a backend-only PR can still make this stale).
- No new runtime configuration — this is a build-time/generator change only.

## What's next

This closes the initiative as scoped ("One phase" in `docs/roadmap.md`) — no phase 2 planned. Two
things worth a future session, neither blocking:

- **Real C# enums for status/type fields**, closing the last hand-maintained narrowing in
  `types.ts` — needs its own design pass (the `JsonStringEnumConverter` wire-shape question above).
- **Security review Phase 3** and **geo Phases 4-5** are the other open items already on
  `docs/roadmap.md`, unrelated to this initiative.
