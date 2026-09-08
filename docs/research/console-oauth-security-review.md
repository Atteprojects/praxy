# Security review: the operator OAuth surface

Organizations Phase 3 (2026-09-06, `docs/handoff/organizations-phase-3-report.md`) added operator
Google sign-in — the first authentication door into the console that isn't a password. It shipped
three days before this doc, has been reviewed only by the session that wrote it and by the review
inside that phase, and is **off by default** (`Praxy:ConsoleAuth:Google:ClientId`/`ClientSecret`
unset), so no instance has exercised it in anger. `CLAUDE.md` already names a pass over it as a
candidate initiative.

It earns one ahead of the other open items for a reason that isn't seniority: **an operator session
is the whole instance.** Every project, every API key, every hosted site and function, and the
Docker socket the api container holds. The app-user OAuth flow it resembles is scoped to one
developer project; this one is not scoped to anything.

## What makes this surface different from the last security review

The three-phase review in September went after subsystems that had grown organically — containers,
the HTTP edge, project isolation — where findings came from *absence*: a check nobody had written.
This code is the opposite. It is 345 lines in `ConsoleOAuthService`, densely commented, and nearly
every decision in it is deliberate and argued in place. Reading it, the reflex is to agree.

That is the risk. The comments make load-bearing *assertions*:

- "the callback never trusts its own query string for them"
- "a second claim attempt via either door still gets `InstanceAlreadyClaimed`"
- "a wrong secret gets the exact same error and timing as a nonexistent invite"
- "the console is always same-origin with these endpoints"

Each is a property that either holds or doesn't. **The review's deliverable is those properties
tested, not read.** A phase that reports "the comments are accurate" without having tried to falsify
one of them has not done the work.

## Already verified sound — don't re-spend the budget here

Checked directly while scoping this, so the phase can start further along:

- **`CompactJwt`** (the state cookie) verifies the HMAC with `CryptographicOperations.FixedTimeEquals`
  *before* parsing the payload, ignores the token header's `alg` entirely (always HS256, so `alg:none`
  and algorithm-confusion don't apply), and rejects a passed `exp`. Sound.
- **PKCE is real**: `GoogleOAuthProvider.BuildAuthorizeUrl` sends `code_challenge_method=S256`, and
  the verifier rides the signed cookie rather than any client-visible parameter.
- **The state comparison** is a fixed-time compare of cookie-vs-query, and a missing or mismatched
  cookie fails closed to `/login`.
- **No caller-supplied redirect URL anywhere** — every success and failure path returns a fixed
  relative path built in `ConsoleOAuthService`, never a value from the request.
- **`user.Status` is checked** at password login (`ConsoleAuthService:105`) *and* again when a session
  is resolved (`:129`). So "door X forgets to check `Status`" is not a finding: a session for a
  blocked operator fails on its next request regardless. Both invite-accept doors omit the check and
  that is fine.
- **`up.sh` writes `PRAXY_TRUST_FORWARDED_HEADERS=true`** whenever a domain is configured, so the
  supported deploy gets a correct `Request.IsHttps` and a `Secure` session cookie. Confirmed against
  the live droplet's `.env`.

## Where a finding is actually plausible

Ranked by what a finding would cost, not by how likely it is.

### 1. Implicit account linking at login

`ConsoleOAuthService.LoginAsync`: when no identity matches the Google `sub`, it looks up an operator
by normalized email and — if the account exists, has a password, and the provider says the email is
verified — **links the Google identity to it and issues a session**. No existing session, no
confirmation, no notification.

That converts "controls a Google account whose verified email equals an operator's email" into full
operator access, bypassing the password entirely. For consumer signup this is ordinary. For the
admin console of a self-hosted product it is a trust-model decision that deserves to be made
explicitly rather than inherited from `OAuthService`, whose blast radius is one project.

Note the two doors disagree with each other about how much a Google email proves. The invite door
takes the *stricter* line deliberately: email must match, must be verified, **and** a Google account
already linked to a different operator is a `409`. The login door has no equivalent of that last
guard because it keys on `sub` first — which is correct — but the asymmetry in posture is worth
resolving on purpose.

The question for the phase: should linking an operator's first Google identity require an
authenticated session (link from account settings) rather than happening implicitly on a login
attempt? If the answer is "no, implicit is fine", that is a legitimate outcome — but it should be
written down as a decision with its reasoning, not left as an inherited default.

### 2. The invite secret's blast radius

The secret rides the state cookie (signed, **not encrypted** — anyone who can read the cookie can
read the secret) and, on failure, is reflected back into the redirect URL
(`/accept-invite?organizationId=…&userId=…&secret=…&oauthError=…`), which lands in the browser's
history, the `Location` header, and any proxy log in between.

Neither is obviously wrong: the invitee already holds this secret, in an emailed link with the same
shape. But "already exposed there" is a reason to check whether it needs to be exposed *again*, in
two more places, not a reason to stop looking. Worth asking whether the callback can re-derive the
invite context without carrying the secret through the round trip at all.

### 3. `CallbackUri` is built from the request

`ConsoleOAuthEndpoints.CallbackUri` interpolates `Request.Scheme` and `Request.Host`. `AllowedHosts`
is `*`, and `ForwardedHeaders` (when enabled at all) covers `XForwardedFor|XForwardedProto` —
**not** `XForwardedHost`. A `Host: evil.example` request therefore produces
`redirect_uri=https://evil.example/...` in the authorize URL.

The reason this is probably not exploitable is that Google rejects a `redirect_uri` that isn't
registered, so the flow breaks rather than leaking a code. The phase should confirm that reasoning
holds rather than assume it, and check the adjacent failure: with `TrustForwardedHeaders` **off**
behind a TLS-terminating proxy — a supported configuration for anyone not using the bundled Caddy —
`Scheme` is `http`, so the `redirect_uri` silently stops matching *and* `Secure` comes off both the
state cookie and the operator session cookie. The failure is silent in the direction that matters.

### 4. Rate limiting stops at the front door

`Start` carries `RequireRateLimiting("auth")`. `Callback` carries none. The callback is
unauthenticated, does a real outbound token exchange with Google, and writes to the database. A
forged-cookie request fails cheaply before any of that — but the phase should establish where the
expensive path actually begins and whether an attacker can reach it repeatedly.

### 5. The highest-privilege event in the product isn't audited

Only `ConsoleOAuthIntent.Claim` writes an audit entry. An operator signing in — by password or by
Google — leaves none, and the code notes this is deliberate parity with the existing password door.
Parity with an unaudited thing is still unaudited. For a product whose audit log exists to answer
"who did this", the absence of "who signed in" is a gap in the log's usefulness, and it is
cheap to close.

### 6. The console screens

`/login` and `/accept-invite` read `oauthError` off the query string, and the invite screen reads the
secret from its own URL. Worth confirming nothing reflects a query parameter into the DOM
unescaped, and that an `oauthError` value is rendered as a known-error lookup rather than as
attacker-supplied text.

## Scope

**One phase.** The surface is one service, two endpoints, and two console screens — the console/API
contract initiative was scoped as one phase and stayed one, and this is smaller. Splitting it would
mean two sessions re-deriving the same flow.

In scope: `ConsoleOAuthService`, `ConsoleOAuthEndpoints`, `ConsoleOAuthOptions`, the parts of
`ConsoleAuthService` and `OrganizationsService` those two reach into, and the console's `/login` and
`/accept-invite` screens.

Out of scope, deliberately: the app-user OAuth flow (`OAuthService`) except where comparing the two
explains a decision; TOTP and any second factor (a real feature, not a review finding); and the
Docker socket, which stays a multitenancy prerequisite rather than a console-auth one.
