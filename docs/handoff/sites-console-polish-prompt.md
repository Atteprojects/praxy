# Session task — Sites console polish (deploy flow & create-site modal)

## Why this exists

Sites Phase 1 (`docs/handoff/sites-phase-1-report.md`) shipped functional but plain: the create-site
modal is a single flat form, the deployment `Sheet`'s build log is a bare `<pre>` block, and a
successful activation just flips a badge with no moment of "this worked." The owner looked at
Appwrite's own Sites create/deploy flow (a live preview panel next to the create form, a richer
build-log viewer, a celebratory "Congratulations!" success screen with build metrics and next-step
suggestion cards) and wants Praxy's own version of that polish — **the idea, not the design**. Do not
copy Appwrite's layout, colors, copy, or domain branding (`.appwrite.network`, its pink/red accents,
confetti/glow effects), and do not add anything it has that Praxy's Sites doesn't actually do yet
(git integration, custom domains, CDN/DDoS badges — see Non-goals). This session's job is to make the
existing flow feel considered, in Praxy's own already-established dark ink/iris/mint visual language.

Read `console/src/screens/SitesPage.tsx`, `SiteDeploymentsPage.tsx`, and `SiteDetailHeader.tsx`
first — this is a redesign of those three files, not new ones. Skim `FunctionDeploymentsPage.tsx`/
`FunctionsPage.tsx` too; several existing patterns (the runtime/framework badge, the upload-and-poll
build-log `Sheet`) are worth staying consistent with rather than diverging from.

## Non-goals — do not build these

- **No new dependencies.** Everything here is achievable with the console's existing component set
  (`Modal`, `Sheet`, `Badge`, `DataGrid`, `PageHeader`) and Tailwind utilities already in use
  elsewhere in the console.
- **No git/repository UI.** Sites Phase 1 has no git integration (console tar upload only,
  deliberately) — don't add an "Add repository" affordance or anything implying one is coming.
- **No custom-domain UI.** Sites Phase 1 only has the `*.sites.<domain>` wildcard — don't add an
  "Add domain" affordance.
- **No CDN/DDoS-protection badges or any other infrastructure claim Praxy doesn't actually provide.**
  Showing "Global CDN: Connected" when there is no CDN would be a lie in the UI, not just an
  omission — the whole feature depends on Sites still describing reality accurately (see CLAUDE.md's
  cross-phase rules).
- **No backend changes.** `src/Praxy.Sites/`, `SiteEndpoints.cs`, and every API response shape stay
  untouched — this session works entirely from data the API already returns
  (`PraxySite`/`SiteDeployment` in `console/src/api/types.ts`). If a desired visual needs a field the
  API doesn't expose yet, scope it down to what's already available rather than reopening the
  backend.
- **No changes to Functions' console screens**, even though some of this session's patterns (a
  richer build-log viewer, in particular) would arguably improve `FunctionDeploymentsPage.tsx` too.
  Out of scope — Sites console screens only.

## Scope

1. **Create-site modal** (`SitesPage.tsx`'s `CreateSiteModal`): add a live side panel next to the
   form that updates as the operator types — name, the computed key, the computed public URL (the
   modal already computes this for the helper text under the Key field; surface it more prominently
   here), and a static "Next.js" framework badge in the same visual style
   `FunctionsPage.tsx`'s runtime picker already uses for `dart`/`node`. The panel should read as a
   preview of what's about to be created, not a second copy of the form.
2. **Build-log viewer** (`SiteDeploymentsPage.tsx`'s `DeploymentSheet`): replace the bare `<pre>`
   block with:
   - A search-within-log text input that filters/highlights matching lines.
   - A copy-log button (copy the full current log text to the clipboard).
   - An elapsed-time readout next to the status badge (`createdAt` → now while `building`/`queued`,
     `createdAt` → `updatedAt` once settled).
   Keep the existing 1s poll-while-`building`/`queued` behavior (`useSiteDeployment`) unchanged —
   this is a presentation change, not a data-fetching one.
3. **A real "it's live" moment.** When a deployment's sheet reflects `ready` **and** the site is
   actually active+running for that specific deployment (the same distinction
   `docs/handoff/sites-phase-1-report.md`'s "Known gaps"/owner-test section documents —
   `activeDeploymentId` matching **and** `isRunning`, not `status === "ready"` alone, which only
   means "buildable"), replace the log view with a short success state: a checkmark, build duration
   (`activatedAt - createdAt`), the deployment's `sourceSizeBytes` formatted as KB/MB, and a
   prominent "Visit site" button linking to `publicUrl` (open in a new tab). Keep this in Praxy's
   existing flat, understated visual language — no confetti, no radial glow — the goal is that the
   moment lands, not that it's loud.
4. **Contextual next-step suggestions on that same success state** — Praxy's own, not a copy of
   Appwrite's (no "Add repository"/"Add domain," since neither exists here): small link-cards for
   "Set an environment variable" (→ the site's Settings tab), "View build log" (scroll back to the
   log view within the same sheet), and "Deploy again" (closes the sheet and focuses the upload
   button). Two to three cards, not a whole grid.

## Tests

No new backend surface, so no new `dotnet test` coverage expected. `tsc -b && vite build` must stay
clean. This is a visual/interaction change with no good way to unit-test meaningfully — the real
verification is a click-through: create a site, deploy a real Next.js app (`output: "standalone"`),
watch the enhanced log view (search a term, copy the log, watch the elapsed timer move), watch it
transition into the new success state once truly live (not just `ready`), click "Visit site" and
confirm the real page loads, and click through each next-step suggestion to confirm it lands where
it says it will.

## Done means

- `tsc -b && vite build` clean.
- The owner click-tests the console (CLAUDE.md's standing rule) — the full flow above, run for real
  against a real Docker daemon, not just visually inspected.
- `git status` clean, conventional commits, on a new branch off `main`.
- No `docs/handoff/*-report.md` is required for a UI-only follow-up this size unless something
  non-obvious was found while building it (matches the judgment call
  `docs/handoff/sites-phase-1-prompt.md` itself used) — use judgment; if in doubt, write a short one.
