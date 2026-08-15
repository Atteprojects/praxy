# Phase 0 — session prompt

Paste everything below the line into a fresh session, from the repo root.

---

You are implementing **Phase 0 of Praxy**, a self-hosted BaaS (.NET 10 API + PostgreSQL + Vite/React console
+ Flutter SDK later). The repo at `/Users/ateyibabdulkadir/Documents/projects/Praxy` contains the complete,
already-decided plan — this session is implementation, not re-planning.

Read first, in this order:
1. `docs/roadmap.md` — Phase 0 scope and the owner-test checklist you must end with
2. `docs/architecture.md` — system design, data model, threat model
3. `docs/research/dotnet-stack.md` — **verified package pins and API corrections; follow them exactly**
   (e.g. `Npgsql.DependencyInjection` for `AddNpgsqlDataSource`, rate limiter 503→429 default,
   session-level advisory lock pattern, TypeScript pinned 5.9.3, Tailwind v4 setup)
4. `docs/research/console-design.md` — console IA, screens, and the empty-state/onboarding patterns
5. `docs/research/appwrite-api.md` — headers, error envelope, wire conventions

Build Phase 0 exactly as scoped in the roadmap: solution skeleton, Docker Compose (postgres + api), EF
catalog v1 with startup migrations under an advisory lock, request-id + error-envelope middleware, OpenAPI +
Scalar (dev-only), instance claim (first account wins; setup token when `PRAXY_PUBLIC_URL` set; signup hidden
after claim), the reserved `console` project **with its guard test**, silent first-org creation, the
capabilities endpoint, and the console shell (claim/login, create-project card, project list, overview with
ping-waiting state, ⌘K palette shell) served at `/console`.

Constraints that hold: conventional commits, small and topical; never commit `.env`; integration tests use
Testcontainers with `postgres:17-alpine`; the error-envelope `type` strings are snake_case and unit-tested;
console UI is our own modern design (simple Appwrite-like layout, not their styling).

I have full permission granted for package installs and edits inside this repo. Use subagents where useful.

When done: run the Phase 0 owner-test checklist from the roadmap yourself first (docker compose up → claim →
create project → restart → state intact), then follow the **handoff protocol** at the bottom of
`docs/roadmap.md` — write `docs/handoff/phase-0-report.md` and `docs/handoff/phase-1-prompt.md`, and print
the Phase 1 prompt so I can paste it into the next session.
