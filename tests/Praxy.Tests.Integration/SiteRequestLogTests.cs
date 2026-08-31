using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// docs/handoff/sites-request-logs-prompt.md's owner test, automated: a handful of real proxied
/// requests through <see cref="Praxy.Sites.SiteProxyMiddleware"/> produce matching
/// <c>praxy.site_requests</c> rows (written asynchronously off <see cref="Praxy.Sites.SiteRequestLogWriter"/>'s
/// channel by <see cref="Praxy.Sites.SiteRequestLogWorker"/>, so this polls the read API rather than
/// asserting immediately after the request completes), and the retention sweep prunes rows past its
/// configured age the same way <c>RetentionTests</c> proves for the other three tables.
/// </summary>
public class SiteRequestLogTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Sites:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Sites:BuildTimeoutSeconds"] = "120",
        ["Praxy:Sites:ReconcileIntervalSeconds"] = "3600",
        ["Praxy:Retention:SweepIntervalSeconds"] = "1",
        ["Praxy:Retention:SiteRequestsMaxAgeDays"] = "30",
    };

    /// <summary>Same reasoning as SiteTests'/SiteBuildCachingTests' own overrides — a site's container is deliberately left running when api shuts down.</summary>
    public override async Task DisposeAsync()
    {
        var containerIds = new HashSet<string>();
        await using (var conn = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT container_id FROM praxy.site_deployments WHERE container_id IS NOT NULL", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                containerIds.Add(reader.GetString(0));
        }
        if (containerIds.Count > 0)
        {
            var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();
            foreach (var containerId in containerIds)
                await docker.StopAndRemoveAsync(containerId, CancellationToken.None);
        }
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Proxied_requests_produce_matching_site_request_log_rows()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "log-test");
        var hostname = $"log-test.{projectId}.sites.localhost";

        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildAppTar());
        await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, deploymentId, "ready");
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId);

        await GetSiteBodyAsync(hostname, "/");
        await GetSiteBodyAsync(hostname, "/missing");

        var rows = await WaitForRequestLogRowsAsync(operatorToken, projectId, siteId, 2);

        Assert.All(rows, r => Assert.Equal("GET", r.GetProperty("method").GetString()));
        Assert.Contains(rows, r => r.GetProperty("path").GetString() == "/" && r.GetProperty("statusCode").GetInt32() == 200);
        Assert.Contains(rows, r => r.GetProperty("path").GetString() == "/missing" && r.GetProperty("statusCode").GetInt32() == 404);
        Assert.All(rows, r => Assert.True(r.GetProperty("durationMs").GetInt32() >= 0));
    }

    [Fact]
    public async Task Old_site_request_rows_are_pruned_by_retention_a_recent_one_is_not()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "retention-test");
        var oldId = Guid.NewGuid();
        var recentId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
            db.SiteRequestLogs.AddRange(
                new SiteRequestLog
                {
                    Id = oldId, SiteId = Guid.Parse(siteId), ProjectId = projectId, Method = "GET", Path = "/old",
                    StatusCode = 200, DurationMs = 5, CreatedAt = DateTimeOffset.UtcNow.AddDays(-31),
                },
                new SiteRequestLog
                {
                    Id = recentId, SiteId = Guid.Parse(siteId), ProjectId = projectId, Method = "GET", Path = "/recent",
                    StatusCode = 200, DurationMs = 5, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                });
            await db.SaveChangesAsync();
        }

        await WaitUntilAsync(async () => !await SiteRequestLogExistsAsync(oldId));

        Assert.True(await SiteRequestLogExistsAsync(recentId));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<bool> SiteRequestLogExistsAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        return await db.SiteRequestLogs.AnyAsync(r => r.Id == id);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(500);
        }
        throw new TimeoutException("Retention sweep did not delete the expected row in time.");
    }

    private async Task<string> CreateSiteAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites", operatorToken, new { key, name = key, rootDirectory = "" }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> UploadDeploymentAsync(string operatorToken, string projectId, string siteId, byte[] tar)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites/{siteId}/deployments")
        {
            Content = new ByteArrayContent(tar),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        request.Headers.Add("X-Praxy-Session", operatorToken);
        var response = await Client.SendAsync(request);
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> WaitForDeploymentStatusAsync(
        string operatorToken, string projectId, string siteId, string deploymentId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{deploymentId}", operatorToken)));
            var status = deployment.GetProperty("status").GetString();
            if (status == targetStatus || status == "failed")
                return deployment;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Deployment never reached status '{targetStatus}'.");
    }

    /// <summary>Waits until the site is actually serving its active deployment — "ready" alone only means "buildable", not "a container is up and reachable".</summary>
    private async Task WaitForSiteActiveAsync(string operatorToken, string projectId, string siteId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var site = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken)));
            if (site.TryGetProperty("activeDeploymentId", out _) && site.GetProperty("isRunning").GetBoolean())
                return;
            await Task.Delay(500);
        }
        throw new TimeoutException("Site never became active and running.");
    }

    private async Task<string> GetSiteBodyAsync(string hostname, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Host = hostname } };
        var response = await Client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Polls the console's own read API — proves the write path (channel -> SiteRequestLogWorker -> DB) end to end, not just that a row eventually exists.</summary>
    private async Task<List<JsonElement>> WaitForRequestLogRowsAsync(
        string operatorToken, string projectId, string siteId, int expectedCount)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var body = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}/requests", operatorToken)));
            var requests = body.GetProperty("requests").EnumerateArray().ToList();
            if (requests.Count >= expectedCount)
                return requests;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Expected at least {expectedCount} site request log row(s) to appear.");
    }

    /// <summary>A minimal, real, respondable site — no actual npm dependencies needed to exercise the proxy leg.</summary>
    private static byte[] BuildAppTar()
    {
        const string packageJson = """
            {
              "name": "log-test-site",
              "version": "1.0.0",
              "scripts": { "build": "mkdir -p .next/standalone .next/static public && cp server.js .next/standalone/server.js" }
            }
            """;
        const string serverJs = """
            require('http').createServer((req, res) => {
              res.statusCode = req.url === '/' ? 200 : 404;
              res.end('ok');
            }).listen(process.env.PORT || 3000, process.env.HOSTNAME || '0.0.0.0');
            """;
        return BuildRawTar(("package.json", packageJson), ("server.js", serverJs));
    }

    private static byte[] BuildRawTar(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                };
                writer.WriteEntry(entry);
            }
        }
        return stream.ToArray();
    }
}
