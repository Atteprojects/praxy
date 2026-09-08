# Session task — Security review: the operator OAuth surface

## Why this exists

Organizations Phase 3 added operator Google sign-in: the first non-password door into the console.
An operator session is the entire instance — every project, every API key, every hosted site and
function, and the Docker socket the api container holds. The app-user OAuth flow it resembles is
scoped to one developer project; this one is scoped to nothing.

It shipped 2026-09-06, is off by default, and no instance has run it in anger.

Read `docs/research/console-oauth-security-review.md` first — it records what has **already been
verified sound**, so you don't re-spend the budget there — then `CLAUDE.md`. Work on a new branch
off `main`.

## The one thing to understand before reading code

**This code is careful, densely commented, and mostly right. That is the hazard.**

The last security review found things by absence — a check nobody wrote. Here every decision is
deliberate and argued in place, and the comments make load-bearing assertions:

- "the callback never trusts its own query string for them"
- "a second claim attempt via either door still gets `InstanceAlreadyClaimed`"
- "a wrong secret gets the exact same error and timing as a nonexistent invite"
- "the console is always same-origin with these endpoints"

Each is a property that holds or doesn't. **Your job is to falsify them, not to read them.** A report
saying "the comments are accurate" without a single attempted falsification has not done the work.
Prefer a test or a live request over an argument: this initiative's own history is that reading
produced two wrong conclusions (see below) that ten seconds of checking corrected.

## Scope

`ConsoleOAuthService`, `ConsoleOAuthEndpoints`, `ConsoleOAuthOptions`, the parts of
`ConsoleAuthService` / `OrganizationsService` they reach into, and the console's `/login` and
`/accept-invite` screens. The research doc ranks six areas where a finding is plausible — start
there, but the list is a starting point, not a checklist to tick off.

**Fix what you find, in the same phase.** The owner's standing instruction on the last security
review was "I think we should fix all of them, i dont want any gaps I might forget about later." An
accepted risk needs to be argued in the report, not left implicit.

## Rate each finding under two actors

Carry this forward from the last review, because it is what stopped "low" from hiding real problems:

- **An anonymous caller** who has never authenticated.
- **A second operator** — a `member` of one organization, invited legitimately, who should reach
  nothing outside it.

Anything harmless with one trusted operator but serious with a second untrusted one is a
**multitenancy prerequisite**, not a "low". Say so in those words.

## Landmines

- **Don't report an absent `Status` check.** `ConsoleAuthService` checks `user.Status` at password
  login *and* again when resolving a session, so a session minted for a blocked operator dies on its
  next request. Both invite-accept doors omit the check and that is correct. This was checked while
  scoping; it looked like a finding and isn't.
- **`up.sh` does set `PRAXY_TRUST_FORWARDED_HEADERS=true`** when a domain is configured — confirmed
  against the live droplet. The interesting case is the *unsupported* one: someone behind their own
  nginx/Traefik/cloud LB who never sets it. Then `Request.IsHttps` is false, `Secure` silently comes
  off the operator session cookie, and `redirect_uri` becomes `http://…` and stops matching. Check
  that failure, not the bundled-Caddy one.
- **`CompactJwt` is sound** — HMAC verified with `FixedTimeEquals` before parsing, header `alg`
  ignored, `exp` enforced. Algorithm confusion and `alg:none` do not apply. Don't spend time there.
- **PKCE is real** (`code_challenge_method=S256`), and the verifier never leaves the signed cookie.
- **A fix can introduce the finding.** Five findings across the last review were introduced or
  widened by fixes made in the same review, two of them by the reviewer. Re-read your own diff as
  hostile input before you write the report.

## Two corrections made while scoping, as calibration

Both were confident readings that were wrong, and both took under a minute to check:

- "The operator password door doesn't check `Status`" — it does, `ConsoleAuthService:105`. A grep for
  the wrong error type produced the false negative.
- "Production's session cookie can't be `Secure` behind Caddy" — `PRAXY_TRUST_FORWARDED_HEADERS=true`
  is in the droplet's `.env` and `up.sh` writes it.

The lesson is not "be less suspicious." It is that a suspicion is a question, and the question is
usually cheap to answer. Answer it before it reaches the report.

## Verification

The surface is off by default, so **turn it on and drive it**. A review of an OAuth flow that never
completed one is a code read, and should be reported as one.

- Register a Google OAuth client against a local dev instance (`docs/self-host.md`'s Git-integration
  section is the model for how these setup steps get documented) and complete all three doors: claim
  on an unclaimed instance, ordinary login, and invite-accept.
- Then attack them. Reuse a state cookie. Replay a `code`. Complete a callback with a mismatched
  `state`. Point an invite accept at a *different* invite's `userId`. Sign in with a Google account
  whose verified email matches an existing operator and observe exactly what you get.
- For anything you cannot exercise live, say so explicitly in the report rather than implying you
  did.

## Done means

- Every finding fixed, or its acceptance argued in the report under both actors.
- New or changed behavior covered by tests; a fix without a test that fails without it is not done.
- `dotnet test` green — note that the integration suite takes **~25 minutes** and rebuilding while a
  background run is in flight invalidates it.
- `npm run build --prefix console` clean, and `npm run check:api-types --prefix console` still passing
  if any DTO changed.
- `git status` clean, conventional commits, branch off `main`.
- `docs/handoff/console-oauth-security-review-report.md` written, and `CLAUDE.md`'s Commands section
  updated if any knob or setup step changed.
- The report states plainly which of the six research-doc areas produced findings, which produced
  none, and — for each of those — **what you did to try to make it produce one**.
