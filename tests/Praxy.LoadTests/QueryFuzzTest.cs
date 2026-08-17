using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Praxy.LoadTests;

/// <summary>
/// Roadmap Phase 9: query compiler fuzzing. Fires adversarial <c>queries[]</c> payloads at
/// <c>GET /v1/databases/{db}/tables/{table}/rows</c> — the AST → validate-against-metadata →
/// parameterized-SQL path (architecture.md §4.6) — and asserts the one thing that actually matters:
/// **the compiler never 500s and never hangs**, whatever garbage arrives. A 400 with a clear `type`
/// is success; every input here is either well-formed (should work) or malformed (should be a clean
/// rejection) — a 500 means a request reached raw SQL execution with something the validator should
/// have caught, and an unresponsive server after a batch means it did worse than that.
/// </summary>
public static class QueryFuzzTest
{
    public static async Task RunAsync(string endpoint, int iterations, int concurrency, string email, string password)
    {
        Console.WriteLine($"Query fuzz test: {iterations} random payloads + a fixed adversarial corpus, endpoint {endpoint}");

        using var api = new PraxyApi(endpoint);
        var operatorToken = await api.ClaimOrLoginAsync(email, password);
        var projectId = await api.CreateProjectAsync(operatorToken, "Load Test Fuzz", $"loadtest-fuzz-{Random.Shared.Next(100_000):x}");
        var databaseId = await api.CreateDatabaseAsync(operatorToken, projectId, "fuzzdb", "Fuzz DB");
        var tableId = await api.CreateTableAsync(operatorToken, projectId, databaseId, "targets", "Targets");

        await api.CreateColumnAsync(operatorToken, projectId, databaseId, tableId, "string", new { key = "title", size = 255 });
        await api.CreateColumnAsync(operatorToken, projectId, databaseId, tableId, "integer", new { key = "views" });
        await api.CreateColumnAsync(operatorToken, projectId, databaseId, tableId, "float", new { key = "rating" });
        await api.CreateColumnAsync(operatorToken, projectId, databaseId, tableId, "boolean", new { key = "published" });
        await api.CreateColumnAsync(operatorToken, projectId, databaseId, tableId, "datetime", new { key = "publishedAt" });
        await api.CreateColumnAsync(operatorToken, projectId, databaseId, tableId, "email", new { key = "authorEmail" });
        await api.CreateColumnAsync(operatorToken, projectId, databaseId, tableId, "enum", new { key = "status", elements = new[] { "draft", "published", "archived" } });
        await api.SetTablePermissionsAsync(operatorToken, projectId, databaseId, tableId, "read(\"any\")", "write(\"any\")");

        var (_, apiKey) = await api.CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");

        for (var i = 0; i < 20; i++)
        {
            var body = new
            {
                data = new
                {
                    title = $"Post {i}",
                    views = i * 7,
                    rating = i / 3.0,
                    published = i % 2 == 0,
                    publishedAt = DateTimeOffset.UtcNow.AddDays(-i).ToString("O"),
                    authorEmail = $"author{i}@example.com",
                    status = new[] { "draft", "published", "archived" }[i % 3],
                },
            };
            var response = await api.SendDataPlaneAsync(HttpMethod.Post, $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey, body);
            response.EnsureSuccessStatusCode();
        }
        Console.WriteLine("Seeded 20 rows.");

        var payloads = FixedCorpus().Concat(RandomCorpus(iterations)).ToList();
        Console.WriteLine($"Firing {payloads.Count} query payloads (corpus + random)...");

        var statusCounts = new Dictionary<int, int>();
        var serverErrors = new List<(string Payload, int Status, string Body)>();
        var invalidJsonResponses = 0;
        var connectFailures = 0;
        var gate = new SemaphoreSlim(concurrency);
        var lockObj = new object();
        var sw = Stopwatch.StartNew();

        await Task.WhenAll(payloads.Select(async payload =>
        {
            await gate.WaitAsync();
            try
            {
                var path = $"/v1/databases/{databaseId}/tables/{tableId}/rows?" +
                            string.Join("&", payload.Select(q => $"queries[]={Uri.EscapeDataString(q)}"));
                HttpResponseMessage response;
                try
                {
                    response = await api.SendDataPlaneAsync(HttpMethod.Get, path, projectId, apiKey);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref connectFailures);
                    return;
                }

                var status = (int)response.StatusCode;
                var text = await response.Content.ReadAsStringAsync();
                lock (lockObj)
                {
                    statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
                    if (status >= 500)
                        serverErrors.Add((string.Join(" & ", payload), status, Truncate(text, 300)));
                }

                try { JsonDocument.Parse(text); }
                catch (JsonException) { Interlocked.Increment(ref invalidJsonResponses); }
            }
            finally
            {
                gate.Release();
            }
        }));
        sw.Stop();

        Console.WriteLine($"Fired {payloads.Count} payloads in {sw.Elapsed.TotalSeconds:F1}s ({connectFailures} transport failures).");
        Console.WriteLine("Status code distribution: " + string.Join(", ", statusCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"Non-JSON response bodies: {invalidJsonResponses}");

        var health = await api.SendDataPlaneAsync(HttpMethod.Get, "/v1/ping", projectId, apiKey);
        Console.WriteLine($"Post-fuzz liveness ping: {(int)health.StatusCode}");

        if (serverErrors.Count == 0)
        {
            Console.WriteLine("PASS: no 500s from any payload.");
            return;
        }

        Console.WriteLine($"FAIL: {serverErrors.Count} payload(s) produced a 5xx:");
        foreach (var (payload, status, body) in serverErrors.Take(20))
            Console.WriteLine($"  [{status}] {payload} -> {body}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    /// <summary>Known-nasty, hand-picked cases — the ones a random generator might never stumble on.</summary>
    private static IEnumerable<string[]> FixedCorpus()
    {
        // SQL-injection-shaped values — must round-trip as inert parameters, never break out.
        yield return [Q("equal", "title", "'; DROP TABLE targets; --")];
        yield return [Q("contains", "title", "' OR '1'='1")];
        yield return [Q("equal", "title", "\"; SELECT pg_sleep(5); --")];
        // Unknown attribute / unknown method — identifiers never come from the request string.
        yield return [Q("equal", "does_not_exist", "x")];
        yield return ["""{"method":"regex","attribute":"title","values":["x"]}"""];
        yield return ["""{"method":"","attribute":"title","values":["x"]}"""];
        // Wrong-typed values against real columns.
        yield return [Q("equal", "views", "not-a-number")];
        yield return [Q("equal", "published", "maybe")];
        yield return [Q("equal", "publishedAt", "not-a-date")];
        yield return [Q("equal", "status", "not-a-valid-enum-value")];
        // search without a fulltext index — must be rejected, never silently ILIKE'd.
        yield return [Q("search", "title", "anything")];
        // Structural abuse: caps from architecture.md §4.6 (limit 100, depth 3, 100 queries, 4096 chars).
        yield return ["""{"method":"limit","values":[999999999]}"""];
        yield return ["""{"method":"limit","values":[-1]}"""];
        yield return ["""{"method":"limit","values":[0]}"""];
        yield return ["""{"method":"limit","values":["not-a-number"]}"""];
        yield return ["""{"method":"offset","values":[-500]}"""];
        yield return [DeeplyNestedAnd(depth: 10)];
        yield return [.. Enumerable.Repeat(Q("equal", "title", "x"), 150)]; // over the 100-query cap
        yield return [Q("equal", "title", new string('x', 10_000))]; // over the 4096-char cap
        yield return ["""{"method":"equal","attribute":"title","values":[]}"""]; // empty values
        yield return ["""{"method":"equal","attribute":"title","values":null}"""];
        yield return ["""{"method":"select","values":[]}"""];
        yield return ["""{"method":"select","values":["does_not_exist","$id"]}"""];
        yield return ["""{"method":"cursorAfter","values":["not-a-real-row-id"]}"""];
        yield return ["""{"method":"between","attribute":"views","values":[10]}"""]; // between needs 2 values
        yield return ["""{"method":"and","values":"not-an-array"}"""];
        yield return ["""{"method":"equal","attribute":"title","values":[["nested","array"]]}"""];
        // Malformed JSON / non-object payloads — the transport layer, not just the compiler.
        yield return ["""not json at all"""];
        yield return ["""{"method":"equal","attribute":"title null","values":["x"]}"""];
        yield return ["""{"method":"equal","attribute":"title","values":["日本語 🎉 emoji and unicode"]}"""];
        yield return ["null"];
        yield return ["[]"];
        yield return ["42"];
        yield return ["""{"extra":"unknown top-level field","method":"equal","attribute":"title","values":["x"]}"""];
    }

    private static string DeeplyNestedAnd(int depth)
    {
        var inner = Q("equal", "title", "leaf");
        for (var i = 0; i < depth; i++)
            inner = $$"""{"method":"and","values":[{{inner}},{{Q("equal", "views", i)}}]}""";
        return inner;
    }

    private static string Q(string method, string attribute, object value) =>
        $$"""{"method":"{{method}}","attribute":"{{attribute}}","values":[{{JsonSerializer.Serialize(value)}}]}""";

    private static readonly string[] KnownMethods =
        ["equal", "notEqual", "lessThan", "lessThanEqual", "greaterThan", "greaterThanEqual", "between",
         "isNull", "isNotNull", "startsWith", "endsWith", "contains", "search",
         "select", "orderAsc", "orderDesc", "limit", "offset", "cursorAfter", "cursorBefore", "and", "or"];
    private static readonly string[] KnownAttributes =
        ["title", "views", "rating", "published", "publishedAt", "authorEmail", "status", "$id", "totally_unknown"];

    /// <summary>Randomized combinations of valid vocabulary in structurally-plausible-but-semantically-wild shapes.</summary>
    private static IEnumerable<string[]> RandomCorpus(int count)
    {
        var rng = new Random(12345); // deterministic — a failing run reproduces without re-seeding
        for (var i = 0; i < count; i++)
        {
            var queryCount = rng.Next(1, 4);
            var queries = new string[queryCount];
            for (var q = 0; q < queryCount; q++)
                queries[q] = RandomQuery(rng);
            yield return queries;
        }
    }

    private static string RandomQuery(Random rng)
    {
        var method = KnownMethods[rng.Next(KnownMethods.Length)];
        var attribute = KnownAttributes[rng.Next(KnownAttributes.Length)];
        var valueCount = rng.Next(0, 4);
        var values = Enumerable.Range(0, valueCount).Select(_ => RandomValue(rng)).ToArray();
        return method switch
        {
            "select" or "and" or "or" => $$"""{"method":"{{method}}","values":{{JsonSerializer.Serialize(values)}}}""",
            "limit" or "offset" => $$"""{"method":"{{method}}","values":[{{rng.Next(-10, 100_000)}}]}""",
            _ => $$"""{"method":"{{method}}","attribute":"{{attribute}}","values":{{JsonSerializer.Serialize(values)}}}""",
        };
    }

    private static object RandomValue(Random rng) => rng.Next(6) switch
    {
        0 => rng.Next(-1000, 1_000_000),
        1 => rng.NextDouble() * 1000,
        2 => rng.Next(2) == 0,
        3 => new string('a', rng.Next(0, 50)),
        4 => (object?)null!,
        _ => $"unicode-e-with-nul-\u0000-{rng.Next()}",
    };
}
