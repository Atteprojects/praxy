using System.Diagnostics;
using Npgsql;

namespace Praxy.LoadTests;

/// <summary>
/// Roadmap Phase 9 / architecture.md §13's "load-test with a thousand schemas early... pg_dump,
/// pg_catalog queries and connection startup all degrade in ways that are much cheaper to discover
/// in month one than month five." Creates <c>--count</c> databases (each a real
/// <c>CREATE SCHEMA px_&lt;id&gt;</c>, architecture.md §4.1) in one project, then measures whether a
/// fresh Postgres connection and a <c>pg_catalog</c> scan actually got slower with a thousand extra
/// schemas in the cluster — the concrete thing the risk note warns about, not just "did creation
/// finish."
/// </summary>
public static class SchemaLoadTest
{
    public static async Task RunAsync(string endpoint, string connectionString, int count, int concurrency, string email, string password)
    {
        Console.WriteLine($"Schema load test: {count} databases, concurrency {concurrency}, endpoint {endpoint}");

        using var api = new PraxyApi(endpoint);
        var operatorToken = await api.ClaimOrLoginAsync(email, password);
        var projectId = await api.CreateProjectAsync(operatorToken, "Load Test Schemas", $"loadtest-schemas-{Random.Shared.Next(100_000):x}");
        Console.WriteLine($"Project: {projectId}");

        // The default org quota (20 databases/project) exists precisely to stop this in production —
        // raising it here is the documented escape hatch (docs/self-host.md), not a bypass.
        await RaiseQuotaAsync(connectionString, projectId, count);

        var before = await MeasureCatalogAsync(connectionString);
        Console.WriteLine($"Before: fresh-connect {before.ConnectMs:F0}ms, pg_catalog scan {before.ScanMs:F0}ms " +
                           $"({before.SchemaCount} schemas already in the cluster)");

        var timings = new List<double>();
        var failures = 0;
        var gate = new SemaphoreSlim(concurrency);
        var stopwatch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await api.CreateDatabaseAsync(operatorToken, projectId, $"db{i}", $"Database {i}");
                    lock (timings) timings.Add(sw.Elapsed.TotalMilliseconds);
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        stopwatch.Stop();

        Timings.From(timings, failures, stopwatch.Elapsed.TotalSeconds).Print("CREATE SCHEMA (database create)");

        var after = await MeasureCatalogAsync(connectionString);
        Console.WriteLine($"After:  fresh-connect {after.ConnectMs:F0}ms, pg_catalog scan {after.ScanMs:F0}ms " +
                           $"({after.SchemaCount} schemas now in the cluster)");
        Console.WriteLine(
            $"Degradation: connect {Ratio(before.ConnectMs, after.ConnectMs)}x, catalog scan {Ratio(before.ScanMs, after.ScanMs)}x");
    }

    private static string Ratio(double before, double after) =>
        before < 0.01 ? "n/a (baseline ~0ms)" : (after / before).ToString("F1");

    private static async Task RaiseQuotaAsync(string connectionString, string projectId, int atLeast)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE praxy.organizations SET limits = jsonb_set(limits, '{maxDatabasesPerProject}', $1::text::jsonb)
            WHERE id = (SELECT organization_id FROM praxy.projects WHERE id = $2)
            """, conn);
        cmd.Parameters.AddWithValue(atLeast + 10);
        cmd.Parameters.AddWithValue(projectId);
        await cmd.ExecuteNonQueryAsync();
    }

    private readonly record struct CatalogSnapshot(double ConnectMs, double ScanMs, int SchemaCount);

    private static async Task<CatalogSnapshot> MeasureCatalogAsync(string connectionString)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var connectMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name LIKE 'px\\_%'", conn);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        var scanMs = sw.Elapsed.TotalMilliseconds;

        return new CatalogSnapshot(connectMs, scanMs, (int)count);
    }
}
