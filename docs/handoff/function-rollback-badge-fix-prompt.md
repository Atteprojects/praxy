# Session task — fix Functions' permanently-disabled rollback button

## Why this exists

`console/src/screens/FunctionDeploymentsPage.tsx` has a real, currently-broken rollback path. Its
"active" badge (line 66) and its Activate button's gating (`canActivate`, line 166) both key off
`d.activatedAt` — but `activatedAt` means "was this deployment ever activated," not "is this the one
currently serving traffic." Once *any* deployment has been activated once, `activatedAt` stays set on
it forever, even after a later deployment supersedes it. Net effect: **a function's rollback button
disables itself permanently the first time it's ever used**, exactly the moment a real operator needs
it (recovering from a bad deploy). The "active" badge has the mirror problem — it can end up on a
deployment that isn't actually active anymore, or fail to move to the one that is.

This exact bug was already found and fixed once, in the sibling `console/src/screens/SiteDeploymentsPage.tsx`
— compare `id === activeDeploymentId` instead of trusting `activatedAt` alone. It's flagged there in
two inline comments explaining why (search that file for "activatedAt alone can't tell"). Two earlier
sessions independently found the same bug still present in `FunctionDeploymentsPage.tsx` and explicitly
deferred fixing it because it was out of their own scope — this is that fix, finally in scope on its
own. Read `SiteDeploymentsPage.tsx` in full first; this task is applying its already-proven pattern to
Functions, not designing a new one. Work on a new branch off `main`. Read `CLAUDE.md` first.

## Non-goals — do not build these

- **No backend changes.** `activeDeploymentId` is already returned on `FunctionResponse`
  (`console/src/api/types.ts:399` — same field `PraxySite` already exposes at `:504`, which is what
  `SiteDeploymentsPage.tsx` already consumes correctly). This is a console-only fix.
- **No changes to `SiteDeploymentsPage.tsx`.** It's already correct — the reference implementation to
  copy the pattern from, not a file to touch.
- **No broader redesign of the Functions deployments screen.** This is a targeted bug fix. Don't fold
  in unrelated polish (e.g. don't try to add Sites' build-duration/preview-link UI to Functions here).
- **Don't remove or repurpose `activatedAt` itself.** It still has a legitimate meaning ("was this
  deployment ever activated") and other things may reasonably read it that way later (e.g. a future
  build-duration calculation, the same way `SiteDeploymentsPage.tsx`'s own `buildSeconds` still uses
  `activatedAt` for timing, just not for "is this active"). Only the two "is this the current one"
  checks change.

## Scope

Confirmed via grep — exactly three lines in one file reference `activatedAt` today, all part of this
bug (`console/src/screens/FunctionDeploymentsPage.tsx:66,166,179`):

1. **The "active" badge cell** (line 66, inside the top-level `columns` `useMemo`): change from
   `row.original.activatedAt ? <Badge tone="mint">active</Badge> : null` to comparing
   `row.original.id === fn.data.activeDeploymentId`. Add `fn.data.activeDeploymentId` to the `useMemo`
   dependency array (currently `[]`) — mirrors `SiteDeploymentsPage.tsx`'s own `columns` `useMemo`,
   which depends on `[activeDeploymentId]` for exactly this reason.
2. **`DeploymentSheet`** (the sheet opened when a deployment row is clicked) needs a new
   `activeDeploymentId: string | null` prop, passed from `FunctionDeploymentsPage`'s render as
   `activeDeploymentId={fn.data.activeDeploymentId}` — mirror `SiteDeploymentsPage.tsx`'s
   `DeploymentSheet`, which already takes and uses this prop. Inside, compute
   `const isActive = d.id === activeDeploymentId;` and change:
   - `canActivate` (line 166) from `d.status === "ready" && !d.activatedAt` to
     `d.status === "ready" && !isActive`.
   - The button label (line 179) from `d.activatedAt ? "Active" : "Activate"` to
     `isActive ? "Active" : "Activate"`.
3. Add a short comment at each changed spot explaining why, adapted from `SiteDeploymentsPage.tsx`'s
   own ("activatedAt stays set on a deployment forever once it's first activated, even after a
   redeploy supersedes it, so activatedAt alone can't tell 'active' from 'was active once'") — don't
   copy Sites' wording verbatim where it references preview URLs or other Sites-only concepts Functions
   doesn't have.

## Tests

No backend surface changes, so no new `dotnet test` coverage — `tsc -b && vite build` must stay clean.
This repo has no console test infrastructure (no vitest/jest config exists) — the real verification is
a click-through, same precedent `docs/handoff/sites-console-polish-prompt.md` already used for a
console-only change.

## Done means

- `tsc -b && vite build` clean.
- Click-tested for real against a running instance (local dev is fine, real Docker daemon required for
  a function to actually build): create a function, upload a deployment (v1, auto-activates), upload a
  second one (v2, auto-activates and supersedes v1), open v1's deployment sheet and confirm its
  Activate button is now enabled (not permanently disabled) and clicking it successfully rolls back —
  the function actually serves v1's code afterward, not just a UI state flip. Confirm the "active"
  badge in the deployments list is on exactly one row at a time and moves correctly at each step.
- `git status` clean, conventional commit, on a new branch off `main`.
- No handoff report needed for a fix this size, per this repo's own precedent for small console-only
  fixes (`sites-console-polish-prompt.md`'s own "Done means") — unless something non-obvious turns up
  while fixing it, in which case write a short one.
