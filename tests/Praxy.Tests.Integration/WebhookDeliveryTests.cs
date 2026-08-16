using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;
using Praxy.Webhooks;

namespace Praxy.Tests.Integration;

/// <summary>
/// The Phase 6 owner-test flow end to end, against real loopback sockets: the outbox dispatcher and
/// delivery worker are real hosted services in this process making real HTTP calls, not stubbed —
/// only the SSRF guard's default (block private/loopback ranges) is relaxed via
/// <c>AllowPrivateNetworkTargets</c>, which is exactly the self-host escape hatch it exists for
/// (verified separately, without a live server, by <c>SsrfGuardTests</c>). Poll/backoff settings are
/// tightened so retry/auto-disable behavior is observable in seconds instead of minutes.
/// </summary>
public class WebhookDeliveryTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Webhooks:AllowPrivateNetworkTargets"] = "true",
        ["Praxy:Webhooks:DispatchPollIntervalSeconds"] = "1",
        ["Praxy:Webhooks:DeliveryPollIntervalSeconds"] = "1",
        ["Praxy:Webhooks:TimeoutSeconds"] = "5",
        ["Praxy:Webhooks:MaxAttempts"] = "3",
        ["Praxy:Webhooks:BackoffBaseSeconds"] = "0.2",
        ["Praxy:Webhooks:BackoffCapSeconds"] = "1",
        ["Praxy:Webhooks:DisableAfterConsecutiveFailures"] = "1",
    };

    [Fact]
    public async Task Owner_test_flow_delivers_and_the_signature_verifies()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        await using var echo = new EchoListener();
        var (webhookId, secret) = await CreateWebhookAsync(operatorToken, projectId, echo.Url,
            ["databases.*.tables.*.rows.*.create"]);

        var row = await CreateRowAsync(projectId, apiKey, databaseId, tableId,
            new { data = new { title = "Hello", views = 1, published = false } });
        var rowId = row.GetProperty("$id").GetString()!;

        var (headers, body) = await echo.NextRequestAsync();

        Assert.Equal(webhookId, headers["X-Praxy-Webhook-Id"]);
        Assert.Equal(projectId, headers["X-Praxy-Webhook-ProjectId"]);
        Assert.Contains("databases.*.tables.*.rows.*.create", headers["X-Praxy-Webhook-Events"]);
        var timestamp = long.Parse(headers["X-Praxy-Webhook-Timestamp"]!);
        Assert.True(WebhookSignature.Verify(secret, timestamp, body, headers["X-Praxy-Webhook-Signature"]!));

        var payload = JsonDocument.Parse(body).RootElement;
        Assert.EndsWith(".create", payload.GetProperty("event").GetString());
        Assert.Equal(projectId, payload.GetProperty("projectId").GetString());
        Assert.Equal(rowId, payload.GetProperty("data").GetProperty("rowId").GetString());

        var delivery = await WaitForDeliveryStatusAsync(operatorToken, projectId, webhookId, "succeeded");
        Assert.Equal(1, delivery.GetProperty("attempts").GetInt32());
        Assert.Equal(200, delivery.GetProperty("lastStatusCode").GetInt32());

        // Never re-echoed after creation — list and single-get both omit the secret entirely.
        var list = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/webhooks", operatorToken)));
        var listed = list.GetProperty("webhooks").EnumerateArray().Single(w => w.GetProperty("id").GetString() == webhookId);
        Assert.False(listed.TryGetProperty("secret", out _));

        // Redeliver clones a fresh row rather than mutating the original's history.
        var deliveryId = delivery.GetProperty("id").GetString()!;
        var redeliverResponse = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/webhooks/{webhookId}/deliveries/{deliveryId}/redeliver", operatorToken));
        Assert.Equal(201, (int)redeliverResponse.StatusCode);
        var redelivered = await ReadJson(redeliverResponse);
        var redeliveredId = redelivered.GetProperty("id").GetString()!;
        Assert.NotEqual(deliveryId, redeliveredId);
        Assert.Equal(deliveryId, redelivered.GetProperty("redeliveredFromId").GetString());

        await echo.NextRequestAsync(); // the redelivery's own attempt
        var redeliveredDetail = await WaitForDeliveryStatusAsync(operatorToken, projectId, webhookId, "succeeded", redeliveredId);
        Assert.Equal("succeeded", redeliveredDetail.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Dead_endpoint_retries_then_the_subscription_auto_disables()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        await using var dead = new EchoListener(responseStatus: 500);
        var (webhookId, _) = await CreateWebhookAsync(operatorToken, projectId, dead.Url,
            ["databases.*.tables.*.rows.*.create"]);

        await CreateRowAsync(projectId, apiKey, databaseId, tableId,
            new { data = new { title = "Retry me", views = 0, published = false } });

        // MaxAttempts = 3: the dead endpoint should see exactly that many real attempts, each 500.
        for (var i = 0; i < 3; i++)
        {
            var (_, _) = await dead.NextRequestAsync();
        }

        var delivery = await WaitForDeliveryStatusAsync(operatorToken, projectId, webhookId, "failed");
        Assert.Equal(3, delivery.GetProperty("attempts").GetInt32());
        Assert.Equal(500, delivery.GetProperty("lastStatusCode").GetInt32());

        var deliveryId = delivery.GetProperty("id").GetString()!;
        var attemptsResponse = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/webhooks/{webhookId}/deliveries/{deliveryId}", operatorToken)));
        Assert.Equal(3, attemptsResponse.GetProperty("attempts").GetArrayLength());
        Assert.All(attemptsResponse.GetProperty("attempts").EnumerateArray(),
            a => Assert.Equal(500, a.GetProperty("statusCode").GetInt32()));

        // DisableAfterConsecutiveFailures = 1: one terminally-failed delivery is enough to trip it —
        // loud (a console-visible reason), never a silent stop.
        var webhook = await WaitForWebhookDisabledAsync(operatorToken, projectId, webhookId);
        Assert.False(webhook.GetProperty("enabled").GetBoolean());
        Assert.Contains("disabled automatically", webhook.GetProperty("disabledReason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_url_and_empty_events_are_refused_at_creation()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/webhooks", operatorToken,
            new { name = "bad", url = "not-a-url", events = Array.Empty<string>() }));
        var body = await AssertError(response, 400, "general_argument_invalid");
        Assert.NotEmpty(body.GetProperty("fields").GetProperty("url").EnumerateArray());
        Assert.NotEmpty(body.GetProperty("fields").GetProperty("events").EnumerateArray());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<(string Id, string Secret)> CreateWebhookAsync(
        string operatorToken, string projectId, string url, string[] events)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/webhooks", operatorToken,
            new { name = "test webhook", url, events }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return (body.GetProperty("webhook").GetProperty("id").GetString()!, body.GetProperty("secret").GetString()!);
    }

    private async Task<JsonElement> WaitForDeliveryStatusAsync(
        string operatorToken, string projectId, string webhookId, string targetStatus, string? deliveryId = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/webhooks/{webhookId}/deliveries", operatorToken)));
            var deliveries = list.GetProperty("deliveries").EnumerateArray();
            var match = deliveryId is null
                ? deliveries.FirstOrDefault()
                : deliveries.FirstOrDefault(d => d.GetProperty("id").GetString() == deliveryId);
            if (match.ValueKind == JsonValueKind.Object && match.GetProperty("status").GetString() == targetStatus)
                return match;
            await Task.Delay(150);
        }
        throw new TimeoutException($"No delivery reached status '{targetStatus}' in time.");
    }

    private async Task<JsonElement> WaitForWebhookDisabledAsync(string operatorToken, string projectId, string webhookId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var webhook = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/webhooks/{webhookId}", operatorToken)));
            if (!webhook.GetProperty("enabled").GetBoolean())
                return webhook;
            await Task.Delay(150);
        }
        throw new TimeoutException("Webhook was not auto-disabled in time.");
    }

    private async Task<(string DatabaseId, string TableId)> CreateTableWithColumnsAsync(string projectId, string apiKey)
    {
        var database = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            "/v1/databases", projectId, apiKey: apiKey, body: new { key = "blog", name = "Blog" })));
        var databaseId = database.GetProperty("id").GetString()!;
        var table = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables", projectId, apiKey: apiKey, body: new { key = "posts", name = "Posts" })));
        var tableId = table.GetProperty("id").GetString()!;

        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/string", projectId, apiKey: apiKey,
            body: new { key = "title", size = 200, required = true }));
        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/integer", projectId, apiKey: apiKey,
            body: new { key = "views" }));
        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/boolean", projectId, apiKey: apiKey,
            body: new { key = "published" }));

        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { permissions = new[] { "create(\"any\")", "read(\"any\")" } }));

        return (databaseId, tableId);
    }

    private async Task<JsonElement> CreateRowAsync(
        string projectId, string apiKey, string databaseId, string tableId, object body)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey, body: body));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }

    /// <summary>A real loopback HTTP listener the delivery worker's real HttpClient connects to — no ASP.NET Core in-memory transport involved.</summary>
    private sealed class EchoListener : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly int _responseStatus;
        private readonly Task _loop;
        private readonly System.Threading.Channels.Channel<(NameValueCollection Headers, string Body)> _requests =
            System.Threading.Channels.Channel.CreateUnbounded<(NameValueCollection, string)>();

        public string Url { get; }

        public EchoListener(int responseStatus = 200)
        {
            _responseStatus = responseStatus;
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/webhook";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(RunAsync);
        }

        public async Task<(NameValueCollection Headers, string Body)> NextRequestAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await _requests.Reader.ReadAsync(cts.Token);
        }

        private async Task RunAsync()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    break; // listener stopped/disposed
                }

                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                await _requests.Writer.WriteAsync((ctx.Request.Headers, body));
                ctx.Response.StatusCode = _responseStatus;
                ctx.Response.Close();
            }
        }

        private static int GetFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                await _loop;
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
