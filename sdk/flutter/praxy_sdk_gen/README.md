# praxy_sdk_gen

Generates `praxy_core`'s mechanical service surface from `docs/openapi/v1.json`.

Repo-internal tooling: `publish_to: none`, never a dependency of an app. Maintainers run it, CI
gates its output, and the generated files are committed like any other source.

```bash
cd sdk/flutter && dart run praxy_sdk_gen
```

## What it generates, and what it deliberately doesn't

**Generated: service methods and the models that are pure wire shape.** Today that is
`UsersService` (`/v1/users`, 13 operations) — the server-side administration surface an API key
reaches and an end-user session never should.

**Hand-written: everything with a design decision in it.** Transport, retry and error mapping;
the `Query`/`Permission` builders; `RowCodec`; session persistence; storage uploads and range
reads; realtime. The client-facing services (`account`, `tables`, `teams`, `functions`, `storage`)
stay hand-written for that reason.

This is the deliberate divergence from Appwrite, whose generator emits the client core too —
because sixteen SDKs cannot be hand-maintained. Praxy has two languages and can afford the quality
that buys. Don't "fix" this later by generating everything; see `docs/research/sdk-generation.md`.

## Adding a service

Add a `ServiceSpec` to `lib/src/services.dart` and regenerate. The important field is
`modelTypes`: the schemas `praxy_core` already models by hand, which the generator must reuse
rather than duplicate. A schema that is in neither `modelTypes` nor `generateModels` is an error,
not a guess — a generator that invents a type produces an SDK whose bugs look like the server's.

## Document gaps

The generator can only emit what the document describes, and Praxy's document does not describe
query parameters that a handler reads straight out of `HttpContext` — 37 such reads across 16
endpoint files, including `limit`, `offset`, `search` and the row query DSL's `queries`. .NET's
OpenAPI generation only sees parameters that are actually bound.

Emitting those methods silently would ship an SDK that looks complete and quietly cannot paginate.
So a known gap is declared in `documentGaps` and lands in the generated method's own doc comment,
where the person calling it will read it. A stale entry — one naming an operation that no longer
exists — is a hard error, so a note cannot outlive the gap it describes.

The real fix is to make those parameters visible to OpenAPI; until then the SDK says so out loud.

## Why there is no `--check` flag

CI regenerates into the working tree and runs `git diff --exit-code`, mirroring the console's
`check:api-types`. One code path means the check can never disagree with what a regeneration would
actually write. The CI step runs `git add --intent-to-add` first, because `git diff` does not
report untracked files and a newly registered service would otherwise pass while missing from the
repo.

## Tests

`dart test praxy_sdk_gen` covers the emitter against a miniature document — including that it
fails loudly on an unmapped schema and on a stale gap note. `praxy_core/test/users_service_test.dart`
exercises the *generated code itself* against a fake transport, which is the part a template change
can break while still looking right in a diff.
