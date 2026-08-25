# Research — .NET 10 stack, verified

Verified against SDK `10.0.100` / runtime `10.0.0` on this machine, plus NuGet and npm registry checks.
.NET 11 is preview only — **.NET 10 is the target**.

---

## Pinned versions

| Package | Version | Notes |
|---|---|---|
| `Npgsql` | 10.0.3 | |
| `Npgsql.DependencyInjection` | 10.0.3 | **separate package — `AddNpgsqlDataSource` lives here** |
| `Npgsql.OpenTelemetry` | 10.0.3 | |
| `Microsoft.EntityFrameworkCore` | 10.0.11 | |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.11 | pin explicitly, see skew below |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.3 | transitively pulls EF Core **10.0.4** |
| `EFCore.NamingConventions` | 10.0.1 | snake_case |
| `Konscious.Security.Cryptography.Argon2` | 1.3.1 | see decision below |
| `Microsoft.AspNetCore.OpenApi` | 10.0.11 | pulls `Microsoft.OpenApi` 2.7.5 |
| `Scalar.AspNetCore` | 2.16.20 | OpenAPI UI — none ships in the box |
| `Testcontainers.PostgreSql` | 4.14.0 | |
| `Docker.DotNet.Enhanced` | 4.3.3 | Phase 7 (Functions) and Sites Phase 1 |
| `Yarp.ReverseProxy` | 2.3.0 | Sites Phase 1 only — see below |
| `Serilog.AspNetCore` | 10.0.0 | |
| `OpenTelemetry.Extensions.Hosting` | 1.17.0 | instrumentation packages same version |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.17.0 | |

WebSockets, `System.Threading.Channels`, and rate limiting need **no package** — all in the shared framework.

### Console

| Package | Version |
|---|---|
| `vite` | 8.2.1 |
| `@vitejs/plugin-react` | 6.0.5 |
| `react` / `react-dom` | 19.2.8 |
| `@tanstack/react-router` | 1.170.29 |
| `@tanstack/react-query` | 5.101.4 |
| `tailwindcss` / `@tailwindcss/vite` | 4.3.3 |
| `typescript` | **5.9.3 — pin, do not take `latest`** |

npm `latest` for TypeScript is 7.0.2, the native-Go compiler port, with exactly one stable release and an
immature lint/tooling tail. Pin 5.9.3.

---

## Corrections against common assumptions

1. **`AddNpgsqlDataSource` requires `Npgsql.DependencyInjection`.** Without it you get a bare
   `CS1061: 'IServiceCollection' does not contain a definition for 'AddNpgsqlDataSource'`.
2. **`NpgsqlCommandBuilder.QuoteIdentifier` is an *instance* method**, not static.
3. **`RateLimiterOptions.RejectionStatusCode` defaults to `503`, not `429`.** Set it explicitly.
4. **`OpenTelemetry.Exporter.Otlp` does not exist.** The package is
   `OpenTelemetry.Exporter.OpenTelemetryProtocol`.
5. **`Docker.DotNet` has been stale since May 2023** with no deprecation notice.
   `Docker.DotNet.Enhanced` is the Testcontainers-maintained fork and is what Testcontainers itself runs on.
6. **Swashbuckle is not dead** (10.2.3, June 2026) — but `Microsoft.AspNetCore.OpenApi` is the right default,
   and it ships **no UI**. Default output is OpenAPI **3.1**.
7. **Npgsql's EF provider pulls EF Core 10.0.4** while the current patch is 10.0.11. Pin `.Design` so tooling
   and runtime agree.
8. **Pooling is configured through the connection string, not `NpgsqlDataSourceBuilder`** — there are no
   pooling methods on the builder. Keys: `Pooling`, `MinPoolSize`, `MaxPoolSize` (default 100),
   `ConnectionIdleLifetime`, `ConnectionPruningInterval`, `Multiplexing`.
9. **Direct `new NpgsqlConnection(...)` has been discouraged since Npgsql 7.** Always go through the
   singleton, thread-safe `NpgsqlDataSource`.
10. **`ForwardedHeadersOptions.KnownNetworks` is obsolete in .NET 10** (`ASPDEPR005`, a hard build
    error under this repo's `TreatWarningsAsErrors`) — use `KnownIPNetworks` (`System.Net.IPNetwork`-based)
    instead. `KnownProxies` is unaffected.

---

## Identifier quoting — confirmed safe, but not sufficient

```csharp
static string Q(string ident) => new NpgsqlCommandBuilder().QuoteIdentifier(ident);
```

Observed behaviour — embedded quotes are doubled:

```
users                   ->  "users"
MixedCase               ->  "MixedCase"
weird"name              ->  "weird""name"
tab";DROP TABLE x;--    ->  "tab"";DROP TABLE x;--"
```

**Parameters can never bind identifiers — only values.** So quoting makes the SQL syntactically safe but still
lets a caller create arbitrarily-named objects. Praxy's generated-physical-name scheme plus a strict regex
allowlist is the actual control; `QuoteIdentifier` is the second layer.

---

## Startup migration under an advisory lock

Use **session-level `pg_advisory_lock`**, not `pg_advisory_xact_lock` — EF migrations span multiple
transactions, so a transaction-scoped lock releases too early. Hold it on one dedicated connection for the
whole migration.

```csharp
await using var conn = await dataSource.OpenConnectionAsync(ct);
await using (var c = new NpgsqlCommand("SELECT pg_advisory_lock($1)", conn))
{ c.Parameters.AddWithValue(MigrationLockKey); await c.ExecuteNonQueryAsync(ct); }
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
    if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
        await db.Database.MigrateAsync(ct);
}
finally { /* pg_advisory_unlock */ }
```

Losing instances block, then find zero pending migrations. Note this serializes *startup* only — it does not
make a destructive migration safe. Keep migrations additive.

Share one data source between Npgsql and EF — the `UseNpgsql(DbDataSource)` overload exists:

```csharp
builder.Services.AddDbContext<PraxyDb>((sp, o) => o
    .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
    .UseSnakeCaseNamingConvention());
```

---

## UUIDv7 — validated

`Guid.CreateVersion7()` and `Guid.CreateVersion7(DateTimeOffset)` both exist and are static.

Sorting, tested over 20,000 timestamps spanning ~10 years and confirmed to cross the signed-16-bit boundary:

| Ordering | Chronological? |
|---|---|
| `OrderBy(g => g)` — default `Guid.CompareTo` | ✅ |
| Ordinal sort of `g.ToString()` | ✅ |
| `g.ToByteArray(bigEndian: true)` | ✅ — this is what Postgres does |
| `g.ToByteArray()` (little-endian default) | ❌ |

The old "never sort GUIDs in .NET" folklore applies to `ToByteArray()`'s little-endian layout, not to modern
`CompareTo`, which compares fields unsigned. **Postgres `uuid` compares bytewise, matching UUIDv7's big-endian
layout** — so time-ordered, index-friendly primary keys with no custom comparer and no extension.

One privacy consequence to document: the embedded millisecond timestamp makes row creation time inferable from
any exposed id.

---

## Argon2 — the weakest link, decided

No pure-managed option is both actively released and dependency-free.

| Package | Version | Last release | Repo | Notes |
|---|---|---|---|---|
| `Konscious...Argon2` | 1.3.1 | 2024-06 | 2024-06 | pure managed, most used, raw bytes only |
| `Isopoh.Cryptography.Argon2` | 2.0.0 | 2023-08 | 2026-08 | repo active, **NuGet 3 years stale** |
| `Geralt` | 4.4.0 | 2026-07 | 2026-07 | libsodium, misuse-resistant, active |
| `NSec.Cryptography` | 26.4.0 | 2026-04 | 2026-04 | libsodium binding |

**Decision: Konscious**, behind an `IPasswordHasher` interface so swapping to Geralt is a one-file change. Its
low commit rate reflects a finished primitive, not neglect, and zero native dependencies is worth a lot for a
self-hosted product that must run anywhere.

Measured on this machine (Release build, 32-byte output, `MemorySize` in KiB):

```
m=19456 (19 MiB), t=2, p=1   ->  131 ms     <- OWASP baseline, adopt
m=47MiB,          t=1, p=1   ->  145 ms
m=64MiB,          t=3, p=1   ->  282 ms
m=32MiB,          t=3, p=2   ->   65 ms
```

Memory cost is **per concurrent hash** — 19 MiB × 50 concurrent logins ≈ 1 GB. Budget it against login QPS.

Konscious returns raw bytes only, so persist a PHC string `$argon2id$v=19$m=,t=,p=$salt$hash` yourself to allow
verification and re-hash-on-upgrade. Verify with `CryptographicOperations.FixedTimeEquals`.

⚠️ ASP.NET Core Identity's `PasswordHasher<T>` is PBKDF2. We are not using Identity.

---

## WebSocket + bounded channel shape

```csharp
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
{ KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(20) });

var outbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(256)
{ FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
```

Two things that matter: **exactly one writer task per socket** — `SendAsync` is not safe for concurrent callers,
and the single-reader pump enforces that — and a **bounded** channel so one slow client applies local
backpressure instead of growing an unbounded queue.

`KeepAliveTimeout` (new in .NET 9) gives ping/pong dead-peer detection.

---

## Rate limiting

```csharp
options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;   // NOT the default

options.AddPolicy("per-tenant", ctx => RateLimitPartition.GetTokenBucketLimiter(
    partitionKey: ctx.User.FindFirst("tenant")?.Value
                  ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
    factory: _ => new TokenBucketRateLimiterOptions { … }));

app.UseRouting();
app.UseRateLimiter();     // MUST follow UseRouting for per-endpoint policies
```

Partitioning on client IP is DoS-able by source-address spoofing — prefer an authenticated project/API-key claim
as the partition key. Limiters are **per-process**, so with multiple instances the effective limit multiplies by
instance count; a true global cap needs a distributed store.

Emit `Retry-After` from `OnRejected` via `MetadataName.RetryAfter` — the Flutter SDK's
`PraxyRateLimitException.retryAfter` depends on it.

---

## Tailwind v4 — breaking changes that compile silently

Items 1–3 are loud. **5, 6 and 7 change appearance without erroring** and are the dangerous ones.

1. Browser floor: Safari 16.4+, Chrome 111+, Firefox 128+
2. `@tailwind base/components/utilities` → `@import "tailwindcss"`
3. No `tailwind.config.js` auto-detection — config moves into CSS via `@theme`
4. PostCSS plugin split to `@tailwindcss/postcss`; the Vite plugin replaces PostCSS entirely
5. **Renamed scale utilities, old names still valid with new meanings:** `shadow-sm`→`shadow-xs`,
   `shadow`→`shadow-sm`, `rounded-sm`→`rounded-xs`, `outline-none`→`outline-hidden`, `ring`→`ring-3`
6. **Default border colour** `gray-200` → `currentColor`
7. **Default ring** 3px/`blue-500` → 1px/`currentColor`
8. Removed `bg-opacity-*` etc. (use `bg-black/50`), `flex-shrink-*`→`shrink-*`
9. Variant stacking now left-to-right: `first:*:pt-0` → `*:first:pt-0`
10. Important modifier is a suffix: `!flex` → `flex!`
11. Arbitrary CSS vars: `bg-[--brand]` → `bg-(--brand)`
12. Custom utilities: `@layer utilities` → `@utility`
13. **Sass/Less/Stylus are incompatible with v4**

```ts
export default defineConfig({ plugins: [react(), tailwindcss()] });
```

```css
@import "tailwindcss";
@theme { --color-brand-500: oklch(0.62 0.19 260); }
```

---

## Webhook delivery: SSRF guard and secret storage (Phase 6)

No resilience/retry package exists on NuGet worth pulling in for this: `Microsoft.Extensions.Http.Resilience`
wraps Polly and is built for *outbound calls this process makes to services it trusts*, with jittered retry
as the main feature — it has no SSRF-guard concept, and the full-jitter backoff formula
(`uniform(0, min(cap, base·2^attempt))`) research/flutter-sdk.md already specified for the realtime
reconnect is three lines to hand-roll (`Praxy.Webhooks.WebhookBackoff`) versus a new pinned dependency
for one call site. Skipped.

**SSRF guard — connect-time, not URL-validation-time.** A webhook URL is arbitrary and owner-supplied
per project; the naive guard (parse the URL, resolve DNS, reject private ranges, *then* let
`HttpClient` connect) has a TOCTOU hole — DNS can resolve differently a second later (rebinding), and
the validating code path is never the code path that actually opens the socket. The fix confirmed
against the current BCL: `SocketsHttpHandler.ConnectCallback` (`Func<SocketsHttpConnectionContext,
CancellationToken, ValueTask<Stream>>`, available since .NET 6) lets you replace the handler's own
connect step entirely. `Praxy.Webhooks.SsrfGuard.ConnectAsync` resolves `Dns.GetHostAddressesAsync`
itself, filters the *resolved addresses* (not the hostname) against private/loopback/link-local/
multicast ranges, and opens the `Socket` directly — so the address that gets validated is the address
that gets connected to, no gap in between. `AllowAutoRedirect = false` on the same handler is the
entire "no redirects followed cross-origin" requirement — simpler and strictly safer than allowing
same-origin redirects selectively (a redirect target needs the same guard applied again, and disabling
redirects entirely sidesteps that). No package needed — `SocketsHttpHandler`, `Dns`, and `Socket` are
all in the `System.Net`/`System.Net.Http` shared-framework namespaces already implicit-used here.

**Webhook signing secrets are stored in plaintext, not hashed.** Every other secret in this codebase
(session tokens, API keys, verification/recovery tokens) is hashed at rest because the server only
ever needs to *compare* a presented value against the stored hash. A webhook secret is structurally
different: the server is the one computing `HMAC-SHA256(secret, timestamp + "." + body)` on every
outbound delivery, forever — a one-way hash of the secret cannot be used to compute a new HMAC, so the
raw value has to stay retrievable. This is the same shape Stripe, GitHub, and every other webhook
provider uses (the signing secret lives in their DB in a form the delivery worker can read back).
Architecture.md §10's "OAuth provider tokens encrypted with a project key" describes a *reversible*
symmetric-encryption layer for exactly this kind of secret, but that layer doesn't exist yet anywhere
in the codebase (`Microsoft.AspNetCore.DataProtection` is available via the Web SDK's shared framework
but nothing currently calls `AddDataProtection()`/`IDataProtector`, and OAuth provider tokens
themselves aren't persisted past the token exchange today — there's nothing to check the pattern
against). Building that layer for one column, mid-phase, is more surface than this phase asked for;
Phase 6 stores `WebhookSubscription.Secret` in plaintext (reveal-once at the API response layer, same
as API keys, just not hash-only at rest) and flags this as the natural target once a project-key
encryption layer exists for OAuth tokens too — one mechanism, two consumers, not two bespoke ones.

**Correction (Phase 7): that project-key encryption layer already existed — this section's claim
that it didn't was wrong.** `Praxy.Auth.InstanceKey` (built in Phase 1 to sign the short-lived OAuth
JWTs) also carries an AES-256-GCM `Encrypt`/`Decrypt` pair keyed by `PRAXY_SECRET_KEY`, and
`OAuthService.ResolveUserAsync` was *already* calling `key.Encrypt(token.AccessToken)` into
`Identity.AccessTokenEnc` before this paragraph was written — a grep miss, not a design gap. Phase 7
needed exactly this ("env vars encrypted at rest" for `FunctionEnvVar.ProtectedValue`) and found it
already live, so it reuses `InstanceKey` rather than standing up `Microsoft.AspNetCore.DataProtection`
as this section originally proposed. One mechanism, now two consumers (OAuth provider tokens,
function env vars) — the outcome this paragraph predicted, just via the layer that was already there.
Lesson for future phases: grep for the actual call sites (`key.Encrypt`/`key.Decrypt`) before
concluding a mechanism doesn't exist, the way this section should have the first time.

## Docker executor and the open-runtimes contract (Phase 7)

**`Docker.DotNet.Enhanced` 4.3.3 — confirmed still current**, and its API shape differs from the
historical `Docker.DotNet` idiom worth knowing before writing code from memory: there is no
`DockerClientConfiguration` class in this version. Construction is a builder:
`new DockerClientBuilder().WithEndpoint(new Uri("unix:///var/run/docker.sock")).Build()`. Verified via
reflection against the installed `net10.0` package assets (`Docker.DotNet.Enhanced` ships a `net10.0`
target), not the package's own docs, which are thin.

**`IImageOperations.BuildImageFromDockerfileAsync`'s two overloads are not interchangeable, and the
"correct-looking" one has a real bug.** The `IProgress<JSONMessage>`-callback overload is the one the
library itself documents as "waits for the build to complete" — but it was observed to hang
indefinitely (not honoring the passed `CancellationToken`) on some failed builds against this Docker
Engine version (28.1.1), intermittently — reproduced, then fixed, not theorized. The fix: use the
`Task<Stream> BuildImageFromDockerfileAsync(Stream, ImageBuildParameters, CancellationToken)` overload
instead (marked `[Obsolete]` with the message "does not wait for build to complete" — true only for
callers who don't read the returned stream) and parse the newline-delimited JSON yourself with a plain
`StreamReader.ReadLineAsync(ct)` loop, watching for `{"stream":...}`/`{"status":...}`/`{"error":...}`
keys per line. That `ReadLineAsync` call is the only thing that can block, and it actually respects
cancellation — `Praxy.Functions.DockerExecutor.BuildImageAsync` is the implementation, with a scoped
`#pragma warning disable CS0618` around the one call site (this project's `TreatWarningsAsErrors` would
otherwise refuse the intentional obsolete-overload use).

**Correction (post-Phase-7 bugfix): Phase 7's original design — publish the function container's port
to the Docker host's `127.0.0.1` and connect there — only works when `api` itself runs bare on the
host.** It was never actually exercised against the real self-host deployment (`api` running inside
`deploy/docker-compose.yml`), where `api`'s own `127.0.0.1` is a different network namespace than the
host's — every Functions invocation there timed out. Fixed by joining function containers to `api`'s
own Docker network instead (`Praxy:Functions:DockerNetwork`, empty by default to keep the original
host-port-publish behavior for dev mode) and connecting by container IP on the runtime's own port
(3000), never the host's network stack. The `CreateContainerParameters.NetworkingConfig` /
`HostConfig.NetworkMode` shape needed for this was verified the same way as the rest of this section —
by reflecting against the installed `Docker.DotNet.Enhanced` 4.3.3 `net10.0` assets, not written from
memory: `NetworkingConfig.EndpointsConfig` is `IDictionary<string, EndpointSettings>` keyed by network
name, and `HostConfig.NetworkMode` must also be set to that same network name — setting only
`EndpointsConfig` without a matching `NetworkMode` is a documented Docker Engine API footgun that
silently leaves the container on the default bridge network instead (this mirrors what `docker run
--network=<name>` itself sets under the hood). After `InspectContainerAsync`, the container's address
on that network is `ContainerInspectResponse.NetworkSettings.Networks[name].IPAddress` — that
dictionary only exposes `IDictionary<TKey,TValue>.TryGetValue`, not the `GetValueOrDefault` extension
(that extension targets `IReadOnlyDictionary<TKey,TValue>`, which `IDictionary<TKey,TValue>` doesn't
implement here).

**`System.Formats.Tar` (BCL, .NET 7+, no package) is a real tar reader/writer** —
`TarWriter`/`TarReader`/`PaxTarEntry` — used both to read an uploaded deployment's tar and to write the
combined build-context tar (uploaded files + generated Dockerfile + generated runtime wrapper) that
gets streamed into `BuildImageFromDockerfileAsync`. One real gotcha found by testing an actual macOS
upload end to end, not by reasoning about it: **macOS's `bsdtar` embeds a PAX extended attribute
(`com.apple.provenance`) that the Linux side of the Docker daemon's context extraction rejects
outright** (`lsetxattr ... operation not supported`, surfacing as a build failure before the
Dockerfile's first instruction ever runs). Forwarding the `TarEntry` objects `TarReader` returns
straight into the output `TarWriter` propagates this. The fix: re-emit a fresh minimal
`PaxTarEntry(entry.EntryType, entry.Name) { Mode = entry.Mode, DataStream = entry.DataStream }` per
file instead, dropping every attribute the entry doesn't strictly need — robust against this and
whatever the next platform-specific tar quirk turns out to be, since Praxy's own build context never
depends on anything beyond name/mode/contents.

**`Cronos` 0.13.0 — confirmed current and actively maintained** (HangfireIO, the same team behind
Hangfire's own scheduler), chosen over hand-rolling cron math for `FunctionScheduler`'s next-occurrence
calculation. UTC-only usage here (`CronExpression.Parse(schedule).GetNextOccurrence(DateTimeOffset.UtcNow,
TimeZoneInfo.Utc)`) sidesteps the DST-transition ambiguity the package's own docs warn about for local
`DateTime` — deliberate, not an oversight.

**The open-runtimes wire contract is adopted only where it's actually public.** The executor↔runtime
contract this phase's roadmap line references has two genuinely documented parts — the
`x-open-runtimes-secret` header (must match the container's `OPEN_RUNTIMES_SECRET` env var) and the
build-phase/start-phase split — both adopted verbatim (`Praxy.Functions.RuntimeTemplates.SecretHeader`,
the constant literal `"x-open-runtimes-secret"`). Everything else open-runtimes' own reference
implementation does internally (its exact request/response envelope, its per-language SDK surface) is
not publicly specified anywhere reachable in this research pass, and replicating it faithfully would
be a project of its own — so Praxy's runtime wrapper (`RuntimeTemplates.DartWrapper`/`NodeWrapper`)
defines its own minimal envelope (`{method, path, body, headers}` in, `{statusCode, body, headers,
logs, errors}` out) and its own per-language function signature (Dart: `Future<Map<String, dynamic>>
handler(Map<String, dynamic> context)`; Node: `module.exports = async (context) => ({statusCode, body,
headers})`). Documented here so the divergence is a decision on record, not something the next phase
discovers by diffing against upstream.

**Correction (post-Phase-7 bugfix): the Dart entrypoint function was originally specified as `main`,
not `handler` — that was unworkable, not a style choice.** Dart's compiler enforces a hard rule
project-wide, not just for the file actually passed to `dart run`: **any top-level function named
`main`, anywhere in the compiled program — including one only ever reached via a static `import ... as
alias`, never executed as the process entry point — must accept `List<String>` (or no) arguments.**
`RuntimeTemplates.DartWrapper` imports the user's file and calls `user_fn.main(envelope)`; with the
user file defining `Future<Map<String, dynamic>> main(Map<String, dynamic> context)` per the original
contract, this fails outright at container start (`dart run` never gets past its own front-end check):
`Error: The type 'Map<String, dynamic>' of the first parameter of the 'main' method is not a supertype
of 'List<String>'`. This is not a Praxy bug in the sense of "wrong code" — it's a contract that Dart
itself cannot satisfy, discovered by running the exact wrapper+user-file pair through `dart run` in
isolation, not by reasoning about it. **No Dart function ever ran successfully under the original
contract, in any environment, for any user code.** Fixed by renaming the required export to `handler`
— an ordinary top-level function name carries none of `main`'s special entry-point rules — while
leaving the entrypoint *file* free to be named anything (still `main.dart` by convention; only the
*function* inside it changed).

## Yarp.ReverseProxy and Docker.DotNet.Enhanced extensions (Sites Phase 1)

**`Yarp.ReverseProxy` 2.3.0 — confirmed current** (checked against the NuGet flatcontainer index and
a web search; nothing newer exists as of this research, despite the package's own highest explicit
`TargetFramework` group in its nuspec being `net8.0` with a `frameworkReference` to
`Microsoft.AspNetCore.App` — no `net9.0`/`net10.0` group exists yet). This is not a compatibility
problem: NuGet resolves a `net8.0`-targeted library into a `net10.0` app fine (`Microsoft.AspNetCore.App`
framework references are forward-compatible within the same major-version-family convention), and it
was verified empirically, not just asserted — `dotnet build` against this repo's actual `net10.0`
`Praxy.Sites` project succeeds with the package referenced, zero warnings under this repo's
`TreatWarningsAsErrors`.

**Use `IHttpForwarder` direct forwarding, not the route/cluster config model.** YARP's usual mode
(`AddReverseProxy().LoadFromConfig(...)`) is for a static or slowly-changing set of routes/clusters;
Sites needs to resolve a destination *per request* from a live DB + in-memory registry lookup
(`<site key>.<projectId>` → active deployment's container address), which is exactly what the
lower-level `IHttpForwarder` adapter is for (Microsoft's own docs distinguish this as "Direct
Forwarding" — dynamic destination selection, the caller supplies the `HttpMessageInvoker`, no
routing/load-balancing/retries built in, since the caller does its own). Registration is
`builder.Services.AddHttpForwarder()`; the call site is
`await forwarder.SendAsync(httpContext, destinationPrefix, httpMessageInvoker, forwarderRequestConfig, transformer?)`,
returning a `ForwarderError` (`ForwarderError.None` on success; on failure, `SendAsync` itself has
already converted the error to a response — `ctx.GetForwarderErrorFeature()` is for logging the
underlying exception, not for producing the response yourself). Always construct the
`HttpMessageInvoker` from a `SocketsHttpHandler`, never a plain `HttpClient` — `HttpClient` buffers
responses by default, which breaks the streaming (SSR, RSC) this feature exists to carry.

**`Docker.DotNet.Enhanced` 4.3.3's `ImageBuildParameters.BuildArgs`** (`IDictionary<string, string>`)
is the real channel for Docker `--build-arg` values — confirmed via reflection against the installed
package (same discipline Phase 7's own research used), not assumed. This is how Sites gets env vars
into `npm run build` without ever writing a secret value into generated Dockerfile text: the
Dockerfile only contains `ARG <key>` / `ENV <key>=$<key>` (key names only, already validated to
`[A-Za-z0-9_]+`), and the actual values travel through `BuildArgs`, which Docker's own build API
handles — no shell, no interpolation, no injection surface.

**`HostConfig.RestartPolicy` (`RestartPolicyKind`: `Undefined`/`No`/`Always`/`OnFailure`/
`UnlessStopped`)** — confirmed to exist via reflection; unused by Functions (whose containers are
warm-pool-managed, not meant to persist) but load-bearing for Sites, where a site's active
deployment's container is meant to run continuously and be restarted by Docker itself on crash. Set
`Name = RestartPolicyKind.UnlessStopped` in `HostConfig`, same struct Functions already sets
`NetworkMode`/`EndpointsConfig` on for its own dual-networking mode (reused verbatim for Sites' own
`SiteDockerExecutor` — see the Functions Docker section above for the full explanation of why both
must be set together).

**A Next.js `.next/standalone` output does not always carry its own `public/` or `.next/static`
directories** — if the app has no `public/` folder at all (legitimate; it's optional), the official
"copy `.next/standalone`, then separately copy `.next/static` and `public`" multi-stage pattern
(exactly what `praxy-sites.md`'s own template specifies) fails the runner stage's `COPY
--from=builder .../public ./public` with an opaque "not found" error — reproduced directly against a
real minimal app with no `public/` directory, not theorized. Fixed with a defensive
`RUN mkdir -p public .next/static` at the end of the builder stage, after `npm run build` — cheap,
idempotent if the directories already exist, and turns an app-shape edge case into a no-op instead of
a build failure with no actionable message (the same "don't let this become an opaque Docker error"
discipline the missing-`output: "standalone"` check already follows).

## Caddy on-demand TLS — current directive syntax (Sites Phase 1)

Verified against Caddy's own current docs (`caddyserver.com/docs/caddyfile/options`,
`.../caddyfile/directives/tls`, and `caddyserver.com/on-demand-tls`'s own worked example for exactly
this "many dynamically-created subdomains" shape), not recalled from training data, per this file's
own standing discipline.

On-demand TLS needs **two pieces working together**, not one:

1. A **global options block** (must be the first thing in the Caddyfile if present) naming the ask
   endpoint:
   ```caddyfile
   {
       on_demand_tls {
           ask http://api:8080/v1/sites/_ask-tls
       }
   }
   ```
   Caddy appends `?domain=<hostname>` itself — the config only names the base URL. **`interval` and
   `burst`** (older rate-limiting sub-directives some examples still show) **are explicitly
   documented as no longer recommended** and should not be added.
2. A **per-site `tls { on_demand }`** directive on the site block that should actually use on-demand
   issuance:
   ```caddyfile
   *.*.{$PRAXY_SITES_DOMAIN} {
       tls { on_demand }
       reverse_proxy api:8080
   }
   ```
   Without the global `ask` option configured, Caddy's own docs call enabling `tls { on_demand }` in
   production **insecure** (anyone who can complete a TLS handshake could make Caddy mint them a
   cert) — the two pieces are not independently sufficient, only together. Every other site block in
   `deploy/Caddyfile` (the main `PRAXY_DOMAIN`/`PRAXY_CONSOLE_DOMAIN` block) is unaffected — it keeps
   ordinary automatic HTTPS, since only a block with `tls { on_demand }` opts in.

A wildcard-shaped site address (`*.*.example.com`) under on-demand TLS does **not** provision one
real wildcard certificate (that would need DNS-01 and a DNS provider's API credentials, the whole
thing on-demand TLS exists to avoid) — Caddy matches the pattern for routing purposes, then issues a
**separate certificate per exact hostname** the first time each one is actually requested, exactly as
`docs/research/praxy-sites.md` specified.

**Correction (post-Phase-1 bugfix, found live on the deployed instance): Caddy's automation-policy
subject matching is exactly as strict about wildcard *depth* as a real TLS wildcard certificate is —
`*.{$PRAXY_SITES_DOMAIN}` (single label) does not match a two-label prefix, and Sites' actual
hostname pattern (`<key>.<projectId>.{Praxy:Sites:Domain}`) has *two* variable labels, not one.** The
first draft of this Caddyfile block (and this doc's own example above, now fixed) used a single
`*.{$PRAXY_SITES_DOMAIN}`, which validated fine with `caddy validate` (a syntax check, not a
runtime-semantics one) and was never caught by any integration test, because none of them exercise
real Caddy — `SitesAskTlsTests` calls `/v1/sites/_ask-tls` directly over HTTP, and `SiteTests`' proxy
assertions go through the WebApplicationFactory's in-memory transport with the `Host` header set
directly, bypassing Caddy's own SNI/TLS-policy matching entirely. The failure mode this produces is
worth knowing precisely, because none of it points at the actual cause on its own: the browser sees
`ERR_SSL_PROTOCOL_ERROR` ("sent an invalid response"); `curl -v` shows the TLS handshake failing
immediately after ClientHello with `tlsv1 alert internal error` (Go's `crypto/tls` sends exactly this
alert when a `GetCertificate` SNI callback can't produce a cert); and Caddy's own logs, even at INFO
level, show **nothing at all** — no ask-endpoint call, no ACME attempt, because the policy-match
failure happens before any of that logic runs. It only became visible with `debug` temporarily added
to the Caddyfile's global options block, which logs the exact rejection:
`"msg":"no certificate matching TLS ClientHello", ..., "on_demand":false` — that trailing
`"on_demand":false` is the tell; it means the automation-policy subject match failed, not that the
ask endpoint said no (a real ask-endpoint denial looks different and does call out to `api`, visible
in `api`'s own logs). Fixed by widening the site block's address to `*.*.{$PRAXY_SITES_DOMAIN}`,
verified against real Caddy end to end afterward: a genuine two-label hostname
(`todos.<projectId>.sites.praxycore.dev`) got a real Let's Encrypt certificate on first request and
served the actual page. **Lesson for future phases:** `caddy validate` proves the config parses; it
does not prove the automation policies actually match the hostnames the application generates —
that needs a real request against a real deployed Caddy instance, which is exactly the gap here.

## Caddy on-demand TLS — 3-label preview hostnames (Sites Phase 2)

Sites Phase 2 adds a per-deployment preview URL, a third leading label in front of the existing
2-label production pattern: `<deploymentId>.<key>.<projectId>.{PRAXY_SITES_DOMAIN}`. Given the Phase 1
postmortem above, this session verified the wildcard-depth question against real Caddy **before**
touching `deploy/Caddyfile`, rather than assuming one more `*.` label would obviously be correct.

**Method** (no internet/ACME needed — the failure mode lives entirely in local automation-policy
matching, before any network call): ran `caddy:2` in a throwaway Docker container with `debug`
logging on, `on_demand_tls { ask http://host.docker.internal:9999/ask }` pointed at a local Python
HTTP server that logs every hit and always denies (404) — enough to prove whether the automation
policy matched at all, without needing a real ask-endpoint allow or a real Let's Encrypt round trip.

- **Negative control**, reproducing Phase 1's exact bug one label short: a Caddyfile with only
  `*.*.{$PRAXY_SITES_DOMAIN}` (2 wildcard labels, the current production block) received a TLS
  handshake for a genuine 3-label hostname (`abc123deploy.mykey.myproj.sites.praxytest.local`). The
  ask server's log stayed empty — never called — and Caddy's debug log showed exactly Phase 1's
  signature: `"msg":"no certificate matching TLS ClientHello", ..., "on_demand":false`.
- **Fix, verified**: adding a second Caddyfile site block, `*.*.*.{$PRAXY_SITES_DOMAIN}` (3 wildcard
  labels), alongside the existing 2-label one. The same 3-label hostname now correctly triggered
  `"msg":"asking for permission for on-demand certificate"`, called the ask endpoint
  (`http://host.docker.internal:9999/ask?domain=abc123deploy.mykey.myproj.sites.praxytest.local`,
  visible in both Caddy's log and the ask server's own), and — since the test server always denies —
  cleanly failed with `"on-demand certificate issuance denied" ... non-2xx status code 404`, the
  correct behavior for a real deny, not a silent policy mismatch. The existing 2-label hostname was
  re-tested against the same combined config and still matched its own block correctly (the two
  blocks don't shadow each other — Caddy picks the most specific matching subject).

**DNS: no new record needed.** Unlike the TLS automation-policy match (exactly one label per `*.`,
confirmed above), a DNS wildcard record matches *any* number of labels below its owner name, not just
one, as long as no more specific record intercepts it first (RFC 4592) — `*.sites.<domain>` (Phase 1's
existing record) already resolves both `<key>.<projectId>.sites.<domain>` (2 extra labels) and
`<deploymentId>.<key>.<projectId>.sites.<domain>` (3 extra labels) to the same IP with zero
configuration change. This asymmetry between DNS wildcards (any depth) and TLS wildcard matching
(exactly one label) is exactly why Caddy needs a dedicated automation-policy block per hostname depth
even though DNS itself never needed one — consistent with Phase 1's own fix having been Caddyfile-only
despite going from a 1-label to a 2-label pattern.

**Full ACME issuance against a real public domain was not re-verified in this session** (no internet-
reachable test domain in this environment) — the local verification above proves the piece Phase 1's
postmortem showed is the actual risk (automation-policy subject matching), the same scope Phase 1's
own pre-deploy session verified before its later live-deploy correction. Confirm the first real preview
URL gets an actual Let's Encrypt cert the same way Phase 1's live fix was confirmed, the first time this
ships to a real domain.

## Caddy on-demand TLS — catch-all site address for custom domains (Sites Phase 3)

Custom domains have no fixed suffix or wildcard depth to match against — the whole point is arbitrary,
unpredictable hostnames (`myapp.com`, `www.myapp.com`, ...). Verified Caddy's own documented answer for
exactly this shape against real Caddy docs first, not memory: `caddyserver.com/docs/caddyfile/concepts`
says "to catch all hosts, omit the host portion of the address, for example, simply `https://`," and
`caddyserver.com/on-demand-tls`'s own worked example for "many dynamically-created subdomains" uses
precisely that — a bare `https://` site block with `tls { on_demand }` inside, no address restriction:

```caddyfile
https:// {
	tls {
		on_demand
	}
	reverse_proxy api:8080
}
```

`:443` (a port with no host) is the equivalent alternative some other examples use; `https://` was
chosen to match Caddy's own canonical on-demand-TLS documentation page as closely as possible.

**The real risk with a catch-all block isn't syntax, it's shadowing** — does a host-unrestricted
`tls { on_demand }` block accidentally take over automation/routing for hostnames that already match a
more specific block (the two existing sites-wildcard blocks, or the console/API's own explicit-hostname
block)? Real prior art shows this class of bug is genuine: [Caddy issue
#5933](https://github.com/caddyserver/caddy/issues/5933) reports a catch-all site's certificate being
served for requests to other sites under certain configs. Caddy's docs also state the general rule that
should prevent it: "If a request matches multiple site blocks, the site block with the most specific
matching address is chosen" — but per this file's own standing discipline, a documented rule isn't
enough on its own; it needs a real request against a real deployed Caddy instance actually loading all
four site blocks together, the same bar Phase 1 and Phase 2's own fixes were held to.

**Verified live**, in two passes: first with a throwaway Caddy instance running a synthetic Caddyfile
(all four block shapes, `debug` logging on, `on_demand_tls { ask http://host.docker.internal:9999/ask }`
pointed at a local Python stub that logs every call and always denies), then again loading the *actual*
`deploy/Caddyfile` verbatim (env vars supplied, only the ask URL swapped to the local stub, byte-identical
site blocks otherwise) with real TLS requests (`curl -k --resolve <host>:<port>:127.0.0.1`) against both:

- A genuine 2-label site hostname (`blog.myproj.sites.praxycore.test`) triggered the ask endpoint with
  exactly that domain — the two-label wildcard block's own automation policy matched, not the catch-all's.
- A genuine 3-label preview hostname (`dep123.blog.myproj.sites.praxycore.test`) likewise matched its
  own three-label wildcard block.
- A legitimate-shaped but unregistered custom domain (`mycustomdomain.example.test`) and a made-up
  attacker-controlled hostname (`totallyrandom.attacker.test`) both correctly fell through to the
  catch-all block's ask call — proving it does activate for arbitrary hostnames, with `_ask-tls` (not
  Caddy) responsible for actually denying the attacker one.
- The console/API's own domain (`console.praxycore.test`, regular automatic HTTPS, no `tls { on_demand
  }`) **never triggered an ask call at all** in either pass — its explicit-hostname automation policy
  took precedence over the catch-all with no shadowing observed, confirming issue #5933's failure mode
  does not reproduce here (that report involved a catch-all with an explicit static certificate, not a
  pure on-demand policy with no subjects, and no other block in this Caddyfile carries an explicit cert
  either).
- The synthetic-Caddyfile pass's debug log directly confirms how Caddy compiles this into automation
  policies — `apps.tls.automation.policies` at runtime (not `caddy adapt`'s static, pre-provision JSON,
  which does not yet reflect this) showed exactly three entries: the console's explicit-subjects policy,
  one `on_demand:true` policy scoped to `["*.*.*.{domain}","*.*.{domain}"]` (Caddy merged the two
  wildcard blocks' identical `tls { on_demand }` config into one policy, harmlessly), and a bare
  `{"on_demand":true}` fallback policy with no subjects — exactly the "most specific wins, catch-all
  falls back" structure the docs describe.

No real Let's Encrypt round trip was attempted (same reasoning as Phase 2's own note above — no
internet-reachable test domain in this environment); this verification proves the automation-policy
matching/shadowing risk, which is the piece that fails silently and was the actual root cause both
prior times this Caddyfile broke in production. Confirm a real custom domain gets an actual cert the
same way Phase 1's and Phase 2's live fixes were eventually confirmed, the first time this ships.

**Postscript, found live 2026-08-25 (after Phase 4 shipped, root-caused all the way back to this
section)**: the no-real-ACME caveat above turned out to hide a genuine issuer-selection bug, not just
an untested code path. Once this Caddyfile went live, every on-demand cert for a real site hostname was
silently self-signed by Caddy's internal CA instead of Let's Encrypt — no error, ask endpoint called
correctly, `caddy adapt`'s static output looking completely normal. Root cause, found by reading Caddy
v2.11.4's actual source (`modules/caddytls/automation.go`, `tls.go`) rather than trusting docs or the
static adapted config:

- Caddy's **Automatic HTTPS** logic rebuilds automation policies at *runtime* (not at `caddy adapt`
  time — compare the static adapted JSON against the running config's own debug "adjusted config" log
  or `autosave.json`; they differ). For a site block with a host matcher, the rebuilt policy's
  `subjects` becomes that block's own *address pattern* — so the two-label wildcard block's policy ends
  up with `subjects: ["*.*.{domain}"]`, not the actual hostnames being requested.
- certmagic's own `SubjectQualifiesForPublicCert` (`certificates.go`) explicitly rejects any subject
  with **more than one wildcard label** (CA/Browser Forum requires exactly one, left-most) — so a
  policy whose derived subject is a two-or-three-label wildcard pattern never qualifies, and Caddy's
  `DefaultIssuersProvisioned()` heuristic ("internal unless every subject qualifies for a public cert")
  falls back to the internal CA for it. This is true **regardless of an explicit per-site `issuer`
  directive** — Automatic HTTPS's rebuild does not carry a per-site `tls { on_demand; issuer acme }`
  forward into the policy it reconstructs, confirmed by trying both the bare `issuer acme` shorthand and
  the fully-spelled-out `issuer acme { dir https://acme-v02.api.letsencrypt.org/directory }` form —
  neither survived the rebuild, verified via the running config, not the static one.
- The bare `https://` catch-all block (the one described above, with genuinely *no* subjects) does not
  hit this — an empty subject list doesn't fail `SubjectQualifiesForPublicCert` the way a multi-wildcard
  one does, and (separately, still needing verification) an empty-subjects on-demand policy picking
  `DefaultIssuersProvisioned()`'s "internal" branch by default is exactly why an explicit issuer is still
  needed for it specifically — just not via a per-site directive, since that gets dropped by the same
  rebuild either way.

**The fix**: delete the two wildcard-depth-specific site blocks (`*.*.{$PRAXY_SITES_DOMAIN}`,
`*.*.*.{$PRAXY_SITES_DOMAIN}`) entirely and rely solely on the bare `https://` catch-all for every
Sites hostname shape (subdomains, previews, and custom domains alike) — it has no wildcard-depth
pattern of its own to trip the rejection above, and correctly catches any hostname regardless of label
count (this also makes the Phase 1/Phase 2 wildcard-depth-matching concern moot: a host-less block has
no depth to violate). Pair this with a **global** `cert_issuer acme` in the Caddyfile's top-level
options block — global options are not subject to Automatic HTTPS's per-site policy rebuild, so this
survives where a per-site `issuer` directive didn't.

Verified two ways against a live throwaway Caddy instance running the exact production Caddyfile shape
(env vars substituted, only the ask URL pointed at a local stub):
1. **Before the fix** (three site blocks, `issuer acme` on each): a genuinely fresh, never-cached
   hostname's on-demand obtain completed in ~12–25ms with `"issuer":"local"` in Caddy's own logs — far
   too fast for a real ACME round trip, confirming an instant internal-CA substitution, not an attempted
   and failed ACME order.
2. **After the fix** (single catch-all, global `cert_issuer`): the same test, plus a 2-label site
   hostname and a 3-label preview hostname, all produced genuine ACME order-creation log lines
   (`"logger":"http.acme_client","msg":"creating order"`, `"ca":"https://acme-v02.api.letsencrypt.org/directory"`)
   and were rejected by the **real** Let's Encrypt API with `"Domain name does not end with a valid
   public suffix (TLD)"` — proof of a genuine attempt, since that specific error can only come from
   Let's Encrypt itself, not from Caddy's own internal issuer.

Anyone who has hand-edited a deployed Caddyfile to add back per-site wildcard blocks or a per-site
`issuer` directive should revert to the single-catch-all-plus-global-`cert_issuer` shape above — it is
not a style preference, it's the only shape confirmed to survive Automatic HTTPS's runtime rebuild.

## Git integration: zero new packages (Sites Phase 4)

Three capabilities the GitHub App integration needs, each evaluated against "is a package actually
warranted" and each landing on "no":

- **RS256 JWT signing** (GitHub App identity JWTs — `iss`=App id, ~10 minute expiry, RS256). Hand-rolled
  on `System.Security.Cryptography.RSA` (`ImportFromPem` + `SignData` with `RSASignaturePadding.Pkcs1`)
  and `System.Text.Json`, ~20 lines (`Praxy.Vcs.GitHubAppJwt`). No `System.IdentityModel.Tokens.Jwt` or
  similar: this code only ever *signs* a JWT with a fixed, tiny claim set — it never parses or verifies
  someone else's, which is where a JWT library earns its keep (claim validation, multiple algorithms,
  `kid`-based key rotation, none of which apply here).
- **GitHub webhook HMAC verification** (`X-Hub-Signature-256: sha256=<hex HMACSHA256(secret, rawBody)>`).
  `System.Security.Cryptography.HMACSHA256` + `CryptographicOperations.FixedTimeEquals` — the same BCL
  primitives `Praxy.Webhooks.WebhookSignature` already uses for its own (differently-shaped, Stripe-style)
  outbound scheme. A second ~15-line static method (`Praxy.Vcs.GitHubWebhookSignature`), not a shared
  class — the wire formats genuinely differ (GitHub has no timestamp component), so sharing code here
  would mean a branchy abstraction over two unrelated formats for no real reuse benefit.
- **Git clone** (shallow, exact pushed commit). Shells out to the system `git` CLI via
  `System.Diagnostics.Process` rather than `LibGit2Sharp` — no managed dependency, no native-library
  packaging/platform-matrix concerns (LibGit2Sharp ships `.so`/`.dylib`/`.dll` per-RID, a real
  cross-platform-build headache this project hasn't needed to solve for anything else), and the actual
  operation needed (`init` + `remote add` + `fetch --depth 1 origin <sha>` + `checkout FETCH_HEAD`) is
  four ordinary git subcommands, not something that benefits from a full binding library. Trade-off:
  the container image now needs `git` on `PATH` (`deploy/Dockerfile`'s runtime stage installs it via
  `apt-get`) — a real but small and one-time addition, versus a native-dependency package on every
  build.

None of this needed a new entry in the pinned-versions table above — every dependency used is either
already a transitive reference (`Microsoft.EntityFrameworkCore` via `Praxy.Persistence`) or pure BCL.

## Other notes

- **OpenAPI UI only in Development.** It discloses the full API surface.
- `IOpenApiDocumentProvider` (new in .NET 10) reads documents outside a request — useful for SDK generation.
- Serilog: `builder.Services.AddSerilog(...)` is current; `builder.Host.UseSerilog(...)` is legacy. Use the
  two-stage bootstrap-logger pattern, remembering the final logger **replaces** the bootstrap one, so sinks must
  be redeclared.
- Decide deliberately whether logs go through Serilog sinks *or* OTel logs — both duplicates volume and cost.
- Testcontainers: pin the image tag (`postgres:17-alpine`), share one container per test collection via
  `ICollectionFixture` — container startup dominates otherwise.
