# Security review — design

## Context

Storage Phases 1–3 and the transform follow-up shipped between 2026-09-03 and 2026-09-05. The defect
record across that one sequence:

- a **stored XSS**, introduced by the *design document* (`Content-Disposition` was listed under
  ergonomics rather than security) and caught in review, not by tests;
- a production crash;
- three physical-naming 500s (`docs/…/praxy-physical-naming-budget.md`);
- two transform correctness bugs (transparent→black on JPEG; EXIF orientation ignored), both found by
  comparing against Appwrite **on a running instance**, neither reachable by a synthetic test image;
- a flaky test that raced the live functions dispatcher.

The test suite caught none of them. The XSS could not have: it was a design defect, faithfully
implemented. That is the case for a pass that reads shipped code adversarially against a stated
threat model, rather than adding behaviour and testing that the behaviour works.

This doc is that plan. Per the repo's established pattern, the session that writes it produces
**planning artifacts only** — this doc, a `docs/roadmap.md` section, and
`docs/handoff/security-review-phase-1-prompt.md`. No `src/`, `console/`, or `sdk/` file changes here.

## Why these three subsystems

Each one takes untrusted input from the network and turns it into something the host acts on:

| Subsystem | What untrusted input becomes |
|---|---|
| **Storage** | bytes served over HTTP, with a caller-chosen MIME type and filename |
| **Sites** | an attacker-authored app, reached on an attacker-influenced hostname, proxied by the API |
| **Functions** | attacker-authored **code**, built and executed against a root-equivalent Docker socket |

Tables, Auth, Realtime and Messaging are deliberately **out of scope**. They went through Phase 9's
hardening pass and are covered by `tests/Praxy.LoadTests` (`schemas|websockets|fuzz`). All three
subsystems above were built after v0.1.0 and have never had a dedicated adversarial pass.

## The threat model this reviews against

Two actors, and the distinction drives every severity call:

1. **Anonymous network caller** — anyone who can reach the instance, no credentials. Reaches the
   public API surface, every site's public hostname, and any file whose bucket grants `read("any")`.
2. **Project developer** — an authenticated operator who can create buckets, upload files, and deploy
   functions and sites in their own project. **Today this is the instance owner and people they
   trust**: `CLAUDE.md` defers multitenancy explicitly ("operator OAuth is deferred to future
   multitenancy work").

That second actor is the reason severity here is not a single number. **Every finding gets rated
under both models.** A finding that is harmless while one trusted operator runs the box, but critical
the moment a second untrusted developer shares it, is not "low" — it is a **multitenancy
prerequisite**, and must be recorded as one. That is exactly the class of defect that silently
blocks a future feature: nobody discovers it when multitenancy is designed, because it lives in code
written years earlier for a different assumption.

**Explicitly not in scope: a host-root attacker.** `deploy/docker-compose.yml` already concedes that
boundary in a long inline comment — the Docker socket mount is root-equivalent host access, there is
no sandboxed alternative in this release, and commenting it out disables Functions entirely.
Re-litigating that tradeoff is not this review's job. Mapping what *else* becomes reachable because
of it **is**.

## Verified starting findings

These were found while scoping this review, by reading the code and confirming against the live
praxycore.dev instance. They are **starting points, not the scope** — a review that only closes this
list has not been done.

### 1. Function containers share a network with the database (Phase 1)

`deploy/docker-compose.yml` names the Compose `default` network `praxy-functions`:

```yaml
networks:
  default:
    name: praxy-functions
```

`postgres` declares no `networks:` key, so it joins `default` — which *is* the network every function
container is attached to via `Praxy__Functions__DockerNetwork`. Confirmed live:

```
praxy-functions  internal=false  containers: praxy-api-1 praxy-caddy-1 praxy-postgres-1
praxy-sites      internal=false  containers: praxy-api-1 <site containers…>
```

Attacker-authored function code can therefore open TCP to `postgres:5432` directly. It still needs
the password to do anything — but the network layer, which is supposed to be the boundary that holds
when a credential leaks or a Postgres CVE lands, is not there at all.

**Sites got this right and Functions did not**, and not by design: `praxy-sites` is a genuinely
separate network with no database on it, while Functions inherited `default` purely as a side effect
of the rename. The asymmetry is the tell.

Under the trusted-operator model this is defence-in-depth. Under multitenancy it is a **direct
tenant-to-database path** and a hard prerequisite.

### 2. Neither container executor sets any isolation flag beyond memory and CPU (Phase 1)

`DockerExecutor.cs` and `SiteDockerExecutor.cs` build near-identical `HostConfig` objects setting
`Memory`, `NanoCPUs`, `AutoRemove` (and, for Sites, `RestartPolicy`). A repo-wide grep finds **zero**
occurrences of `PidsLimit`, `CapDrop`, `SecurityOpt`, `ReadonlyRootfs`, or `no-new-privileges`, and
neither executor sets `User`.

Concretely: a fork bomb in a function is unbounded by `PidsLimit`; the container keeps the default
capability set; `no-new-privileges` is unset, so setuid binaries in the image can still escalate
within the container; and whether the process runs as root depends entirely on the base image.

The two executors being near-identical is *why these belong in one phase* — see the phasing below.

### 3. Sound, and worth not re-deriving

Recorded so the review spends its time where the risk is:

- `GitHubWebhookSignature.Verify` is correct — GitHub's scheme, raw body before model binding,
  `CryptographicOperations.FixedTimeEquals`, length-checked.
- `ContentDisposition.Build` strips everything outside printable ASCII from the quoted form rather
  than escaping it, so CR/LF cannot reach the header; `filename*` carries the real name
  percent-encoded.
- `InlineTypes.ServesInline` is genuinely two gates, re-intersected at serve time so shrinking `Safe`
  takes effect immediately on already-stored bucket settings, with `text/html` and `image/svg+xml`
  permanently excluded and `nosniff` unconditional.

Phase 2's job on Storage is therefore mostly **verification of a defended edge**, not discovery. The
undiscovered surface there is the transform/derivative path, which is newer than the XSS fix.

## Phasing — by threat surface, not by subsystem

The obvious split is one phase per subsystem. That is the wrong shape here: Functions and Sites build
**near-identical `HostConfig` blocks**, so reviewing them in separate sessions means making the same
isolation decisions twice, in two contexts, with drift between them. Phase by the boundary being
tested instead.

- **Phase 1 — the container boundary** (Functions + Sites). The Docker execution seam:
  `HostConfig` hardening applied consistently to both executors, network topology (finding 1),
  what lands in a container's environment (`PRAXY_FUNCTION_API_KEY`, `PRAXY_FUNCTION_JWT`, decrypted
  user secrets), resource limits including the ones absent today, egress, and the image-build path
  (`RuntimeTemplates`/`SiteRuntimeTemplates`, `GitCliRepositoryCloner`) as a supply-chain surface.
  **Highest risk, so it goes first** — it is the only surface where untrusted *code* runs.

- **Phase 2 — the HTTP edge** (Storage + the Sites proxy). Where attacker-controlled bytes and
  hostnames meet the response: the derivative/transform path (newest storage code, post-dating the
  XSS fix), `ByteRanges` arithmetic, `SiteProxyMiddleware`'s header and host handling,
  `SiteHostPattern`'s parse and the `_ask-tls` endpoint it shares, preview-URL enumeration, and
  whether `site_requests` logging can be poisoned by a crafted request.

- **Phase 3 — authorization and project isolation** (all three). The permission model itself:
  Storage's additive bucket/per-file grants, the scope and lifetime of a function's minted
  credentials, and whether project isolation actually holds at every entry point in all three
  subsystems — including the ones that do not go through the normal API surface (the proxy, the
  webhook endpoint, the schedulers and workers).

Each phase is independently useful and ships its own fixes. Phase 1 is the one to run even if the
sequence is never finished.

## What a review phase must produce — and how it differs from a feature phase

This is the part most likely to go wrong when handed to a fresh session, because every other prompt
in `docs/handoff/` describes behaviour to build and this one cannot.

1. **The deliverable is findings, and the findings are unknown up front.** A review prompt therefore
   specifies a *threat model to work through* and an *output format*, not a scope list. Judging a
   review session by "did it implement the listed items" would reward exactly the wrong behaviour.
2. **Every finding is recorded whether or not it is fixed**, with: what an attacker does, what they
   get, severity under *both* actor models, and either the fix or the explicit reason for accepting
   it. An accepted risk that is written down is a decision; an unwritten one is a surprise later.
3. **Not everything gets fixed.** A review that tries to fix every finding turns into a rewrite and
   ships nothing. Fix what is cheap and clearly right; document-and-accept what is expensive; never
   redesign a shipped subsystem mid-review.
4. **Every fix carries a regression test.** Untested security fixes come back — and the whole reason
   this review exists is that the existing suite did not catch this class of defect.
5. **No new features.** The temptation to improve things while in the code is strong and must be
   refused; a feature idea found during review becomes a note in the report.
6. **Report format is fixed** so three reports compose into one picture: findings table, then a
   section per finding, then "accepted risks", then "multitenancy prerequisites" — that last one
   being the durable output that outlives this initiative.

## Verification

Each phase owns its own: `dotnet test` green, console build clean, and — for anything reachable over
HTTP — the finding demonstrated against a running instance before the fix and shown closed after,
the same discipline that caught the transform bugs. Findings inferred by reading alone are marked as
such and rated lower confidence than ones actually exercised; the transform sequence is the standing
proof that reading is not enough.
