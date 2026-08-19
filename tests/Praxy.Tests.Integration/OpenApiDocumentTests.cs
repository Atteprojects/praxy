using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The generated document is the published API reference (docs/api-reference.md) — the only one
/// anyone gets without running an instance. It described request bodies and nothing else for ten
/// phases, which nobody noticed because nothing asserted on it. These tests are the ratchet: a new
/// endpoint that forgets its response type fails here rather than shipping undocumented.
/// </summary>
public class OpenApiDocumentTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    /// <summary>The document is dev-only by design, so the test host has to ask for Development.</summary>
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Development",
    };

    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

    private async Task<JsonElement> DocumentAsync()
    {
        var response = await Client.GetAsync("/openapi/v1.json");
        Assert.Equal(200, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private static IEnumerable<(string Method, string Path, JsonElement Op)> Operations(JsonElement doc)
    {
        foreach (var path in doc.GetProperty("paths").EnumerateObject())
            foreach (var op in path.Value.EnumerateObject())
                if (HttpMethods.Contains(op.Name))
                    yield return (op.Name.ToUpperInvariant(), path.Name, op.Value);
    }

    [Fact]
    public async Task Every_operation_documents_a_response_body_or_says_it_has_none()
    {
        var doc = await DocumentAsync();
        var undocumented = new List<string>();

        foreach (var (method, path, op) in Operations(doc))
        {
            if (!op.TryGetProperty("responses", out var responses))
            {
                undocumented.Add($"{method} {path}");
                continue;
            }

            // Either a success status carrying a schema, or one of the statuses that legitimately
            // has no body: 204 (deleted), 302 (redirect), 101 (WebSocket upgrade).
            var ok = responses.EnumerateObject().Any(r =>
                (r.Name.StartsWith('2') && r.Value.TryGetProperty("content", out _))
                || r.Name is "204" or "302" or "101");
            if (!ok)
                undocumented.Add($"{method} {path}");
        }

        Assert.True(undocumented.Count == 0,
            "These operations document no response. Add .Produces<T>() (or .Produces(204/302)) where "
            + "they are mapped:\n  " + string.Join("\n  ", undocumented));
    }

    /// <summary>
    /// Error <c>type</c> strings are public API (CLAUDE.md), so the envelope carrying them has to be
    /// in the document rather than something an SDK author reverse-engineers from a live instance.
    /// </summary>
    [Fact]
    public async Task Every_operation_documents_the_error_envelope()
    {
        var doc = await DocumentAsync();

        Assert.True(
            doc.GetProperty("components").GetProperty("schemas").TryGetProperty("ErrorEnvelope", out var envelope),
            "ErrorEnvelope is missing from components.schemas.");

        var properties = envelope.GetProperty("properties");
        foreach (var required in new[] { "message", "code", "type", "version", "requestId", "fields" })
            Assert.True(properties.TryGetProperty(required, out _), $"ErrorEnvelope is missing '{required}'.");

        var missing = new List<string>();
        foreach (var (method, path, op) in Operations(doc))
        {
            // /v1/health never returns the envelope — it is liveness, polled by load balancers.
            if (path == "/v1/health")
                continue;
            if (!op.GetProperty("responses").TryGetProperty("default", out var fallback))
            {
                missing.Add($"{method} {path}");
                continue;
            }
            var reference = fallback.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString();
            Assert.Equal("#/components/schemas/ErrorEnvelope", reference);
        }

        Assert.True(missing.Count == 0, "No documented error response on:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// 429 carries headers a client is expected to act on, so it is spelled out rather than folded
    /// into `default` — and only on endpoints that can actually produce it.
    /// </summary>
    [Fact]
    public async Task Rate_limited_operations_document_their_429_and_its_headers()
    {
        var doc = await DocumentAsync();

        var limited = Operations(doc)
            .Where(o => o.Op.GetProperty("responses").TryGetProperty("429", out _))
            .ToList();
        Assert.NotEmpty(limited);

        foreach (var (_, _, op) in limited)
        {
            var headers = op.GetProperty("responses").GetProperty("429").GetProperty("headers");
            foreach (var header in new[] { "Retry-After", "RateLimit-Limit", "RateLimit-Remaining", "RateLimit-Reset" })
                Assert.True(headers.TryGetProperty(header, out _), $"429 is missing the {header} header.");
        }

        // The auth and data-plane buckets both have to be represented — a regression that dropped
        // RequireRateLimiting from a whole surface would otherwise still pass the loop above.
        var paths = limited.Select(o => o.Path).ToList();
        Assert.Contains("/v1/account/sessions/email", paths);
        Assert.Contains("/v1/databases/{databaseId}/tables/{tableId}/rows", paths);
        Assert.Contains("/v1/functions/{functionId}/executions", paths);
        Assert.Contains("/v1/realtime/ticket", paths);
    }

    /// <summary>
    /// The committed snapshot is what everyone not running a dev instance reads. If it drifts from
    /// what the code generates, the published reference is a lie — this catches "forgot to
    /// regenerate" at test time rather than at the next release.
    /// </summary>
    [Fact]
    public async Task The_committed_snapshot_matches_what_the_code_generates()
    {
        var repoRoot = FindRepoRoot();
        var snapshotPath = Path.Combine(repoRoot, "docs", "openapi", "v1.json");
        Assert.True(File.Exists(snapshotPath), $"Snapshot not found at {snapshotPath}.");

        var live = Normalize(await DocumentAsync());
        var committed = Normalize(JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath)).RootElement);

        Assert.True(live == committed,
            "docs/openapi/v1.json is stale. Regenerate it (docs/api-reference.md has the command) "
            + "and commit the result.");
    }

    /// <summary>Re-serializes with sorted keys so formatting differences never fail the comparison.</summary>
    private static string Normalize(JsonElement element) =>
        JsonSerializer.Serialize(
            JsonSerializer.Deserialize<object>(element.GetRawText()),
            new JsonSerializerOptions { WriteIndented = false });

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
