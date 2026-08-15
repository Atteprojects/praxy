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
| `Docker.DotNet.Enhanced` | 4.3.3 | Phase 7 only |
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

## Other notes

- **OpenAPI UI only in Development.** It discloses the full API surface.
- `IOpenApiDocumentProvider` (new in .NET 10) reads documents outside a request — useful for SDK generation.
- Serilog: `builder.Services.AddSerilog(...)` is current; `builder.Host.UseSerilog(...)` is legacy. Use the
  two-stage bootstrap-logger pattern, remembering the final logger **replaces** the bootstrap one, so sinks must
  be redeclared.
- Decide deliberately whether logs go through Serilog sinks *or* OTel logs — both duplicates volume and cost.
- Testcontainers: pin the image tag (`postgres:17-alpine`), share one container per test collection via
  `ICollectionFixture` — container startup dominates otherwise.
