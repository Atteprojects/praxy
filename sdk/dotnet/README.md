# Praxy .NET SDK

Server-side .NET SDK for Praxy. Authenticates with a project API key.

```csharp
using var praxy = new PraxyClient("https://api.example.com", "<projectId>", "<keyId>.<secret>");

var page = await praxy.Users.List(limit: 50, search: "ada");
var user = await praxy.Users.Create(email: "new@user.com", password: "hunter2");
await praxy.Users.DeleteAllSessions(user!.Id);
```

## Server-only, on purpose

There is no session-based constructor. A project API key is not scoped to one end user the way a
session is, and a key shipped inside a desktop or mobile application is extractable from it. Unlike
`praxy_core` — which is dual-mode because it is also the base of the Flutter *client* SDK — this
package has no client half to serve, so the safest shape is the only shape.

`PraxyClient` is safe to keep for the process lifetime and to share across threads, like the
`HttpClient` it wraps. Pass your own `HttpClient` to control handlers, proxies or timeouts; the
client then does not dispose it.

## Errors

Every failure is a `PraxyException`. API failures carry the server's stable, machine-readable
`Type` — switch on that, never on `Message`, which is prose and may be reworded.

`PraxyAuthException` (401/403) · `PraxyNotFoundException` (404) · `PraxyConflictException` (409) ·
`PraxyRateLimitException` (429, with `RetryAfter`) · `PraxyValidationException` (per-field `Fields`)
· `PraxyNetworkException` (never reached the server) · `PraxyDecodeException`.

The hierarchy deliberately mirrors `praxy_core`'s and `@praxy/core`'s class for class.

## What is generated

`Services/Generated/` comes from `docs/openapi/v1.json` via `sdk/generator` — regenerate with
`node sdk/generator/bin/generate.mjs --target csharp`, and CI fails if the committed output is
stale. Everything else here is hand-written, because it carries decisions a generator would flatten.

## Layout

- `Praxy.Sdk/` — the package.
- `Praxy.Sdk.Tests/` — tests, including ones that exercise the *generated* code against a stub
  handler rather than only inspecting the emitted text.

Both are in `Praxy.sln`, so `dotnet build` and `dotnet test` at the repo root cover them.
