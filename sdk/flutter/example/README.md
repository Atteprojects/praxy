# Praxy SDK example

A runnable Flutter app exercising the full [`praxy_flutter`](../praxy_flutter/) surface against a
real Praxy instance: sign-up, Google OAuth, typed row CRUD, and a live realtime subscription. This
is the exact flow validated as the SDK's owner test in
[docs/handoff/phase-5-report.md](../../../docs/handoff/phase-5-report.md).

## Run it

1. Start Praxy locally (`cd deploy && ./up.sh`, or `dotnet run --project src/Praxy.Api` for dev —
   see the [root README](../../../README.md)).
2. In the console, create a project, a database, and a table with a couple of string/boolean
   columns — the app needs real generated ids, not arbitrary strings.
3. Run:

   ```bash
   flutter run \
     --dart-define=PRAXY_ENDPOINT=http://localhost:5090 \
     --dart-define=PRAXY_PROJECT_ID=<id> \
     --dart-define=PRAXY_DATABASE_ID=<id> \
     --dart-define=PRAXY_TABLE_ID=<id>
   ```

   `PRAXY_ENDPOINT` defaults to `http://localhost:5090` if omitted. The app shows a
   "run with: ..." screen instead of crashing if `PRAXY_PROJECT_ID`/`PRAXY_DATABASE_ID`/`PRAXY_TABLE_ID`
   are missing.

Google sign-in needs a project with Google OAuth configured in the console, plus the Android
callback-scheme intent-filter this example's `AndroidManifest.xml` already declares
(`com.praxy.example` — see [docs/research/flutter-sdk.md](../../../docs/research/flutter-sdk.md) if
adapting this for your own app's bundle id). iOS needs no extra setup.

`lib/db.dart` holds the table reference(s) this example reads/writes — a small hand-written
`RowCodec`, or regenerate its `Col` constants with
[`praxy_codegen`](../praxy_codegen/) once your schema is stable.
