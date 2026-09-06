using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// security-review-phase-3, Finding A: <see cref="Praxy.Sites.SiteProxyMiddleware"/>'s preview branch
/// resolved a hostname's <c>deploymentRef</c> label against <see cref="Praxy.Sites.SiteContainerRegistry"/>
/// — a single, unpartitioned, process-wide map of every project's live production and warm preview
/// containers — and only verified the deployment actually belonged to the hostname's own site on a
/// registry <em>miss</em>. A registry <em>hit</em> (the common case: any deployment that is currently
/// running, which for a production deployment is effectively always) skipped that check entirely, so
/// naming a different project's deployment id in an otherwise-ordinary, self-owned preview hostname
/// forwarded that other project's live traffic to the caller. No malformed input, no crafted deployment
/// id — the attack is an ordinary preview URL with someone else's real, already-known deployment id in
/// the label a caller fully controls.
///
/// The one trusted operator running an instance today can create as many projects as they like
/// (staging/prod separation, multiple clients, …), so this same request shape is reachable without any
/// second developer at all — this is not a multitenancy-only concern.
/// </summary>
public class SiteProxyIsolationTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Sites:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Sites:BuildTimeoutSeconds"] = "60",
        ["Praxy:Sites:ReconcileIntervalSeconds"] = "3600",
    };

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

        var registry = Factory.Services.GetRequiredService<Praxy.Sites.SiteContainerRegistry>();
        foreach (var container in registry.AllContainers())
            containerIds.Add(container.ContainerId);

        if (containerIds.Count == 0)
            return;

        var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();
        foreach (var containerId in containerIds)
            await docker.StopAndRemoveAsync(containerId, CancellationToken.None);
    }

    [Fact]
    public async Task A_forged_preview_hostname_naming_another_projects_own_deployment_id_does_not_reach_its_container()
    {
        // One ordinary trusted operator, two ordinary projects — exactly the shape this phase's own
        // guidance asks for over a crafted-looking attack.
        var (operatorToken, victimProjectId) = await SetupProjectAsync("Victim");
        var attackerProjectId = await CreateProjectAsync(operatorToken, "Attacker");

        var victimSiteId = await CreateSiteAsync(operatorToken, victimProjectId, "victim-site");
        var victimDeploymentId = await UploadDeploymentAsync(
            operatorToken, victimProjectId, victimSiteId, BuildFakeServerTar("victim-secret-payload"));
        await WaitForSiteRunningAsync(operatorToken, victimProjectId, victimSiteId, victimDeploymentId);

        // The attacker's own site needs no deployment at all — SiteProxyMiddleware only requires it
        // to exist, belong to their project, and be enabled (the default), before it ever looks at
        // the registry.
        await CreateSiteAsync(operatorToken, attackerProjectId, "attacker-site");

        // A hostname shaped exactly like the attacker's own preview URL, except the deployment label
        // names the victim's real, already-active deployment id instead of one of the attacker's own.
        var forgedHost = $"{victimDeploymentId}.attacker-site.{attackerProjectId}.sites.localhost";
        var request = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = forgedHost } };
        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("victim-secret-payload", body);
        Assert.NotEqual(200, (int)response.StatusCode);

        // Confirm this isn't merely a fluke of the victim's specific hostname shape — the attacker's
        // *own* preview of their *own* site still works once they actually deploy one, proving the fix
        // rejects only the cross-site case, not preview requests generally.
        var attackerSiteId2 = await CreateSiteAsync(operatorToken, attackerProjectId, "attacker-site-2");
        var attackerDeploymentId = await UploadDeploymentAsync(
            operatorToken, attackerProjectId, attackerSiteId2, BuildFakeServerTar("attacker-own-content"));
        await WaitForSiteRunningAsync(operatorToken, attackerProjectId, attackerSiteId2, attackerDeploymentId);
        var ownPreviewHost = $"{attackerDeploymentId}.attacker-site-2.{attackerProjectId}.sites.localhost";
        var ownPreviewResponse = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = ownPreviewHost } });
        Assert.Equal(200, (int)ownPreviewResponse.StatusCode);
        Assert.Equal("attacker-own-content", await ownPreviewResponse.Content.ReadAsStringAsync());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> CreateProjectAsync(string operatorToken, string name)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", operatorToken, new { name }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
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

    /// <summary>No real Next.js: a package.json whose "build" script hand-writes a standalone-shaped
    /// output directly, and a fixed, caller-chosen body — mirrors SitesAskTlsTests'/SiteCustomDomainTests'
    /// own fake server, parameterized so each side of the isolation test has recognizable content.</summary>
    private static byte[] BuildFakeServerTar(string body)
    {
        var serverJs = $$"""
            require('http').createServer((req, res) => { res.end('{{body}}'); })
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
