# The console/API contract — design

## Context

`console/src/api/types.ts` is **897 hand-written lines** describing the API's wire shapes, maintained
by convention against a server it cannot see. `docs/openapi/v1.json` is a committed, regenerated,
**CI-verified** description of those same shapes — `OpenApiDocumentTests` fails the build when the
snapshot drifts from what the code generates.

So the contract is already written down twice: once mechanically and once by hand, with nothing
reconciling them.

## The bug class this produces, twice now

Both were shipped, both reached real screens, and **TypeScript could not see either**:

- **PR #55** — optional-vs-null modelled wrong, producing "Cannot read properties of undefined
  (reading 'join')" on the Storage screens. `types.ts`'s own header now carries a long comment
  explaining the rule (`WhenWritingNull` means an absent key, never `null`) precisely because getting
  it wrong once cost a release.
- **Organizations Phase 2** — a member's `userId` was `Ids.Wire`-encoded (32 hex) while
  `ConsoleAccount.Id` serializes as a dashed `Guid`. The console compared them to answer "is this
  me?", the comparison never matched, and the Leave button never rendered. Both sides are `string`,
  so the compiler was silent.

**51 ids in that file are typed as bare `string`.** The second bug is not a mistake anyone made
carelessly — it is invisible by construction.

## Why this is worth fixing mechanically rather than by care

The console has **zero automated tests** — no runner, no test script, no test files. Its only gate is
`tsc -b && vite build`: a typecheck over hand-written types, and a bundle. `CLAUDE.md` makes the
owner's click-test the acceptance gate deliberately, and that is a reasonable choice for *behaviour*
— but it cannot catch a shape mismatch that renders without error.

Three console defects surfaced during the Organizations initiative alone, every one found by someone
clicking. Generating the types converts the largest of those classes into a build failure.

## Design

**Generate from the committed snapshot, not from a running server.** `docs/openapi/v1.json` is
already the artifact CI trusts; making it the generator's input means the console builds without a
database, a running API, or network access — the same property `npm run build` has today. It also
means generation has a stable, reviewable input: a regenerated snapshot is a diff someone reads.

**Commit the generated output.** The console must keep building from a clean checkout with no
codegen step, and a generated file that lands in a PR is a diff that shows exactly which wire shapes
a backend change moved. This mirrors how the snapshot itself is already handled.

**Brand the ids.** Shape-correctness alone would not have caught the Phase 2 bug — both sides were
`string` and both were *correctly* `string`. What was wrong was the *encoding*. A distinct type for
a wire id (32-hex) versus a dashed `Guid` makes that assignment a compiler error. Whether the
generator can express this directly or it needs a post-processing step is the implementing session's
call; that it must be expressed somehow is not.

**Optional-vs-null is the landmine, not a detail.** `Program.cs` sets
`DefaultIgnoreCondition = WhenWritingNull`, so a null-valued property is *absent*, never `null`.
`types.ts` models this as `foo?: T` and never `| null`, and its header explains why at length. If the
OpenAPI document describes those properties as nullable and the generator faithfully emits
`foo?: T | null`, the generated types would **reintroduce exactly the bug PR #55 fixed** — with more
authority, because they look machine-verified. Establish what the document actually says about
nullability before choosing a generator, and treat "the generated types match the hand-written ones
on this axis" as an acceptance criterion, not an assumption.

**Row data is the deliberate exception.** A null column value in `Row` is a real, present `null`,
because `JsonNode` contents bypass `WhenWritingNull`. `types.ts` models column values as `unknown`
for that reason. Whatever the generator produces here needs the same treatment.

## Non-goals

- **A console test suite.** Worth its own discussion; this initiative is about making a class of bug
  impossible rather than about detecting bugs generally. Bundling them would make both slower.
- **Changing any wire shape.** This is a representation change in the console only. If generation
  reveals a shape that is wrong or awkward, record it — do not fix it here.
- **Generating the SDKs.** `@praxy/core` and the Flutter SDK have their own hand-written models and
  their own reasons; whether the same approach helps them is a separate question with a separate
  blast radius.

## Verification

The honest test is not "it compiles." It is: **revert each of the two known bugs and confirm the
generated types reject them.** Reintroduce a `| null` guard on an optional field, and assign a
wire-encoded id where a dashed one is expected — both should fail `tsc`. If either still passes, the
generation has produced tidier types without closing the class it exists to close.
