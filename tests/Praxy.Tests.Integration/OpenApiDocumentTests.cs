using System.Text.Json;
using Praxy.Api.Infrastructure;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The generated document is the published API reference (docs/api-reference.md) — the only one
/// anyone gets without running an instance. It described request bodies and nothing else for ten
/// phases, which nobody noticed because nothing asserted on it. These tests are the ratchet: a new
/// endpoint that forgets its response type fails here rather than shipping undocumented.
/// </summary>
public partial class OpenApiDocumentTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    /// <summary>The document is dev-only by design, so the test host has to ask for Development.</summary>
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Development",
    };

    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

    /// <summary>
    /// Operation summaries may only ever increase in coverage.
    ///
    /// <para>Summaries come from a plain <c>/// &lt;summary&gt;</c> on the handler, lifted into the
    /// document by <c>OpenApiXmlSummaries</c>. .NET's own XML support does not do this: it
    /// documents public DTOs, and every handler here is <c>private static</c>, which is why the
    /// count sat at 1 of 290 until that transformer existed.</para>
    ///
    /// <para>A budget rather than a list of exemptions. An allow-list of a couple of hundred
    /// undocumented operations is a file nobody reads and everybody appends to; a number that may
    /// only go down is one line, and it fails the moment a new endpoint ships without a summary.
    /// When you document more, lower it — that is the ratchet turning.</para>
    /// </summary>
    [Fact]
    public async Task No_more_operations_lack_a_summary_than_the_current_budget()
    {
        const int budget = 246;

        var doc = await DocumentAsync();
        var undocumented = Operations(doc)
            .Where(o => !o.Op.TryGetProperty("summary", out var s) || string.IsNullOrWhiteSpace(s.GetString()))
            .Select(o => o.Op.TryGetProperty("operationId", out var id) ? id.GetString()! : $"{o.Method} {o.Path}")
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undocumented.Count <= budget,
            $"{undocumented.Count} operations have no summary, budget is {budget}. Add a "
            + "/// <summary> to the handler method.\nFirst few:\n  "
            + string.Join("\n  ", undocumented.Take(10)));
    }

    /// <summary>
    /// Every query-string key a handler reads must be documented as a parameter on its operation.
    ///
    /// <para>The gap this locks shut: .NET documents only parameters the framework binds, and these
    /// handlers read theirs straight out of <c>HttpContext.Request.Query</c>. The result was a
    /// document describing no query parameters at all — and a generated SDK whose
    /// <c>users.list()</c> could not page, faithfully, because the document said there was nothing
    /// to pass. <c>OpenApiQueryParameters</c> declares them; without this test, the declaration and
    /// the handler drift the first time someone adds a filter.</para>
    ///
    /// <para>This reads the endpoint sources rather than the compiled assembly because the fact
    /// being checked — "this code reads this query key" — exists only in the source. A handler is
    /// matched to its operation through <c>OpenApiOperationIds.Compute</c>, the same derivation the
    /// document itself uses, so the two cannot disagree about which operation a method produces.</para>
    /// </summary>
    [Fact]
    public async Task Every_query_parameter_a_handler_reads_is_documented()
    {
        var doc = await DocumentAsync();
        var documented = Operations(doc)
            .Where(o => o.Op.TryGetProperty("operationId", out _))
            .ToDictionary(
                o => o.Op.GetProperty("operationId").GetString()!,
                o => o.Op.TryGetProperty("parameters", out var ps)
                    ? ps.EnumerateArray()
                        .Where(p => p.GetProperty("in").GetString() == "query")
                        .Select(p => p.GetProperty("name").GetString()!)
                        .ToHashSet(StringComparer.Ordinal)
                    : []);

        var missing = new List<string>();
        foreach (var (operationId, keys) in QueryKeysByHandler())
        {
            if (!documented.TryGetValue(operationId, out var declared))
                continue; // Not an operation — a helper, or a handler this class does not map.
            foreach (var key in keys.Where(k => !declared.Contains(k)))
                missing.Add($"{operationId} reads ?{key}= but does not document it");
        }

        Assert.True(
            missing.Count == 0,
            "Undocumented query parameters — declare them with .WithPagination()/.WithSearch()/"
            + $".WithQueryParameters(...) on the endpoint mapping:\n  {string.Join("\n  ", missing)}");
    }

    /// <summary>
    /// Every query key each handler reads, <em>including through the helpers it calls</em>.
    ///
    /// <para>The indirection is the whole point. These files do not read
    /// <c>Request.Query["limit"]</c> in the handler; they call a private
    /// <c>ListParams(HttpContext)</c> that does. A version of this check that only looked at the
    /// handler's own body attributed those keys to <c>ListParams</c> — which is not an operation,
    /// so they were skipped — and it passed happily with every <c>.WithPagination()</c> deleted.
    /// Verified by deleting one: the check named nothing. So calls are followed transitively.</para>
    /// </summary>
    private static IEnumerable<(string OperationId, HashSet<string> Keys)> QueryKeysByHandler()
    {
        var direct = new Dictionary<(string Type, string Method), HashSet<string>>();
        var calls = new Dictionary<(string Type, string Method), HashSet<(string, string)>>();

        foreach (var file in Directory.EnumerateFiles(EndpointsDirectory(), "*.cs"))
        {
            var typeName = Path.GetFileNameWithoutExtension(file);
            (string, string)? current = null;

            foreach (var line in File.ReadAllLines(file))
            {
                var signature = MethodSignature().Match(line);
                if (signature.Success)
                {
                    current = (typeName, signature.Groups["name"].Value);
                    direct.TryAdd(current.Value, []);
                    calls.TryAdd(current.Value, []);
                    continue;
                }
                if (current is not { } method)
                    continue;

                foreach (System.Text.RegularExpressions.Match read in QueryRead().Matches(line))
                    direct[method].Add(read.Groups["key"].Value);
                foreach (System.Text.RegularExpressions.Match call in MethodCall().Matches(line))
                {
                    var qualifier = call.Groups["type"].Success ? call.Groups["type"].Value : typeName;
                    calls[method].Add((qualifier, call.Groups["name"].Value));
                }
            }
        }

        foreach (var method in direct.Keys)
        {
            var keys = Reachable(method, direct, calls, []);
            if (keys.Count > 0)
                yield return (OpenApiOperationIds.Compute(method.Type, method.Method), keys);
        }
    }

    /// <summary>Keys a method reads directly or through anything it calls; cycle-safe via <paramref name="seen"/>.</summary>
    private static HashSet<string> Reachable(
        (string Type, string Method) method,
        Dictionary<(string, string), HashSet<string>> direct,
        Dictionary<(string, string), HashSet<(string, string)>> calls,
        HashSet<(string, string)> seen)
    {
        if (!seen.Add(method) || !direct.TryGetValue(method, out var own))
            return [];

        var keys = new HashSet<string>(own, StringComparer.Ordinal);
        foreach (var callee in calls[method])
            keys.UnionWith(Reachable(callee, direct, calls, seen));
        return keys;
    }

    /// <summary>
    /// The endpoint sources, found by walking up from the test assembly to the repository root.
    /// Fails loudly rather than silently finding nothing: a test that reads zero files passes
    /// trivially, which is the one outcome that would hide the very drift it exists to catch.
    /// </summary>
    private static string EndpointsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Praxy.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var endpoints = Path.Combine(directory!.FullName, "src", "Praxy.Api", "Endpoints");
        Assert.True(Directory.Exists(endpoints), $"Endpoint sources not found at {endpoints}.");
        Assert.NotEmpty(Directory.GetFiles(endpoints, "*.cs"));
        return endpoints;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^\s*(?:private|internal|public)\s+(?:static\s+)?(?:async\s+)?[\w<>?,\s\[\]().]+?\s(?<name>\w+)\s*\(")]
    private static partial System.Text.RegularExpressions.Regex MethodSignature();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:Request\.Query|\bq)\[""(?<key>[^""]+)""\]")]
    private static partial System.Text.RegularExpressions.Regex QueryRead();

    /// <summary>A call to <c>Helper(</c> or <c>OtherEndpoints.Helper(</c>, used to follow indirection.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:(?<type>[A-Z]\w*)\.)?\b(?<name>[A-Z]\w*)\s*\(")]
    private static partial System.Text.RegularExpressions.Regex MethodCall();

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
    /// <c>Program.cs</c> sets <c>DefaultIgnoreCondition = WhenWritingNull</c>: a null-valued property
    /// is dropped from the JSON, so it reaches a client absent, never as <c>null</c>.
    /// <see cref="Praxy.Api.Infrastructure.OpenApiWireNullability"/> makes the document say that.
    /// Without this test the fix is silently removable — delete the transformer, regenerate the
    /// snapshot, and every other gate here still passes while the console's generated types go back
    /// to <c>foo: T | null</c>, which is precisely the bug PR #55 fixed.
    ///
    /// Asserted over the whole document rather than just the components the transformer walks, so a
    /// future endpoint whose nullable property lands in an *inline* schema fails here too.
    /// </summary>
    [Fact]
    public async Task No_schema_anywhere_documents_a_property_as_nullable()
    {
        var doc = await DocumentAsync();
        var offenders = new List<string>();
        CollectNullTypes(doc, "$", offenders);

        Assert.True(offenders.Count == 0,
            "These schemas say a value may be null, but WhenWritingNull means it is absent instead. "
            + "OpenApiWireNullability should have rewritten them — if one is genuinely nullable on "
            + "the wire, that is a wire-shape decision, not a documentation one:\n  "
            + string.Join("\n  ", offenders));
    }

    private static void CollectNullTypes(JsonElement element, string path, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("type") && MentionsNull(property.Value))
                        offenders.Add(path);
                    CollectNullTypes(property.Value, $"{path}.{property.Name}", offenders);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    CollectNullTypes(item, $"{path}[{index++}]", offenders);
                break;
        }
    }

    /// <summary>A type is either a string or an array of them; "null" in either spelling counts.</summary>
    private static bool MentionsNull(JsonElement type) =>
        type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() == "null",
            JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String
                                                                  && t.GetString() == "null"),
            _ => false,
        };

    /// <summary>
    /// Every operation needs a stable, unique <c>operationId</c>, because an SDK generator names its
    /// methods from it. Two failures matter and they fail differently.
    ///
    /// <b>Missing</b> means a lambda was mapped inline: <see cref="Praxy.Api.Infrastructure.OpenApiOperationIds"/>
    /// deliberately refuses to derive a name from a compiler-generated one like <c>&lt;Map&gt;b__0_0</c>,
    /// which is meaningless in an SDK and changes when surrounding code is reordered. Add
    /// <c>.WithName("resource.action")</c> to that endpoint.
    ///
    /// <b>Duplicated</b> is worse and quieter: two endpoints resolving to one id collapse into a
    /// single SDK method, and whichever the generator emits second wins. That is invisible in the
    /// document and obvious only when someone calls the wrong endpoint.
    /// </summary>
    [Fact]
    public async Task Every_operation_has_a_unique_operation_id()
    {
        var doc = await DocumentAsync();
        var missing = new List<string>();
        var seen = new Dictionary<string, string>();
        var duplicated = new List<string>();

        foreach (var (method, path, op) in Operations(doc))
        {
            if (!op.TryGetProperty("operationId", out var idElement) ||
                idElement.GetString() is not { Length: > 0 } id)
            {
                missing.Add($"{method} {path}");
                continue;
            }

            if (seen.TryGetValue(id, out var firstSeenOn))
                duplicated.Add($"'{id}' on both {firstSeenOn} and {method} {path}");
            else
                seen[id] = $"{method} {path}";
        }

        Assert.True(missing.Count == 0,
            "These operations have no operationId — an inline lambda cannot supply one. Add "
            + ".WithName(\"resource.action\") where they are mapped:\n  " + string.Join("\n  ", missing));
        Assert.True(duplicated.Count == 0,
            "These operationIds are not unique, so an SDK generator would emit one method for two "
            + "endpoints:\n  " + string.Join("\n  ", duplicated));
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
