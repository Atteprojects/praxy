using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Same reasoning as SiteTests' own override — a site's container is deliberately left running when api shuts down, so this test cleans up after itself explicitly.</summary>
    public override async Task DisposeAsync()
    {
        await CleanUpSiteContainersAsync();
        await base.DisposeAsync();
    }

    private async Task CleanUpSiteContainersAsync()
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

        // The DB column above only ever holds an *activated* deployment's container — preview
        // containers (SiteContainerRegistry.StartOrJoinAsync) never get written there, so a test
        // that only ever previews a deployment (never activates it) would otherwise leak its
        // container past this cleanup.
        var registry = Factory.Services.GetRequiredService<Praxy.Sites.SiteContainerRegistry>();
        foreach (var deploymentId in registry.TrackedDeploymentIds())
            if (registry.TryGet(deploymentId, out var container))
                containerIds.Add(container.ContainerId);

        if (containerIds.Count == 0)
            return;

        var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();
        foreach (var containerId in containerIds)
            await docker.StopAndRemoveAsync(containerId, CancellationToken.None);
    }

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

    [Fact]
    public async Task A_ready_deployments_3_label_preview_hostname_is_allowed_even_when_not_active()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");

        // v1 builds and auto-activates; v2 builds afterward and auto-activates too, superseding
        // v1 — SiteBuildWorker always auto-activates the freshest successful build (Phase 1's own
        // behavior, unchanged), so v1 is now "ready, but no longer active," exactly the preview
        // shape being asserted here (the same state SiteTests' rollback scenario relies on).
        var v1 = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildFakeServerTar());
        await WaitForSiteRunningAsync(operatorToken, projectId, siteId, v1);
        var v2 = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildFakeServerTar());
        await WaitForSiteRunningAsync(operatorToken, projectId, siteId, v2);

        var previewHostname = $"{v1}.blog.{projectId}.sites.localhost";
        await AssertAskTls(previewHostname, expectAllowed: true);

        // A made-up deployment id under a real site/key still gets rejected — the allow-list checks
        // the deployment actually exists and belongs to this site, not just the hostname's shape.
        var fakeDeploymentId = Guid.NewGuid().ToString("n");
        await AssertAskTls($"{fakeDeploymentId}.blog.{projectId}.sites.localhost", expectAllowed: false);

        // Disabling the site rejects its previews too, not just its production hostname — a site
        // being taken offline must go dark everywhere, previews included.
        var disable = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken, new { enabled = false }));
        Assert.Equal(200, (int)disable.StatusCode);
        await AssertAskTls(previewHostname, expectAllowed: false);
    }

    [Fact]
    public async Task An_unregistered_custom_domain_is_rejected_even_though_it_parses_as_a_real_hostname()
    {
        var (_, _) = await SetupProjectAsync();
        // Shaped exactly like a real public domain (unlike the "not-even-shaped-like-a-site.example.com"
        // case above, which is really testing SiteHostPattern's own rejection) — nothing in
        // site_domains claims it, so the custom-domain fallback must reject it too.
        await AssertAskTls("nobody-registered-this.example.test", expectAllowed: false);
    }

    [Fact]
    public async Task A_custom_domain_is_allowed_once_pending_then_rejected_once_its_site_is_disabled()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildFakeServerTar());
        await WaitForSiteRunningAsync(operatorToken, projectId, siteId, deploymentId);

        var hostname = "myapp.example.test";
        var domain = await AddDomainAsync(operatorToken, projectId, siteId, hostname);
        Assert.Equal("pending", domain.GetProperty("status").GetString());
        // _ask-tls allowing a still-pending domain is the whole point — Caddy calls this *before*
        // issuance, so "pending" must not block the very first attempt.
        await AssertAskTls(hostname, expectAllowed: true);

        var disable = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken, new { enabled = false }));
        Assert.Equal(200, (int)disable.StatusCode);
        await AssertAskTls(hostname, expectAllowed: false);
    }

    [Fact]
    public async Task A_custom_domain_belonging_to_another_site_does_not_leak_through_that_sites_state()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var ownerSiteId = await CreateSiteAsync(operatorToken, projectId, "owner");
        var otherSiteId = await CreateSiteAsync(operatorToken, projectId, "other");

        var ownerDeployment = await UploadDeploymentAsync(operatorToken, projectId, ownerSiteId, BuildFakeServerTar());
        await WaitForSiteRunningAsync(operatorToken, projectId, ownerSiteId, ownerDeployment);
        var otherDeployment = await UploadDeploymentAsync(operatorToken, projectId, otherSiteId, BuildFakeServerTar());
        await WaitForSiteRunningAsync(operatorToken, projectId, otherSiteId, otherDeployment);

        var hostname = "leaktest.example.test";
        await AddDomainAsync(operatorToken, projectId, ownerSiteId, hostname);
        // "other" is a real, enabled, fully-deployed sibling site in the same project — the lookup
        // must resolve strictly through the domain's own site_id, not "any enabled site nearby."
        await AssertAskTls(hostname, expectAllowed: true);

        var disableOwner = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/sites/{ownerSiteId}", operatorToken, new { enabled = false }));
        Assert.Equal(200, (int)disableOwner.StatusCode);
        // Disabling the domain's real owner rejects it, even though "other" stays enabled right next
        // to it — proves the check didn't accidentally key off some other site in the project.
        await AssertAskTls(hostname, expectAllowed: false);
    }

    [Fact]
    public async Task Custom_domain_matching_is_case_insensitive()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildFakeServerTar());
        await WaitForSiteRunningAsync(operatorToken, projectId, siteId, deploymentId);

        await AddDomainAsync(operatorToken, projectId, siteId, "MixedCase.Example.Test");
        await AssertAskTls("mixedcase.example.test", expectAllowed: true);
        await AssertAskTls("MIXEDCASE.EXAMPLE.TEST", expectAllowed: true);
    }

    private async Task<JsonElement> AddDomainAsync(string operatorToken, string projectId, string siteId, string hostname)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites/{siteId}/domains", operatorToken, new { hostname }));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
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
