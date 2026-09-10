# sdk/generator

One generator, many languages. Reads `docs/openapi/v1.json` and emits the mechanical service
surface into each SDK.

```bash
node sdk/generator/bin/generate.mjs                # every target
node sdk/generator/bin/generate.mjs --target dart  # one target
node --test sdk/generator/test/*.test.mjs          # its own tests
```

Repo-internal tooling — never published, never a dependency of an SDK.

## Targets

| target | emits into |
|---|---|
| `dart` | `sdk/flutter/praxy_core/lib/src/services/generated/` |
| `csharp` | `sdk/dotnet/Praxy.Sdk/Services/Generated/` |

## Why one generator rather than one per language

Because adding a language should be a file, not a program. `src/spec.mjs` turns the document into a
language-neutral IR and is the only part that knows OpenAPI — including this document's specific
quirks, which every target would otherwise have to rediscover. A target under `src/targets/` owns
exactly two things: what the code looks like, and where it goes.

That trade only pays above two languages, which is why this replaced a Dart-only generator when a
.NET SDK entered the picture rather than before.

## Zero dependencies, no build step

Deliberate, and it is what makes the CI story work. Node is preinstalled on every runner, so each
language's own job runs the generator with nothing installed and formats only its own target, using
the formatter that job already has. A generator hosted in any one target language would have needed
that toolchain installed in every other language's job.

Tests use Node's built-in runner for the same reason. Types come from JSDoc plus `// @ts-check`.

## Adding a language

1. `src/targets/<lang>.mjs`, exporting `{ name, outputPath(service), render(service, operations, document), formatCommand? }`.
2. Register it in `src/targets/index.mjs`.
3. Add a `targets.<lang>` block to each service in `src/services.mjs`.
4. Add the regenerate-and-diff gate to that language's CI job.

`formatCommand` is optional and returns `[command, args, cwd]`. If the command is missing the
generator warns and carries on, so someone without Dart installed can still regenerate the C#.

## Adding a service

An entry in `src/services.mjs`. The load-bearing field is `modelTypes`: the schemas an SDK already
hand-writes, which the generator must **reuse** rather than duplicate. A schema in neither
`modelTypes` nor `generateModels` is a hard error, not a guess — a generator that invents a type
produces an SDK whose bugs look like the server's.

## What is deliberately not generated

Transport, retry, error mapping, auth, the query DSL, row codecs, session persistence, realtime.
Those carry real decisions a generator would flatten, so every SDK hand-writes them. Appwrite
generates the client core too, because sixteen SDKs cannot be hand-maintained; we have few enough to
afford the quality that buys. See `docs/research/sdk-generation.md`.

## Landmines, which are shared across every target

- **`WhenWritingNull`.** The API omits null-valued properties, so a property is *absent*, never
  `null`. A codec that reads "present but null" is wrong in every language. This already shipped as
  a bug once (PR #55, console). C# needed extra care: `JsonIgnoreCondition.WhenWritingNull` covers a
  type's *properties*, not the dictionary entries a generated request body is built from.
- **`["integer", "string"]`.** .NET types every `int32` as that union though the wire only ever
  carries a number. `openapi-typescript` resolves it to `number` for the console; `src/spec.mjs`
  resolves it the same way, so there is one answer about this document rather than one per
  generator.
- **`Row` values are real nulls.** `JsonNode` content bypasses `WhenWritingNull`, so it is the one
  place `null` genuinely appears on the wire.
