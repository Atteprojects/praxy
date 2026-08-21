using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The <c>_ask-tls</c> allow-list Caddy's on-demand TLS depends on for cert issuance — a security
/// boundary, not a formality (docs/handoff/sites-phase-1-prompt.md's own framing). Deliberately
/// exercises a real build/activate cycle for the "real active site" case rather than seeding the DB
/// directly: the endpoint's actual query (site enabled, has an active deployment, that deployment is
/// <c>ready</c>) should only pass once the real pipeline reaches that state, not because a test wrote
/// the right row by hand. The deployment itself is a fake minimal HTTP server, not real Next.js — this
/// suite is about the allow-list, not the build; <see cref="SiteTests"/> covers a genuine Next.js app.
/// </summary>
public class SitesAskTlsTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Sites:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Sites:BuildTimeoutSeconds"] = "60",
        ["Praxy:Sites:ReconcileIntervalSeconds"] = "3600",
    };

    [Fact]
    public async Task Made_up_hostnames_are_rejected_before_ever_touching_the_database()
    {
        var (_, projectId) = await SetupProjectAsync();
        await AssertAskTls($"nope.{projectId}.sites.localhost", expectAllowed: false);
        await AssertAskTls("not-even-shaped-like-a-site.example.com", expectAllowed: false);
        await AssertAskTls("", expectAllowed: false);
    }

    [Fact]
    public async Task A_site_with_no_deployment_yet_is_rejected()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await CreateSiteAsync(operatorToken, projectId, "empty");
        await AssertAskTls($"empty.{projectId}.sites.localhost", expectAllowed: false);
    }

    [Fact]
    public async Task A_real_enabled_deployed_site_is_allowed_and_a_disabled_one_is_rejected_again()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        var hostname = $"blog.{projectId}.sites.localhost";

        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildFakeServerTar());
        // Wait for the SITE to actually be running the deployment, not just the deployment row
        // reaching 'ready' — that status flips before SiteBuildWorker's auto-activate call (which
        // starts the real container) has necessarily finished, and _ask-tls should correctly say
        // no during that brief window too.
        await WaitForSiteRunningAsync(operatorToken, projectId, siteId, deploymentId);
        await AssertAskTls(hostname, expectAllowed: true);

        var disable = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken, new { enabled = false }));
        Assert.Equal(200, (int)disable.StatusCode);
        await AssertAskTls(hostname, expectAllowed: false);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task AssertAskTls(string domain, bool expectAllowed)
    {
        var response = await Client.GetAsync($"/v1/sites/_ask-tls?domain={Uri.EscapeDataString(domain)}");
        Assert.Equal(expectAllowed ? 204 : 404, (int)response.StatusCode);
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

    /// <summary>
    /// No real Next.js: a package.json with no dependencies whose "build" script hand-writes a
    /// standalone-shaped output directly. Reaches the same 'ready'-and-serving state real content
    /// would, in a fraction of the time — this suite only needs a genuinely active deployment to
    /// exist, not to prove the Next.js pipeline itself.
    /// </summary>
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
