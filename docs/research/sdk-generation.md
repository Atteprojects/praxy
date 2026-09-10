# Generating SDKs from the OpenAPI document

## Why

Praxy hand-writes two SDKs: Dart (`praxy_core`/`praxy_flutter`) and JS (`@praxy/core`, `@praxy/react`,
`@praxy/nextjs`). Appwrite ships **six client and ten server SDKs**, which is only tractable because
they are generated from a spec by `appwrite/sdk-generator`. The owner wants the same approach, with a
**Dart server SDK** as the first thing it produces.

The machinery is half-built already: `docs/openapi/v1.json` is committed and CI-verified
(`OpenApiDocumentTests`), and `console/scripts/generate-api-types.mjs` already generates TypeScript
from it for the console. This initiative points the same idea at SDKs.

## The blocker nobody has hit yet

**The document is not generator-grade.** Measured against the committed snapshot:

| | count | of 290 operations |
|---|---|---|
| `operationId` | **0** | 0% |
| `summary` | 1 | 0% |
| `description` | 1 | 0% |
| `parameters` | 206 | 71% |
| `requestBody` | 104 | 35% |

A generator needs `operationId` to name methods — `account.createEmailSession()` rather than a name
derived from `POST /v1/account/sessions/email`, which is ugly and, worse, *changes if the route
changes*. It needs `summary`/`description` to emit doc comments, without which a generated SDK has no
IDE help at all. .NET's OpenAPI generation does not invent these; they come from `.WithName()`,
`.WithSummary()` and `.WithDescription()` on each endpoint mapping, and Praxy has never called them.

So step one is not the generator. Step one is ~290 endpoint annotations, plus a ratchet test so the
291st endpoint cannot ship without them — exactly the shape of
`No_schema_anywhere_documents_a_property_as_nullable`, which already guards this document's
nullability.

**The audience partition, by contrast, already works.** Every operation is tagged with its C# endpoint
class (`AccountEndpoints`, `ConsoleDatabaseEndpoints`, `UsersServerEndpoints`), so a `Console*` prefix
separates the 107 console endpoints from the 73 data-plane ones a client SDK should see. It is a
naming convention rather than an explicit marker, and it is worth making explicit while annotating
anyway.

## What to generate, and what not to

**Generate the mechanical surface: service methods and their models. Hand-write everything with
design in it.**

This is the split the console/API contract initiative already proved: a generated
`console/src/api/generated/schema.ts` under a hand-written `types.ts` that re-exports, narrows and
brands. It shipped, it works, and its CI gate catches drift.

Hand-written, per language, because each contains real decisions a generator would flatten:

- transport, retry and error mapping to typed exceptions
- the auth model — `@praxy/core` is dual-mode (`X-Praxy-Session` or `X-Praxy-Key`); `praxy_core` is
  session-only through a `SessionStore` seam
- realtime: socket lifecycle, reconnection, ticket minting
- the query DSL and row codecs
- id handling (`Ids.Wire`, 32-hex, versus the two dashed-`Guid` properties)

**This is where we deliberately diverge from Appwrite.** Their generator emits the client core too,
because sixteen SDKs cannot be hand-maintained. We have two languages and can afford the quality that
buys — an ergonomic core with a generated surface is better than a fully generated SDK, and it is
only affordable at our size. Do not "fix" this later by generating everything.

## The Dart server SDK is smaller than it looks

`sdk/flutter/praxy_core` is **pure Dart** — its only dependency is `http`, with no Flutter anywhere.
`praxy_flutter` is the Flutter-specific layer above it. So a Dart server SDK is not a new package; it
is:

1. **API-key auth in `praxy_core`** — an `apiKey` option sending `X-Praxy-Key`, mirroring what
   `@praxy/core` already does. Today it reads a session from `SessionStore` and sends
   `x-praxy-session` only.
2. **The server-only surface** — `/v1/users` (13 operations) and the rest of the admin endpoints a
   client SDK has no business exposing. This is the part worth generating, and the reason the
   generator and the Dart server SDK belong in the same initiative rather than separate ones.

Note the packaging question `@praxy/core` already raises: one dual-mode package makes it *possible*
to construct an API-key client in a browser bundle, where Appwrite's split (`appwrite` versus
`node-appwrite`) makes it impossible. `@praxy/react` correctly has no `apiKey` anywhere.

**Decided (2026-09-10): one dual-mode package**, matching `@praxy/core` rather than splitting. With
one package the split can't be the guard, so the guard moves up a layer: `PraxyFlutter` constructs
its own core and offers no way to pass an `apiKey`, so a shipped app cannot reach for one by
accident, and the warning lives on `Praxy` itself — the one place it still could. Same shape as
`@praxy/core` being dual-mode while `@praxy/react` has none. A real split stays available later if
that proves insufficient.

## Landmines

- **`WhenWritingNull` is now a cross-language problem.** `Program.cs` drops null-valued properties
  entirely, so a property is absent, never null. The console pipeline learned this the hard way
  (PR #55), and `OpenApiWireNullability` fixes the document itself so every consumer benefits — but a
  Dart generator emitting `String?` for an *absent* property repeats the bug in a new language. Read
  `docs/research/console-api-contract.md` before writing a single template.
- **Id encoding has no schema signal.** `Ids.Wire` output (32 hex) is an ordinary `string` to JSON
  Schema; only the two genuine `Guid` properties carry `format: uuid`. TypeScript solved this with
  branded types. Dart has no structural typing — extension types (Dart 3.3+) are the closest
  equivalent, and that is a real design decision, not a detail.
- **`Row` is dynamic.** Its column values are a real, present `null` (JsonNode content bypasses
  `WhenWritingNull`), which is why the console models them as `unknown`. Any generated language needs
  the same exception.
- **Generated code the compiler trusts is more dangerous than hand-written code it doesn't.** Carried
  forward verbatim from the console initiative.

## One generator, not one per language

**Decided 2026-09-10, on the owner's call**, because a .NET server SDK was next and more languages
are wanted after it. The generator lives at `sdk/generator`: `src/spec.mjs` turns the document into
a language-neutral IR, and each `src/targets/<lang>.mjs` owns only what the code looks like and
where it goes. Adding a language is a file, not a program.

The earlier reasoning — that at two languages a shared core costs more than the ~150 duplicated
lines of parsing — was right about two languages and wrong about the destination. Above two, every
target otherwise has to rediscover this document's quirks independently, and they are exactly the
quirks that produce a *plausible but wrong* SDK: `WhenWritingNull`, the `["integer","string"]`
union, `Row`'s real nulls. One IR means one answer to each.

**Hosted in Node, deliberately, and with zero dependencies.** Node is preinstalled on every CI
runner, so each language's job runs the generator with nothing installed and formats only its own
target with the formatter that job already has. A generator hosted in any one target language would
have needed that toolchain in every other language's job — .NET inside the Flutter job, Dart inside
the API job.

## Phasing

- **Phase 1 — make the document generator-grade.** `.WithName()`/`.WithSummary()`/`.WithDescription()`
  across ~290 operations; an explicit audience marker rather than the tag-prefix convention; a ratchet
  test asserting every operation has an `operationId` and a summary. Mechanical, and it improves
  `docs/api-reference.md` for humans on its own merits, independent of any generator.
- **Phase 2 — the generator, and the Dart server SDK as its first consumer.** **Shipped 2026-09-10.**
  API-key auth in `praxy_core` (`X-Praxy-Key`, and a server client never reads the session store —
  falling back would let a background job inherit whatever session was persisted), `praxy_sdk_gen`,
  and `UsersService` as its first generated output. The packaging decision above is settled.

  **What Phase 2 found, and Phase 3 has to deal with: the document describes no query parameters at
  all.** Only path parameters survive. 37 reads across 16 endpoint files pull `limit`, `offset`,
  `search`, `expand`, `force` and the row DSL's `queries` straight out of `HttpContext`, and .NET's
  OpenAPI generation can only see parameters that are actually bound — so `users.list` is documented
  as taking nothing, and the generated method cannot paginate. Phase 1 fixed naming and did not
  touch this; the two gaps are independent. Rather than emit a method that looks complete and
  quietly can't page, the generator declares known gaps and puts them in the generated doc comment,
  and errors if such a note outlives the operation it describes. Fixing it properly means declaring
  those parameters to OpenAPI (a marker plus a transformer keeps the handlers reading from
  `HttpContext` exactly as now, so runtime behaviour doesn't move).

  Operation summaries are still 1 of 290, so generated methods carry no prose docs beyond the
  service-level one. Same cause: `.WithSummary()` was in Phase 1's scope on paper and the XML docs
  that shipped reached schemas, not operations.
- **Phase 3 — apply it to the surfaces that already exist**, incrementally and only where it removes
  hand-maintenance without costing ergonomics. Not a rewrite of the JS or Flutter SDKs.

Phase 1 is worth doing even if Phases 2 and 3 never happen, which is the test of whether it is
sequenced correctly.
