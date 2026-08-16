using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Raw WebSocket harness against the Testcontainers-backed API host (phase-4-prompt.md): every
/// connection here authenticates via a minted ticket rather than cookies/headers, both because
/// that is exactly what a ticket is for and because <see cref="Microsoft.AspNetCore.TestHost.WebSocketClient"/>
/// has no API to attach custom headers before the handshake.
/// </summary>
public class RealtimeTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromMilliseconds(800);

    [Fact]
    public async Task Connect_subscribe_ping_round_trip()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        using var socket = await ConnectGuestAsync(projectId);

        await SendAsync(socket, new { type = "ping" });
        var pong = await ReceiveMessageAsync(socket);
        Assert.Equal("pong", pong.GetProperty("type").GetString());

        await SendAsync(socket, new
        {
            type = "subscribe",
            data = new[] { new { subscriptionId = "s1", channels = new[] { $"databases.{databaseId}.tables.{tableId}.rows" } } },
        });
        var response = await ReceiveMessageAsync(socket);
        Assert.Equal("response", response.GetProperty("type").GetString());
        Assert.Equal("s1", response.GetProperty("data").GetProperty("subscriptions")[0].GetString());
    }

    [Fact]
    public async Task A_row_write_is_delivered_to_a_subscriber_who_can_read_it()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        using var socket = await ConnectGuestAsync(projectId); // table grants read("any")
        await SubscribeAsync(socket, "s1", $"databases.{databaseId}.tables.{tableId}.rows");

        var row = await CreateRowAsync(projectId, apiKey, databaseId, tableId,
            new { data = new { title = "Hello", views = 1, published = false } });
        var rowId = row.GetProperty("$id").GetString()!;

        var evt = await ReceiveMessageAsync(socket);
        Assert.Equal("event", evt.GetProperty("type").GetString());
        var data = evt.GetProperty("data");
        Assert.Contains("s1", data.GetProperty("subscriptions").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(rowId, data.GetProperty("payload").GetProperty("rowId").GetString());
        Assert.Contains($"databases.{databaseId}.tables.{tableId}.rows.{rowId}.create",
            data.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_subscriber_who_cannot_read_the_table_receives_nothing()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var (tokenA, userA) = await SignupAsync(projectId, "a@example.com");
        var (tokenB, _) = await SignupAsync(projectId, "b@example.com");
        var wireA = userA.GetProperty("id").GetString()!;

        // Only A can read at the table level; B (and guests) cannot.
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { permissions = new[] { "create(\"users\")", $"read(\"user:{wireA}\")" } }));

        using var socketA = await ConnectWithTicketAsync(projectId, tokenA);
        using var socketB = await ConnectWithTicketAsync(projectId, tokenB);
        await SubscribeAsync(socketA, "sa", $"databases.{databaseId}.tables.{tableId}.rows");
        await SubscribeAsync(socketB, "sb", $"databases.{databaseId}.tables.{tableId}.rows");

        await CreateRowAsync(projectId, tokenA, null, databaseId, tableId,
            new { data = new { title = "A's row", views = 0, published = false } });

        var evt = await ReceiveMessageAsync(socketA);
        Assert.Equal("event", evt.GetProperty("type").GetString());

        await AssertSilentAsync(socketB);
    }

    [Fact]
    public async Task Subscribing_to_a_specific_row_channel_delivers_only_that_rows_events()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var row1 = await CreateRowAsync(projectId, apiKey, databaseId, tableId,
            new { data = new { title = "row1", views = 0, published = false } });
        var row1Id = row1.GetProperty("$id").GetString()!;

        using var socket = await ConnectGuestAsync(projectId);
        await SubscribeAsync(socket, "s1", $"databases.{databaseId}.tables.{tableId}.rows.{row1Id}");

        // A different row's create must not arrive on this channel.
        await CreateRowAsync(projectId, apiKey, databaseId, tableId,
            new { data = new { title = "row2", views = 0, published = false } });
        await AssertSilentAsync(socket);

        // An update to the subscribed row does.
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{row1Id}", projectId, apiKey: apiKey,
            body: new { data = new { views = 1 } }));
        var evt = await ReceiveMessageAsync(socket);
        Assert.Equal(row1Id, evt.GetProperty("data").GetProperty("payload").GetProperty("rowId").GetString());
    }

    [Fact]
    public async Task Revoking_the_sessions_closes_its_socket()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (token, user) = await SignupAsync(projectId, "closeme@example.com");
        var userId = user.GetProperty("id").GetString()!;
        var sessionId = token.Split('.')[0];

        using var socket = await ConnectWithTicketAsync(projectId, token);

        var revoke = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/projects/{projectId}/users/{userId}/sessions/{sessionId}", operatorToken));
        Assert.Equal(204, (int)revoke.StatusCode);

        using var cts = new CancellationTokenSource(ReceiveTimeout);
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
    }

    [Fact]
    public async Task An_unknown_project_is_rejected_before_the_upgrade()
    {
        var wsClient = Factory.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            wsClient.ConnectAsync(new Uri(Factory.Server.BaseAddress, "/v1/realtime?project=does-not-exist"), CancellationToken.None));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<WebSocket> ConnectGuestAsync(string projectId)
    {
        var wsClient = Factory.Server.CreateWebSocketClient();
        var socket = await wsClient.ConnectAsync(new Uri(Factory.Server.BaseAddress, $"/v1/realtime?project={projectId}"), CancellationToken.None);
        await ReceiveMessageAsync(socket); // "connected"
        return socket;
    }

    private async Task<WebSocket> ConnectWithTicketAsync(string projectId, string sessionToken)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/realtime/ticket", projectId, sessionToken: sessionToken));
        Assert.Equal(200, (int)response.StatusCode);
        var body = await ReadJson(response);
        var ticket = body.GetProperty("ticket").GetString();

        var wsClient = Factory.Server.CreateWebSocketClient();
        var socket = await wsClient.ConnectAsync(
            new Uri(Factory.Server.BaseAddress, $"/v1/realtime?project={projectId}&ticket={ticket}"), CancellationToken.None);
        await ReceiveMessageAsync(socket); // "connected"
        return socket;
    }

    private static async Task SubscribeAsync(WebSocket socket, string subscriptionId, params string[] channels)
    {
        await SendAsync(socket, new { type = "subscribe", data = new[] { new { subscriptionId, channels } } });
        var response = await ReceiveMessageAsync(socket);
        Assert.Equal("response", response.GetProperty("type").GetString());
    }

    private static async Task SendAsync(WebSocket socket, object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<JsonElement> ReceiveMessageAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        return doc.RootElement.Clone();
    }

    /// <summary>Asserts nothing arrives within <see cref="SilenceWindow"/> — deny-by-default is invisibility, not an error.</summary>
    private static async Task AssertSilentAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(SilenceWindow);
        var buffer = new byte[4096];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => socket.ReceiveAsync(buffer, cts.Token));
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
            body: new { permissions = new[] { "create(\"any\")", "read(\"any\")", "update(\"any\")", "delete(\"any\")" } }));

        return (databaseId, tableId);
    }

    private async Task<JsonElement> CreateRowAsync(string projectId, string apiKey, string databaseId, string tableId, object body)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey, body: body));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private async Task<JsonElement> CreateRowAsync(
        string projectId, string? sessionToken, string? apiKey, string databaseId, string tableId, object body)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, sessionToken: sessionToken, apiKey: apiKey, body: body));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }
}
