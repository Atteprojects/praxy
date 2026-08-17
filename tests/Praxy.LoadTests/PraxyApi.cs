using System.Net.Http.Json;
using System.Text.Json;

namespace Praxy.LoadTests;

/// <summary>
/// Minimal HTTP client for the console + data-plane surface these load tests drive. Deliberately
/// not the Flutter/console SDK — this talks to whatever instance <c>--endpoint</c> points at over
/// plain HTTP, so the same tool works against a local dev API or a deployed self-host instance.
/// </summary>
public sealed class PraxyApi(string endpoint) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(30) };

    public string Endpoint { get; } = endpoint;

    /// <summary>Claims the instance if unclaimed, else logs in. Either way returns an operator session token.</summary>
    public async Task<string> ClaimOrLoginAsync(string email, string password)
    {
        var claim = await _http.PostAsJsonAsync("/v1/console/claim", new { email, password, name = "Load Test" });
        if (claim.IsSuccessStatusCode)
            return (await claim.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("session").GetProperty("token").GetString()!;

        var login = await _http.PostAsJsonAsync("/v1/console/sessions", new { email, password });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("session").GetProperty("token").GetString()!;
    }

    public async Task<string> CreateProjectAsync(string operatorToken, string name, string? projectId = null)
    {
        var response = await SendAsync(HttpMethod.Post, "/v1/console/projects", operatorToken,
            new { name, projectId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    public async Task<(string Id, string Secret)> CreateApiKeyAsync(string operatorToken, string projectId, params string[] scopes)
    {
        var response = await SendAsync(HttpMethod.Post, $"/v1/console/projects/{projectId}/keys", operatorToken,
            new { name = "load-test", scopes });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("key").GetProperty("id").GetString()!, body.GetProperty("secret").GetString()!);
    }

    public async Task<string> CreateDatabaseAsync(string operatorToken, string projectId, string key, string name)
    {
        var response = await SendAsync(HttpMethod.Post, $"/v1/console/projects/{projectId}/databases", operatorToken,
            new { key, name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    public async Task<string> CreateTableAsync(string operatorToken, string projectId, string databaseId, string key, string name)
    {
        var response = await SendAsync(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/databases/{databaseId}/tables", operatorToken, new { key, name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    public async Task CreateColumnAsync(
        string operatorToken, string projectId, string databaseId, string tableId, string type, object body)
    {
        var response = await SendAsync(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/databases/{databaseId}/tables/{tableId}/columns/{type}",
            operatorToken, body);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetTablePermissionsAsync(
        string operatorToken, string projectId, string databaseId, string tableId, params string[] permissions)
    {
        var response = await SendAsync(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/databases/{databaseId}/tables/{tableId}/permissions",
            operatorToken, new { permissions });
        response.EnsureSuccessStatusCode();
    }

    public HttpRequestMessage DataPlaneRequest(HttpMethod method, string path, string projectId, string? apiKey = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Praxy-Project", projectId);
        if (apiKey is not null)
            request.Headers.Add("X-Praxy-Key", apiKey);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    public Task<HttpResponseMessage> SendDataPlaneAsync(HttpMethod method, string path, string projectId, string? apiKey = null, object? body = null) =>
        _http.SendAsync(DataPlaneRequest(method, path, projectId, apiKey, body));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string operatorToken, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Praxy-Session", operatorToken);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return _http.SendAsync(request);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Percentile summary for a bag of millisecond timings — every command reports one of these.</summary>
public sealed record Timings(int Count, int Failures, double P50Ms, double P95Ms, double P99Ms, double MaxMs, double TotalSeconds)
{
    public static Timings From(List<double> ms, int failures, double totalSeconds)
    {
        if (ms.Count == 0)
            return new Timings(0, failures, 0, 0, 0, 0, totalSeconds);
        ms.Sort();
        return new Timings(ms.Count, failures, Percentile(ms, 0.50), Percentile(ms, 0.95), Percentile(ms, 0.99), ms[^1], totalSeconds);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    public void Print(string label)
    {
        Console.WriteLine(
            $"{label}: {Count} ok, {Failures} failed, {TotalSeconds:F1}s total — " +
            $"p50={P50Ms:F0}ms p95={P95Ms:F0}ms p99={P99Ms:F0}ms max={MaxMs:F0}ms");
    }
}
