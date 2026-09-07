# Session task — Generate the console's API types from the OpenAPI snapshot

## Why this exists

`console/src/api/types.ts` is 897 hand-written lines describing the API's wire shapes.
`docs/openapi/v1.json` is a committed, regenerated, CI-verified description of the same shapes.
Nothing reconciles them, and that gap has already shipped two bugs **TypeScript could not see**:

- **PR #55** — optional-vs-null modelled wrong → "Cannot read properties of undefined (reading
  'join')" on the Storage screens.
- **Organizations Phase 2** — a member's `userId` was `Ids.Wire`-encoded (32 hex) while
  `ConsoleAccount.Id` serializes as a dashed `Guid`; the console compared them to decide "is this
  me?", never matched, and the Leave button never rendered. Both are `string`, so nothing complained.

Read `docs/research/console-api-contract.md` first, then `CLAUDE.md`. Work on a new branch off `main`.

## Read this before picking a generator

**`WhenWritingNull` is the whole problem, and a generator can make it worse.** `Program.cs` sets
`DefaultIgnoreCondition = WhenWritingNull`, so a property whose value is null is **dropped from the
JSON entirely** — it arrives `undefined`, never `null`. `types.ts` models that as `foo?: T` and
deliberately never `| null`; its header comment explains why at length, because getting it wrong once
cost a release.

If the OpenAPI document describes those properties as nullable and your generator faithfully emits
`foo?: T | null`, **you will have reintroduced PR #55's exact bug with machine-generated authority.**

So: establish what `docs/openapi/v1.json` actually says about nullability *before* choosing anything.
If it says nullable, that is a finding about the document (or about how `Program.cs` describes its
DTOs), and fixing that is part of this work — not something to paper over with a post-processing
step, which would leave the document lying to every other consumer.

## Scope

1. **Generate TypeScript types from `docs/openapi/v1.json`.** From the committed snapshot, not a
   running server — the console must keep building with no database, no API, and no network, exactly
   as `npm run build` does today.
2. **Commit the generated output**, the same way the snapshot itself is committed. A clean checkout
   builds without running codegen, and a backend shape change shows up as a reviewable diff.
3. **Brand wire ids.** Shape-correctness alone would *not* have caught the Phase 2 bug — both sides
   were `string` and both were correctly `string`; the *encoding* differed. A wire id (32 hex) and a
   dashed `Guid` need to be different types so assigning one to the other fails to compile. How you
   express that is your call; that it is expressed is not optional.
4. **Wire it into the workflow**: a script to regenerate, and a CI check that fails when the committed
   output doesn't match what the snapshot produces — mirroring how `OpenApiDocumentTests` already
   guards the snapshot itself.
5. **Migrate `types.ts`.** Decide and record whether that is one replacement or staged; if staged, say
   what remains hand-written and why.
6. Consult `docs/research/dotnet-stack.md` before adding any package — it holds machine-verified pins,
   and the console's TypeScript is pinned 5.9.x.

## Non-goals

**No console test suite** — worth doing, not this. **No wire-shape changes**: this is a representation
change in the console only; if generation reveals a shape that is wrong or awkward, record it in the
report rather than fixing it. **No SDK generation** — `@praxy/core` and Flutter have their own models
and their own blast radius.

## Landmines

- **`Row`'s column values are the deliberate exception.** A null column value there is a real,
  present `null` (`JsonNode` contents bypass `WhenWritingNull`), which is why `types.ts` models them
  as `unknown`. Whatever the generator emits for `Row` needs that same treatment.
- **`ConsoleAccount.Id` is the odd one out and must stay that way.** It serializes as a plain dashed
  `Guid` while nearly every other console id is `Ids.Wire`-encoded. Organizations Phase 3's prompt
  records why changing *it* is the breaking direction. Brand both, don't unify them.
- **Generated code the compiler trusts is more dangerous than hand-written code it doesn't.** A
  wrong hand-written type gets questioned; a wrong generated one gets believed. That is why the
  verification below is about closing known bugs, not about compiling.

## Verification

Compiling is not the test. **Revert each known bug and confirm the generated types reject it:**

- Add a `foo === null` guard on an optional field → must fail `tsc` (or be provably dead, as today).
- Assign a wire-encoded id where a dashed `Guid` is expected, and vice versa → must fail `tsc`.

If either still passes, the work has produced tidier types without closing the class it exists to
close, and the report should say so plainly rather than claiming success.

## Done means

- `npm run build --prefix console` clean from a fresh checkout with no codegen run.
- Regeneration script works, and the CI check fails on a deliberately stale committed output (prove
  it by making one stale on purpose, not by assuming).
- Both verification cases above demonstrated failing to compile.
- `dotnet test` green (the snapshot test must still pass).
- `git status` clean, conventional commits, branch off `main`.
- Write `docs/handoff/console-api-contract-report.md` — including, explicitly, what the OpenAPI
  document turned out to say about nullability, since that determines whether this closed PR #55's
  class or merely moved it.
