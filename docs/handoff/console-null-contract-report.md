# Report — console null/undefined contract sweep

Branch: `chore/console-null-contract` (off `main` at `caa613e`)
Commits: `c36caef` (console), `ef3cc71` (JS SDK)

Closes the class behind PR #55's Storage crash: the API omits null DTO properties from the JSON
entirely (`DefaultIgnoreCondition = WhenWritingNull`, `src/Praxy.Api/Program.cs:378`), so a field the
console modelled `T | null` actually arrives `undefined`, and every `=== null` guard written against
one was dead code.

## The headline number

**66 of 66 fields were genuinely nullable server-side. Zero should have been plain `T`.**

`console/src/api/types.ts` carried 66 `| null` occurrences — 64 as `foo: T | null`, plus
`lastPingAt` and `allowedMimeTypes` already optional-but-still-nullable. Each was checked against its
C# record in `src/Praxy.Api/Endpoints/*.cs` (item 3 of the prompt), and every single one maps to a
nullable property: `string? Hostname`, `int? DurationMs`, `DateTimeOffset? ActivatedAt`, and so on.
None was a case of the console over-modelling a non-nullable DTO field.

That is the answer to the question the prompt wanted this number for: **the mismatch is entirely a
serializer-vs-type-declaration problem, not a "the console guessed wrong about the DTO" problem.**
Which means the server-side `WhenWritingNull` question is the *only* lever that would change any of
these 66 — see the last section.

The one exception is in the JS SDK, not the console: `AppUser.prefs` was `Record<string, unknown> |
null` against a `JsonNode? Prefs` property, but `AppUserResponse.From` routes every construction
through `ParseOrEmpty` (`AuthDtos.cs:17`), which cannot return null. It is now plain
`Record<string, unknown>`, matching what the console has always had.

## Correction to the prompt's premise: `tsc` does not catch the guard half

The prompt said retyping to `foo?: T` makes a leftover `foo === null` a compile error (TS2367), and
that "`tsc` enumerates every site that needs attention." **It does not.** TypeScript deliberately
exempts `null` and `undefined` literals from its no-overlap check, so `foo === null` is accepted
against `string | undefined` — and, verified separately, even against a non-optional `string`.
TS2367 fires for a real mismatch (`s === 5`) but never for these.

What the retyping actually buys is the *dereference* half:

| Shape | Compiler | Recorded |
| --- | --- | --- |
| `x === null ? fallback : x.join(", ")` (crash-shaped) | caught | `TS18048: 'bucket.allowedMimeTypes' is possibly 'undefined'` |
| `x !== null && other` / `x !== null ? a : b` (logic-shaped) | **silent** | no diagnostic at all |
| DTO field passed to a `string \| null` parameter | caught | `TS2345`, `TS2322` |

Both halves were re-verified after the sweep by reintroducing the two shapes: `StoragePage.tsx`'s
`=== null` produced `TS18048` at line 71; `SitesPage.tsx`'s `!== null` produced **nothing**. Both
were then restored.

So the whole retype yielded only **2** compiler errors. The logic-shaped sites were found by hand,
by grepping every one of the 66 field names across `console/src` and reading each usage. The file
header of `types.ts` now records this so the next person does not repeat the assumption.

## What was fixed, by failure mode

### Logic-shaped — silent, compiler-invisible, and two were user-visible bugs

1. **`FunctionExecutionsPage.tsx:45`** — `durationMs !== null ? \`${durationMs}ms\` : "—"`. A
   `waiting`/`processing` execution has no `DurationMs` (`Functions.cs:190`), so the key is absent,
   the guard is always true, and the Duration column rendered **`undefinedms`**. Reproduced live and
   fixed (see Owner test).
2. **`FunctionExecutionsPage.tsx:211-212`** — the detail sheet's `statusCode !== null` and
   `durationMs !== null` chips: a bare `HTTP` with no number, and `undefinedms`, for any in-flight
   execution.
3. **`UserDetailPage.tsx:290`** — `.filter((hostname): hostname is string => hostname !== null)`.
   A `flutter-android`/`flutter-ios` platform has no hostname (required only for `web`,
   `ConsoleAuthAdminEndpoints.cs:548`), so `undefined` passed the filter *through a type predicate
   that asserts `string`*. The verification hint then rendered
   `"must be a registered platform: ."` — an empty list instead of the "none registered yet" link.
   Reproduced live and fixed.
4. **`SitesPage.tsx:57`** — `activeDeploymentId !== null && isRunning`, the one the prompt named.
   Confirmed masked as described: `IsRunning` is server-side `ActiveDeploymentId is { } id && …`, so
   the second clause carried it and the rendered badge was correct either way. Fixed anyway; the
   guard itself never fired.

All four now use `== null`, each with a short comment saying why loose equality is deliberate.

### Crash-shaped

None survived. `tsc` proves it: every dereference of the 66 fields is guarded, or the build would
have reported `TS18048`. The one that shipped broken was `allowedMimeTypes`, already fixed in PR #55
and now backed by a type that would have caught it.

### Type-shaped — no behaviour change, dishonest types

5. **`ColumnsPage.tsx:52`** — `targetTableLabel(targetTableId: string | null)` (`TS2345`).
6. **`UsersPage.tsx:81`** — `lastActivityAt: string | null` prop (`TS2322`).
7. **`ProjectOverviewPage.tsx:30`** — `lastPingAt!`. The `!` was safe (a truthiness guard 15 lines
   up), but it was asserting away exactly the case the guard exists for. Replaced with a narrowed
   local. This was the only non-null assertion on any of the 66; `RowsPage.tsx:872`'s `row!` is
   unrelated.
8. **`JobStatusBadge.tsx:18`** — `error?: string | null` tightened to `error?: string`; it is fed
   only by `ColumnSchema.error` / `IndexSchema.error`.

### Audited and deliberately left alone

`SiteDeploymentsPage`/`FunctionDeploymentsPage`'s `activeDeploymentId ?? null` and `AuditLogPage`'s
`accountId ?? null` normalize at the seam into local `string | null` state — correct as written.
`RowsPage.tsx:462`'s `total ?? null` likewise. Several `title={x ?? undefined}` props are now no-ops
but remain correct for both values; left to keep the diff to the guards.

## JS SDK (`ef3cc71`)

Same 13 fields, retyped the same way; 12 optional, `prefs` plain. Confirmed zero `=== null` guards
existed, as the prompt predicted — nothing was broken, the published types were just lying.

One real consequence surfaced: `useLiveList`'s `setTotal(snapshot.data.total)` stopped compiling
(`TS2345`). Its `LiveListResult.total: number | null` is the hook's **own documented state** ("stale
after the first realtime patch"), a genuine null unrelated to the wire, so it keeps `| null` and now
normalizes with `?? null` at the seam. The existing `expect(result.current.total).toBeNull()` test
still passes.

**Gotcha for the next session:** `npm run typecheck` in `sdk/js` typechecks dependent packages
against the *built* `@praxy/core` d.ts, not its source. It reported green on a stale build and only
failed after `npm run build`. Run `npm run build` first whenever core's types change.

## Flutter SDK — confirmed immune, no changes

Verified rather than assumed. `sdk/flutter/praxy_core/lib/src/models.dart` decodes with
`json['ip'] as String?` and `json['invitedAt'] == null ? null : …`; in Dart a missing map key
evaluates to `null`, so absent and null are indistinguishable by construction. `prefs` uses
`(json['prefs'] as Map?)?.cast<...>() ?? const {}`, `tables_service.dart:52` uses
`json['total'] as int?`, and there are no force-unwraps (`json[...]!`) in any decoder. The four
non-nullable `json['total'] as int` sites all correspond to non-nullable `int Total` DTO properties.

## Owner test — actually run

Against the local dev instance (console 5173, API 5090). **Note: the API on 5090 was a stale binary
from Sep 2, predating the Storage merge — its Storage endpoints 404'd and its quota response was
missing the bucket/storage fields, which rendered as `undefined / undefined` and `NaN KB`. Restarted
with the owner's approval; that display bug was entirely the stale server, not this change.**

The mechanism was confirmed directly against the live API before testing screens: a site with no
connected repo returns **no `repositoryFullName` key** (`"hasRepoKey": false`); a fresh function
returns none of `schedule`, `nextScheduledRunAt`, `activeDeploymentId`, `repositoryFullName`,
`productionBranch`; a user who has never signed in returns a list entry whose only key is `user`.

| Screen | Field exercised | Result |
| --- | --- | --- |
| Project overview | `lastPingAt` absent | "Waiting for your first ping…"; quota card correct |
| Sites list | `activeDeploymentId` present | both sites `live` |
| Site settings | `repositoryFullName` absent | git section renders its correct branch |
| Storage list | `allowedMimeTypes` absent **and** present | `any type` / `image/png, image/jpeg` |
| Bucket settings + header | both branches | `· any type` / `accepting image/png, image/jpeg` |
| Users list | `lastActivityAt` absent | `—` |
| User detail | hostname-less platform | "none registered yet" link |
| Platforms | `hostname` absent | `—` |
| Function executions + sheet | `durationMs`, `statusCode` absent | `—`, no `HTTP` chip |

Two fixes were proven to actually change behaviour by temporarily reverting them under HMR and
watching the page:

- `UserDetailPage`: reverted → `"must be a registered platform: ."`; restored → the link returns.
- `FunctionExecutionsPage`: reverted → `DURATION: undefinedms`; restored → `—`.

The Functions rows were driven by stubbing the executions response in the page (the instance has no
built function, and building one needs a Docker image build); everything else used real API data.
Test data created for the walk (2 buckets, 1 user, 1 platform, 1 function) was deleted afterwards —
the instance is back to 2 sites and zero of everything else, as found.

## Gates

- `tsc -b --force` clean; `npm run build --prefix console` clean.
- `sdk/js`: build clean, typecheck clean (after rebuild), 107 tests passing across all four packages.
- `git status` clean; two conventional commits.

## For the owner — two things to decide later, not done here

1. **`WhenWritingNull` itself.** Not touched, per the non-goal. Worth noting what the 66/66 number
   says: none of these fields is nullable *by accident*, so removing `WhenWritingNull` would not fix
   a modelling error — it would only move where the `null` appears. The real argument for revisiting
   it is item 2 below. Still an owner decision; it changes every response shape at once.

2. **`docs/openapi/v1.json` is wrong about this class, and that is how the SDK got it wrong.**
   The snapshot lists these fields as `required` with a nullable type — e.g. `allowedMimeTypes` is in
   `BucketResponse.required` and typed `["null","array"]` — i.e. it documents "always present, may be
   null", the exact opposite of what the server does. The generator does not account for
   `DefaultIgnoreCondition`. `models.ts`'s header explicitly said nullability was "verified against
   the committed OpenAPI snapshot", which is precisely why it modelled 13 fields as `| null`. Any
   future generated client will inherit the same error. Fixing the generator (or dropping
   `WhenWritingNull` so the document becomes true) is the durable fix; both are server-side and out
   of this session's scope.
