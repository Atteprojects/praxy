using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Sites Phase 3's own owner-test flow, end to end through the real
/// <see cref="Praxy.Sites.SiteProxyMiddleware"/> — a registered custom domain starts <c>pending</c>,
/// stays that way through an allowed <c>_ask-tls</c> ask (which only ever means "you may attempt
/// issuance," per that endpoint's own remarks), reaches the site's real active deployment once
/// proxied, and only <em>then</em> flips to <c>verified</c> — proving the flip genuinely waits for a
/// successful proxied request rather than happening at ask-time. <see cref="SitesAskTlsTests"/> covers
/// the allow-list's own strictness (disabled sites, cross-site leakage, case-insensitivity); this suite
/// is about the custom-domain proxy path and the verification lifecycle. No real Next.js needed here
/// either — same fake minimal HTTP server <see cref="SitesAskTlsTests"/> uses, since this suite isn't
/// exercising the build pipeline.
/// </summary>
public class SiteCustomDomainTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Sites:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Sites:BuildTimeoutSeconds"] = "60",
        ["Praxy:Sites:ReconcileIntervalSeconds"] = "3600",
    };

    /// <summary>Same reasoning as SiteTests' and SitesAskTlsTests' own overrides — a site's container is deliberately left running when api shuts down, so this test cleans up after itself explicitly.</summary>
    public override async Task DisposeAsync()
    {
        await CleanUpSiteContainersAsync();
        await base.DisposeAsync();
    }

    private async Task CleanUpSiteContainersAsync()
    {
        List<string> containerIds;
        await using (var conn = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT container_id FROM praxy.site_deployments WHERE container_id IS NOT NULL", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            containerIds = [];
            while (await reader.ReadAsync())
                containerIds.Add(reader.GetString(0));
        }
        if (containerIds.Count == 0)
            return;

        var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();
        foreach (var containerId in containerIds)
            await docker.StopAndRemoveAsync(containerId, CancellationToken.None);
    }

    [Fact]
    public async Task A_custom_domain_reaches_the_active_deployment_and_only_flips_to_verified_after_that()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildFakeServerTar());
        await WaitForSiteRunningAsync(operatorToken, projectId, siteId, deploymentId);

        var hostname = "custom-domain-test.example.test";
        var domainId = (await AddDomainAsync(operatorToken, projectId, siteId, hostname))
            .GetProperty("id").GetString()!;
        Assert.Equal("pending", await StatusOfAsync(operatorToken, projectId, siteId, domainId));

        // _ask-tls allowing the attempt must not, on its own, flip the row — that would record "we
        // allowed an attempt," not "this domain actually proved control" (the whole point of doing the
        // flip in the proxy middleware instead of _ask-tls).
        var askResponse = await Client.GetAsync($"/v1/sites/_ask-tls?domain={Uri.EscapeDataString(hostname)}");
        Assert.Equal(204, (int)askResponse.StatusCode);
        Assert.Equal("pending", await StatusOfAsync(operatorToken, projectId, siteId, domainId));

        // A real proxied request reaches the site's active deployment (the fake server's fixed body)...
        var body = await GetBodyForHostAsync(hostname);
        Assert.Equal("ok", body);

        // ...and only now does the row flip to verified, with a timestamp.
        var afterList = await ListDomainsAsync(operatorToken, projectId, siteId);
        var afterDomain = afterList.Single(d => d.GetProperty("id").GetString() == domainId);
        Assert.Equal("verified", afterDomain.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, afterDomain.GetProperty("verifiedAt").ValueKind);

        // A second request keeps serving correctly (the pending -> verified update isn't a one-shot
        // that breaks subsequent traffic).
        Assert.Equal("ok", await GetBodyForHostAsync(hostname));
    }

    [Fact]
    public async Task Removing_a_custom_domain_stops_it_resolving_to_the_site()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildFakeServerTar());
        await WaitForSiteRunningAsync(operatorToken, projectId, siteId, deploymentId);

        var hostname = "remove-me.example.test";
        var domainId = (await AddDomainAsync(operatorToken, projectId, siteId, hostname))
            .GetProperty("id").GetString()!;
        Assert.Equal("ok", await GetBodyForHostAsync(hostname));

        var delete = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/projects/{projectId}/sites/{siteId}/domains/{domainId}", operatorToken));
        Assert.Equal(204, (int)delete.StatusCode);

        // SiteProxyMiddleware no longer recognizes this Host at all once the row is gone, so the
        // request falls through to `next(ctx)` — which, in this test host, is the console app's own
        // SPA fallback (a 200 with the console's index.html for any unmatched path, unrelated to
        // Sites). What matters here is that it's provably not the site's own container answering
        // anymore, not the exact status code the console's fallback happens to return.
        var request = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = hostname } };
        var response = await Client.SendAsync(request);
        Assert.NotEqual("ok", await response.Content.ReadAsStringAsync());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> StatusOfAsync(string operatorToken, string projectId, string siteId, string domainId)
    {
        var list = await ListDomainsAsync(operatorToken, projectId, siteId);
        return list.Single(d => d.GetProperty("id").GetString() == domainId).GetProperty("status").GetString()!;
    }

    private async Task<List<JsonElement>> ListDomainsAsync(string operatorToken, string projectId, string siteId)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/sites/{siteId}/domains", operatorToken));
        Assert.Equal(200, (int)response.StatusCode);
        var body = await ReadJson(response);
        return [.. body.GetProperty("domains").EnumerateArray()];
    }

    private async Task<JsonElement> AddDomainAsync(string operatorToken, string projectId, string siteId, string hostname)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites/{siteId}/domains", operatorToken, new { hostname }));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
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

    private async Task WaitForSiteRunningAsync(string operatorToken, string projectId, string siteId, string deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{deploymentId}", operatorToken)));
            Assert.NotEqual("failed", deployment.GetProperty("status").GetString());

            var site = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken)));
            if (site.TryGetProperty("activeDeploymentId", out var active) && active.GetString() == deploymentId
                && site.GetProperty("isRunning").GetBoolean())
                return;

            await Task.Delay(500);
        }
        throw new TimeoutException("Site never became active and running for the deployment.");
    }

    /// <summary>Sends a request through <see cref="Praxy.Sites.SiteProxyMiddleware"/> by setting the Host header to an arbitrary hostname — the custom-domain path never requires it to be shaped like the built-in wildcard pattern.</summary>
    private async Task<string> GetBodyForHostAsync(string hostname)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = hostname } };
        var response = await Client.SendAsync(request);
        Assert.Equal(200, (int)response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>No real Next.js: a package.json with no dependencies whose "build" script hand-writes a standalone-shaped output directly, and a fixed "ok" body — mirrors SitesAskTlsTests' own fake server exactly.</summary>
    private static byte[] BuildFakeServerTar()
    {
        var serverJs = """
            require('http').createServer((req, res) => { res.end('ok'); })
              .listen(process.env.PORT || 3000, process.env.HOSTNAME || '0.0.0.0');
            """;
        var buildScript = "mkdir -p .next/standalone .next/static public && cp server.js .next/standalone/server.js";
        var packageJson = $$"""
            { "name": "fake-site", "version": "1.0.0", "scripts": { "build": "{{buildScript}}" } }
            """;

        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { ("package.json", packageJson), ("server.js", serverJs) })
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
