using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Praxy.LoadTests;

/// <summary>
/// Roadmap Phase 9: 10k WebSocket connections. Spread across <c>--projects</c> projects rather than
/// piled onto one, matching real multi-tenant usage and exercising <c>RealtimeOptions.MaxConnectionsPerProject</c>
/// (default 1000, architecture.md §6) as a genuine per-project quota rather than a single global cap
/// this test would otherwise have to raise. Connections are anonymous (<c>Guest</c> role,
/// architecture.md §4.3) — no session or API key setup needed, and the connect-time-resolved-roles
/// path (architecture.md §6: "roles resolved once at connect") is exercised identically either way.
/// Every connection sends one <c>ping</c> after connecting and must see its <c>pong</c> — proves the
/// sockets are live and processing messages, not just TCP-open and silently stuck.
/// </summary>
public static class WebSocketLoadTest
{
    public static async Task RunAsync(string endpoint, int projects, int connectionsPerProject, int rampConcurrency, string email, string password)
    {
        var total = projects * connectionsPerProject;
        Console.WriteLine($"WebSocket load test: {projects} projects x {connectionsPerProject} connections = {total} total, endpoint {endpoint}");

        using var api = new PraxyApi(endpoint);
        var operatorToken = await api.ClaimOrLoginAsync(email, password);

        var projectIds = new List<string>();
        for (var p = 0; p < projects; p++)
            projectIds.Add(await api.CreateProjectAsync(operatorToken, $"Load Test WS {p}", $"loadtest-ws-{p}-{Random.Shared.Next(100_000):x}"));
        Console.WriteLine($"Created {projectIds.Count} project(s).");

        var wsBase = endpoint.Replace("http://", "ws://").Replace("https://", "wss://");
        var sockets = new List<ClientWebSocket>();
        var connectMs = new List<double>();
        var pingMs = new List<double>();
        var connectFailures = 0;
        var pingFailures = 0;
        var gate = new SemaphoreSlim(rampConcurrency);
        var lockObj = new object();

        var overall = Stopwatch.StartNew();
        var tasks = projectIds.SelectMany(projectId => Enumerable.Range(0, connectionsPerProject)
            .Select(_ => ConnectOneAsync(projectId)));
        await Task.WhenAll(tasks);
        overall.Stop();

        async Task ConnectOneAsync(string projectId)
        {
            await gate.WaitAsync();
            var sw = Stopwatch.StartNew();
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri($"{wsBase}/v1/realtime?project={projectId}"), CancellationToken.None);
                var buffer = new byte[4096];
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None); // "connected" envelope
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new IOException("Expected a text 'connected' frame.");

                lock (lockObj) connectMs.Add(sw.Elapsed.TotalMilliseconds);
                lock (sockets) sockets.Add(socket);
            }
            catch
            {
                Interlocked.Increment(ref connectFailures);
                socket.Dispose();
            }
            finally
            {
                gate.Release();
            }
        }

        Timings.From(connectMs, connectFailures, overall.Elapsed.TotalSeconds).Print("WebSocket connect");
        Console.WriteLine($"{sockets.Count} sockets held open concurrently.");

        // Liveness: every held-open socket round-trips a ping/pong — proves they're processing
        // messages under load, not just sitting there as open file descriptors.
        var pingGate = new SemaphoreSlim(rampConcurrency);
        var pingSw = Stopwatch.StartNew();
        await Task.WhenAll(sockets.Select(async socket =>
        {
            await pingGate.WaitAsync();
            try
            {
                var sw = Stopwatch.StartNew();
                var ping = Encoding.UTF8.GetBytes("""{"type":"ping"}""");
                await socket.SendAsync(ping, WebSocketMessageType.Text, true, CancellationToken.None);
                var buffer = new byte[4096];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await socket.ReceiveAsync(buffer, cts.Token);
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.GetProperty("type").GetString() != "pong")
                    throw new IOException($"Expected pong, got: {text}");
                lock (lockObj) pingMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch
            {
                Interlocked.Increment(ref pingFailures);
            }
            finally
            {
                pingGate.Release();
            }
        }));
        Timings.From(pingMs, pingFailures, pingSw.Elapsed.TotalSeconds).Print("ping -> pong under load");

        Console.WriteLine("Closing all sockets...");
        await Task.WhenAll(sockets.Select(async socket =>
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "load test done", CancellationToken.None); }
            catch { /* best-effort */ }
            finally { socket.Dispose(); }
        }));
        Console.WriteLine("Done.");
    }
}
