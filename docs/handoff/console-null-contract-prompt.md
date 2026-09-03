# Session task — close the console's null/undefined contract mismatch

## Why this exists

**This already caused a production crash.** Storage Phase 1 shipped a Storage screen that threw
`Cannot read properties of undefined (reading 'join')` for any bucket created with the "allowed
types" field left blank — the default path. Fixed in PR #55; this session closes the *class*, because
the same shape is latent across the rest of the console.

The mechanism, verified rather than assumed:

`src/Praxy.Api/Program.cs` configures minimal-API response serialization with

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
```

so **a DTO property whose value is null is omitted from the JSON entirely** — it does not arrive as
`null`. Confirmed against the live production API: a bucket with no mime allow-list came back with no
`allowedMimeTypes` key at all (`"fieldPresentInJson": false`).

`console/src/api/types.ts` models **65 fields** as `foo: T | null`. That type is wrong in *both*
directions: it promises a `null` the wire never sends, and rules out the `undefined` it always sends.
Every `=== null` guard written against those fields is therefore dead code that silently does nothing.

Work on a new branch off `main`. Read `CLAUDE.md` first.

## The three cases — do not treat them the same

This is the part to get right before touching anything:

1. **DTO record properties** (`SiteResponse`, `BucketResponse`, `UserResponse`, …) — serialized by
   the configured minimal-API options, so **null ⇒ omitted**. These are the 65 fields, and they are
   this session's whole scope.
2. **Dynamic row JSON** — `RowsService.BuildRowJson` returns a `JsonObject`, and `JsonNode` contents
   are written verbatim by the node converter, *not* subject to `DefaultIgnoreCondition`. A null
   column value therefore arrives as an explicit `null`. Proven by an existing passing test:
   `GeoEngineTests` asserts `row.GetProperty("location").ValueKind == JsonValueKind.Null` —
   `GetProperty` would throw if the key were absent. **Leave row typing alone.** `Row` already models
   column values with an index signature (`[columnKey: string]: unknown`), which is correct, and
   `$distance?` is already correctly optional.
3. **Function response bodies** — opaque strings containing whatever a user's function returned,
   parsed separately. Not Praxy DTOs. Irrelevant here; don't be misled by
   `FunctionScheduledCredentialsTests` asserting a `null` inside one.

## Scope

1. **Retype the DTO-derived nullable fields in `console/src/api/types.ts`** from `foo: T | null` to
   **`foo?: T`** — optional, and *without* `| null`, because null genuinely never appears on the wire
   for these.

   **This is what makes the task self-verifying.** With `foo?: T`, a leftover `foo === null` becomes a
   *compile error* (TS2367, "This comparison appears unintentional because the types have no
   overlap"), and `foo.bar` becomes TS18048 ("possibly undefined"). So after retyping, `tsc` enumerates
   every site that needs attention. Work the error list; don't hunt by eye.

2. **Fix each site the compiler surfaces.** Two distinct failure modes, and they need different
   treatment:
   - **Crash-shaped**: a `=== null` guard gating a method call or property access
     (`x === null ? … : x.join(", ")`). Use `== null`, which catches both. This is what crashed
     Storage.
   - **Logic-shaped**: `x !== null && somethingElse` — with an omitted field this is *always true*, so
     the condition silently stops meaning what it says. There is at least one already:
     `SitesPage.tsx`'s `isLive = site.activeDeploymentId !== null && site.isRunning`. It is currently
     **masked** — `IsRunning` is server-side `ActiveDeploymentId is { } id && registry.TryGet(...)`, so
     the second clause saves it — but the guard is wrong and one refactor away from being a real bug.
     Fix it properly rather than leaving it because it happens not to bite.
3. **Check, don't assume, which fields are genuinely nullable server-side.** A field typed `| null` in
   the console isn't automatically nullable in the DTO. Read the corresponding record in
   `src/Praxy.Api/Endpoints/*.cs`: if the property is non-nullable there, the console type should just
   be `T` with no optionality at all, and that is a better fix than making it optional.
4. **`sdk/js/packages/core/src/models.ts` has the same dishonest modeling** — 13 `| null` fields
   against the same API. There are currently **zero** `=== null` guards in the JS SDK, so nothing is
   broken today, but the types mislead anyone writing against them. Retype them the same way. Keep it
   a separate commit so it can be reverted independently if it churns the SDK's public types more than
   expected.

## Non-goals — do not do these

- **Do not remove `WhenWritingNull` server-side.** It looks like the tidier one-line fix and it is
  not: it changes the shape of *every* API response at once, which touches both SDKs, the committed
  `docs/openapi/v1.json` snapshot, and any client that currently relies on absence. If you come to
  believe it is the right long-term answer, say so in the report and leave it for an owner decision —
  don't do it as a side effect of this cleanup.
- **Do not retype row/column values.** See case 2 above. Row nulls are real nulls.
- **Do not "fix" the Flutter SDK.** Dart's `json['x'] as String?` treats an absent key and a null value
  identically, so it is structurally immune to this whole class. Confirm that before dismissing it, but
  expect no changes.
- **No behavior changes beyond the guards.** This is a correctness/typing sweep, not a redesign of any
  screen.

## Landmines

- **`== null` vs `=== null` will look like a lint violation to anyone skimming.** It is deliberate:
  loose equality is exactly the "null or undefined" check wanted here. Leave a short comment at any
  site where the intent isn't obvious from context, as PR #55 did in `StoragePage.tsx`.
- **A field being optional in TypeScript does not mean the server omits it** — and vice versa. The
  compiler cannot check this for you; item 3 is a read of the C# DTO, not a guess.
- **Optional chaining is not always the right fix.** `x?.join(", ")` silently yields `undefined` where
  the screen wanted the "any type" fallback text. Prefer an explicit `== null ? fallback : …` where a
  fallback is what the UI actually needs.
- **Watch for `!` non-null assertions** on these fields — they will now be asserting away `undefined`
  rather than `null`, and each one is a crash waiting for the omitted case.

## Tests

The console has no test suite, so `tsc` is the gate and the sweep is designed around that:

- `tsc -b` clean is necessary but not sufficient — it only proves you silenced the errors, not that
  you silenced them correctly. For each fix, state in the report which of the two failure modes it was.
- **Prove the retyping has teeth** the way PR #55 did: after the sweep, temporarily reintroduce one
  `=== null` guard and confirm `tsc` rejects it. Record the error code in the report.
- `npm run build --prefix console` clean.
- If you touch `sdk/js`, its own `typecheck`/`test` must stay green.

## Done means

- `tsc -b && vite build` clean for the console; `sdk/js` typecheck/test green if touched.
- **Owner test, actually run**: the screens whose types changed still render — in particular any screen
  where a field went from "rendered a null branch" to "rendered an undefined branch". At minimum walk
  Sites (the `activeDeploymentId` fix), Storage, and one auth screen.
- `git status` clean, conventional commits, on a new branch off `main`.
- Write `docs/handoff/console-null-contract-report.md`, including a count of how many of the 65 were
  genuinely nullable versus how many should never have been `| null` at all — that number is the
  interesting finding, and it tells the owner whether the server-side `WhenWritingNull` question is
  worth revisiting later.
