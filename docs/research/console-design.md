# Research — Console information architecture

Source: study of `appwrite/console` v8.7.x (SvelteKit source), docs, and both issue trackers. Distilled to what
Praxy's console adopts, adapts, or deliberately beats. Praxy's console is Vite + React, so patterns transfer,
not code.

---

## Structure to adopt

### Three URL tiers

```
/account/*                          the console user themselves
/org/<orgId>/*                      projects, members, usage, settings
/project/<projectId>/*              everything else
```

Appwrite bakes the region into the project slug (`project-fra-...`); Praxy is single-region self-host, so a
plain `/project/<id>` is right. React Router equivalent via TanStack Router layout routes.

### Project navigation

Sidebar with grouped services, collapsible to icon rail, persisted:

```
Overview
── Build ──
Auth · Databases · Functions · Messaging
── Manage ──
Webhooks · Settings
```

(Appwrite buries webhooks under Settings; ours are a first-class Phase 6 feature, so they get a nav entry.)

Uniform nesting pattern everywhere:
1. **Service screen** — big cover title + tab row + table
2. **Entity screen** — breadcrumb back-link + name + copyable ID chip + its own tabs
3. **Nothing deeper.** Appwrite spent the redesign *removing* third-level pages, converting them to side
   sheets with 308 redirects for legacy deep links. Start there — details open in sheets, and deep links
   (`?row=<id>`) open the sheet on load.

Databases is the exception to tabs: a **second sidebar** listing tables (alphabetical, cap ~100, `+ Create`),
because table switching is the highest-frequency navigation in the whole console.

### The spreadsheet is the whole databases surface

Appwrite renders Rows, Columns *and* Indexes with one virtualized spreadsheet primitive: resizable/reorderable
columns, keyboard nav, a `+` action column for schema-edit-from-data-view, a bottom `+` row, multi-select with
a floating action bar (`[3] selected · Cancel · Delete`). **One primitive, three screens.** Build
`<DataGrid />` once in Phase 0–2 on TanStack Table + TanStack Virtual and reuse it everywhere.

Client-side view preferences (column order, widths, header collapsed) are per-user, keyed by org + resource,
**never on the server** — keeps the schema API clean.

### Empty states that teach

Appwrite's best pattern: an empty table renders a **ghost spreadsheet with the real column headers** and
centered action cards (*Create column*, *Create row*, docs link), not a blank panel. Filtered-to-zero gets
"no rows match your filters" + a **Clear filters** button. Copy this exactly.

### Permissions UI

One shared component set across tables, rows, functions, topics:

- **Matrix:** role rows × Create/Read/Update/Delete checkbox columns + a delete-role ✕.
  The **Create column only appears at container level** (`withCreate` prop) — rows can't grant "create".
  That prop is the visual distinction between table-level and row-level permissions.
- **Role picker:** `+ Add role` popover → Any / Guests / Users / pick users (searchable modal) / pick teams /
  label / custom string.
- Role cells resolve `user:<id>` / `team:<id>` to avatar + real name via a memoized fetch.
- Table settings carries the matrix plus a separate **Row security** switch with the two-sentence explanation
  of either/or semantics.

### Onboarding

Appwrite's flow, adapted:

1. Fresh install → `/login` → register (first account claims; **hide signup once claimed** — Appwrite leaves
   the button visible and fails at the API, their issue #2871)
2. First org auto-created silently ("Personal projects") — no org screen on the happy path
3. Chrome-less centered "Create project" card — name + optional custom ID, one button
4. Get-started checklist: ✓ create project → **connect platform** (platform cards → hostname/bundle-id form →
   copy-paste SDK snippet) → build. Advance the step on the **first real SDK/API ping** — and auto-navigate
   when the ping lands (Appwrite doesn't, and first-time users think it's broken — their #10578).

Progress circle in the sidebar until dismissed or complete.

---

## Screen inventory per phase

**Phase 0:** login/claim, org auto-create, project create, project list, project overview (ping-waiting
state), settings shell.
**Phase 1 (Auth):** users table (`ID · Name · Identifiers · Status · Labels · Joined · Last activity`), user
detail (overview / sessions / memberships / identities tabs), teams + members, auth settings (method toggles,
GitHub OAuth config, session limits, password policy), API keys.
**Phase 2 (Schema):** databases list, table sub-sidebar, Columns screen (spreadsheet), column create/edit
sheet, index create sheet, Indexes screen, table settings (permissions matrix + row-security switch + danger
zone).
**Phase 3 (Data):** row browser (spreadsheet, inline editing, filters, sort, infinite scroll), row sheet
(prev/next arrows, copy-as-JSON, permissions), bulk select + delete, CSV import/export can wait.
**Phase 4 (Realtime):** realtime inspector — live event tail with channel filter; connection count on
overview.
**Phase 6 (Webhooks):** webhook list, create (URL + event picker + signing key), delivery log with per-attempt
status and payload.
**Phase 7 (Functions):** function list, deployments + build logs, executions, settings (env vars, triggers,
schedule, timeout).
**Phase 8 (Messaging):** messages list + composer, topics + subscribers, provider config.

---

## Where Praxy's console must beat Appwrite

Ranked by community pain (from their issue trackers):

1. **Async schema status.** Appwrite shows a bare amber `processing` badge — no elapsed time, no queue
   position, no retry, no cancel, no worker health. Stuck-forever attributes are their single biggest issue
   cluster (#9048, #10021, #10032, #4828…). Praxy's synchronous DDL kills most of this class, but for the
   async remainder (index builds): show elapsed time, a cancel button, the captured error on failure, and a
   retry. `schema_jobs` already stores everything needed.
2. **Raw JSON view.** Their top-voted console issue (#1464) is "bring back View as JSON". Ship copy-as-JSON
   and a raw JSON toggle in the row sheet from day one.
3. **Permission presets.** Everyone hand-builds the same matrices (#2874). Offer one-click presets —
   *Public read*, *Owner only*, *Team access* — above the matrix, which fill it and stay editable.
4. **IDs always visible and copyable** (#1469, #1471). Every entity screen gets a copyable ID chip.
5. **Timezone-safe datetime editing.** Appwrite's console shifts datetimes by the local offset on every
   unchanged save (#2870, +9h drift per edit). Only send fields the user actually changed — their #472 —
   and keep datetimes ISO-8601 UTC end-to-end.
6. **Boolean NULL vs FALSE must look different** (#2146).
7. **Reserved-word safety.** A user column named `actions` broke their table page entirely (#2977) because it
   collided with an internal column id. Namespace internal grid columns (`__praxy_actions`).
8. **Don't strand entities.** Their project list caps at 6 regardless of page size (#7356); relationship
   pickers don't paginate past page one (#1519). Paginate everything, always.
9. **Horizontal scroll must exist** on wide tables (#2702) and mobile/tablet must not overflow (#2910 etc.).

## Ideas explicitly worth stealing

- **Capability gating from the server.** Appwrite hides UI behind server-reported capability flags
  (`supportForRelationships`…). Praxy: `GET /v1/console/capabilities` from Phase 0, so the console can ship
  ahead of or behind the API without breaking.
- **Terminology layer.** One set of views serves tablesdb/documentsdb via a vocabulary map. Praxy has one
  engine, so skip the abstraction — but keep all user-facing nouns (`table`, `column`, `row`) in one strings
  module so this stays cheap if a second engine ever appears.
- **Index creation from the schema grid.** Ticking the "Indexed" checkbox on a column opens the create-index
  sheet pre-filled with that column.
- **Right-click header menu:** update / insert-left / insert-right / duplicate / create index / sort / delete,
  with system columns filtered appropriately.
- **⌘K command palette** with `g`-prefixed navigation chords (`g a` auth, `g d` databases) and per-service
  fuzzy searchers. Cheap with cmdk; ship the shell in Phase 0 and add commands per phase.
- **Create-more switch** on the column sheet footer — on submit, reset the form and stay open. Schema
  definition is a batch activity.
- **Toast with action:** "Index is being created" carrying a *View indexes* button when created from another
  tab.

## Anti-goals

- No AI-suggestion cards in v1 (Appwrite's are cloud-only upsells; ghost-sheet empty states must not depend
  on them).
- No CSV import wizard until Phase 3 is otherwise done — Appwrite's is a whole sub-product.
- No mobile-first design — but nothing may *break* on tablet: wide content scrolls in its own container.
