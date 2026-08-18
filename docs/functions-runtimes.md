# Writing a function

This is the contract your uploaded code has to satisfy for each runtime Praxy supports today
(Dart, Node). If you're looking for the HTTP API to create/deploy/invoke functions, see
`docs/api-reference.md` and the console's Functions pages instead — this doc is only about what
goes *inside* the deployment tar.

## How invocation works

Every function runs as its own Docker container, built from your uploaded source plus a Praxy-
generated wrapper that starts an HTTP server on port 3000 inside the container. An invocation is a
single request to that server: a JSON envelope in, a JSON envelope out. Your code never sees raw
HTTP — Praxy's wrapper does the translation.

**In** (what your function receives, as the `context` argument):

```json
{ "method": "GET", "path": "/", "body": "", "headers": {} }
```

`method`/`path`/`body` are whatever the caller passed when invoking (the console's Run modal, or
the data-plane `POST /v1/functions/{id}/executions` body) — Praxy does not proxy a real inbound
HTTP request to your function, so there is no real request to reflect. `headers` is currently
always empty on the way in, regardless of what the caller sent — nothing forwards caller headers
into the function today.

**Out** (what your function must return):

```json
{ "statusCode": 200, "body": "...", "headers": {} }
```

`body` is always a string (JSON-encode your own payload if it's structured data — see the examples
below). `statusCode` defaults to 200 if omitted. Anything your function throws is captured and
stored on the execution as `errors`, with `statusCode` reported as the underlying runtime's own
failure indicator — the execution is marked `failed`, not silently swallowed.

## Environment variables available to your function

- `PRAXY_FUNCTION_ID`, `PRAXY_PROJECT_ID` — always set.
- `PRAXY_FUNCTION_JWT`, `PRAXY_FUNCTION_USER_ID` — set only when the invocation was triggered by a
  specific app user (a JWT or an authenticated app-user session on the data-plane endpoint) — use
  the JWT to call back into Praxy's own data plane as that user. Absent for console/event/schedule
  triggers.
- Anything you set yourself on the function's Settings tab (stored encrypted at rest, decrypted
  into the container's environment at invoke time).

## Packaging and deploying

Upload a `.tar` (not zip) with your entrypoint file at its root — `tar -cf fn.tar main.dart
pubspec.yaml`, not a zipped folder. A successful build activates automatically; see the function's
Deployments tab for build logs. The entrypoint filename is whatever you set when creating the
function (`main.dart`/`index.js` by default, but any name matching the runtime's expected
extension works — Praxy validates the extension, not the exact name).

## Dart

**Contract:** the entrypoint file exports a top-level `handler` function — not `main`. This isn't a
style choice: Dart enforces `List<String>`-only arguments on *any* top-level `main` in a compiled
program, even one that's only ever reached through an `import`, never actually run as the process's
own entry point. A function file that defines its own custom-signature `main` fails to compile the
moment Praxy's wrapper imports it. `handler` is an ordinary function name and carries none of
`main`'s special rules.

```dart
Future<Map<String, dynamic>> handler(Map<String, dynamic> context) async {
  return {
    'statusCode': 200,
    'body': 'Hello, World!',
  };
}
```

A `pubspec.yaml` is required (the build runs `dart pub get` if one is present in the upload — omit
dependencies you don't need, but the file itself must exist):

```yaml
name: hello_world_function
version: 1.0.0
environment:
  sdk: '>=2.19.0 <4.0.0'
```

Base image: `dart:stable` by default (`Praxy:Functions:DartBaseImage`).

## Node

**Contract:** the entrypoint file's default (or sole) export is `async (context) => ({ statusCode,
body, headers })`.

```js
module.exports = async (context) => ({
  statusCode: 200,
  body: JSON.stringify({ message: 'Hello, World!' }),
  headers: { 'content-type': 'application/json' },
});
```

A `package.json` is optional — if present, the build runs `npm install --omit=dev` before your
code runs, so you can bring in dependencies. Skip it entirely for a dependency-free function.

Base image: `node:22-alpine` by default (`Praxy:Functions:NodeBaseImage`).

## Limits

All configurable via `Praxy:Functions:*` (see `docs/self-host.md`'s config table) — defaults:

| Limit | Default | Config key |
|---|---|---|
| Sync invocation hard cap | 30s | `MaxSyncTimeoutSeconds` |
| Cold start (build image → healthy) | 60s | `ColdStartTimeoutSeconds` |
| Memory per container | 256 MB | `MemoryLimitMb` |
| CPU per container | 1.0 | `CpuLimit` |
| Captured response/log bytes | 64 KB | `MaxResponseCaptureBytes` |
| Uploaded source size | 25 MB | `MaxSourceBytes` |
| Build timeout | 600s | `BuildTimeoutSeconds` |

A function's own configured timeout (set per-function, up to 900s) only applies to *async*
invocations — sync invocations are always additionally capped at `MaxSyncTimeoutSeconds`, whichever
is lower.
