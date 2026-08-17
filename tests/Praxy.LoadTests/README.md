# Praxy.LoadTests

Roadmap Phase 9's load-test scripts: 1k schemas, 10k WebSocket connections, query compiler fuzzing.
A runnable console tool, not a claim — see `docs/handoff/phase-9-report.md` for the results of the
run this phase shipped with.

Deliberately **not** an xUnit project under `dotnet test` — these are long-running, resource-heavy
runs against a live instance (dev, self-host, or a disposable throwaway one), not fast assertions
that belong in the normal CI loop. Each command claims the instance if unclaimed, or logs in with
`--email`/`--password` if already claimed, and creates its own scratch projects — safe to run
repeatedly against the same instance.

```bash
# 1k schemas — needs direct Postgres access to raise the org's database quota
# (see docs/self-host.md's Configuration section) and to measure pg_catalog scan time before/after.
dotnet run --project tests/Praxy.LoadTests -- schemas \
  --connection "Host=localhost;Port=5432;Database=praxy;Username=praxy;Password=praxy" \
  --endpoint http://localhost:5090 --count 1000 --concurrency 20

# 10k WebSocket connections — raise the client shell's file-descriptor limit first.
ulimit -n 20000
dotnet run --project tests/Praxy.LoadTests -- websockets \
  --endpoint http://localhost:5090 --projects 10 --connections-per-project 1000

# Query compiler fuzzing — a fixed adversarial corpus (SQL-injection-shaped values, type mismatches,
# cap violations) plus N randomized payloads. Exit criterion: zero 5xx responses.
dotnet run --project tests/Praxy.LoadTests -- fuzz \
  --endpoint http://localhost:5090 --iterations 5000
```

Every command prints what it did and a `Timings` summary (count, failures, p50/p95/p99/max
latency). `fuzz` additionally prints the HTTP status code distribution and fails loudly (a non-zero
process exit is not implemented — read the `FAIL:` line) if anything came back `5xx`, since that's
the one outcome that always means a bug: every fuzzed input is either well-formed (should succeed)
or malformed (should be a clean 4xx), never something that should reach unhandled application code.
