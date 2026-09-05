using System.Formats.Tar;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// security-review-phase-1 follow-up (findings B/F): every site container now runs with
/// <c>ReadonlyRootfs</c> and writes only into size-capped tmpfs mounts, which is the portable
/// stand-in for the per-container disk quota Docker cannot give us.
///
/// <para>Phase 1 deferred <c>ReadonlyRootfs</c> for Sites for a specific, correct reason: it tested
/// a minimal Next.js app with a single <c>/tmp</c> tmpfs, which "exercises neither ISR (revalidate)
/// nor the image optimizer, both of which write to <c>.next/cache</c> at runtime." That is exactly
/// the gap this closes — the app below writes to <c>.next/cache</c> through Next.js's own ISR
/// machinery <em>and</em> probes the three paths directly from an API route, so a missing mount
/// fails loudly here rather than in production the first time a page revalidates.</para>
/// </summary>
public class SiteReadonlyRootfsTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Sites:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Sites:BuildTimeoutSeconds"] = "300",
        ["Praxy:Sites:ReconcileIntervalSeconds"] = "3600",
    };

    public override async Task DisposeAsync()
    {
        await CleanUpSiteContainersAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_site_writes_its_runtime_caches_but_cannot_write_to_the_host_disk()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "rofs");
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildProbeAppTar());
        var deployment = await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, deploymentId, "ready");
        Assert.Equal("ready", deployment.GetProperty("status").GetString());
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, deploymentId);

        var hostname = $"rofs.{projectId}.sites.localhost";

        // 1. The three paths, probed from inside the container by Next.js itself.
        var probe = JsonDocument.Parse(await GetAsync(hostname, "/api/probe")).RootElement;

        // The rootfs is read-only: a write to the app directory is refused by the kernel, so a
        // runaway site cannot fill the disk Postgres and every other container share.
        Assert.Equal("EROFS", probe.GetProperty("rootfs").GetString());

        // But the two paths Next.js genuinely needs at runtime are writable — this is the half that
        // makes ReadonlyRootfs safe to turn on rather than a broken deployment.
        Assert.Equal("OK", probe.GetProperty("nextCache").GetString());
        Assert.Equal("OK", probe.GetProperty("tmp").GetString());

        // 2. And Next.js's own ISR machinery — not just a raw fs.writeFileSync — survives a
        // revalidation, which is the specific thing Phase 1 could not rule out.
        var first = await GetAsync(hostname, "/isr");
        Assert.Contains("isr-page", first);

        // revalidate: 1, so this request serves the stale page and triggers a background
        // regeneration that writes into .next/cache.
        await Task.Delay(1500);
        await GetAsync(hostname, "/isr");

        // Give the background regeneration a moment, then confirm the page still serves and the
        // container did not fall over writing its cache.
        await Task.Delay(1500);
        var third = await GetAsync(hostname, "/isr");
        Assert.Contains("isr-page", third);

        // The probe again: if the ISR write had blown up the cache mount, this would no longer be OK.
        var after = JsonDocument.Parse(await GetAsync(hostname, "/api/probe")).RootElement;
        Assert.Equal("OK", after.GetProperty("nextCache").GetString());
    }

    /// <summary>
    /// A genuine standalone Next.js app with (a) an API route that probes each writable path
    /// directly and (b) an ISR page, so both the raw mount and Next.js's own cache machinery are
    /// covered.
    /// </summary>
    private static byte[] BuildProbeAppTar()
    {
        var packageJson = """
            {
              "name": "praxy-rofs-test",
              "version": "1.0.0",
              "scripts": { "build": "next build" },
              "dependencies": { "next": "latest", "react": "latest", "react-dom": "latest" }
            }
            """;
        var nextConfig = """module.exports = { output: "standalone" };""";

        var probeRoute = """
            const fs = require('fs');
            const path = require('path');

            function probe(target) {
              try {
                fs.mkdirSync(path.dirname(target), { recursive: true });
                fs.writeFileSync(target, 'probe');
                fs.unlinkSync(target);
                return 'OK';
              } catch (e) {
                return e.code || String(e);
              }
            }

            export default function handler(req, res) {
              res.status(200).json({
                rootfs: probe('/app/escape.txt'),
                nextCache: probe('/app/.next/cache/praxy-probe/x.txt'),
                tmp: probe('/tmp/praxy-probe.txt'),
              });
            }
            """;

        var isrPage = """
            export async function getStaticProps() {
              return { props: { at: Date.now() }, revalidate: 1 };
            }
            export default function Isr({ at }) {
              return <div>isr-page {at}</div>;
            }
            """;

        return BuildRawTar(
            ("package.json", packageJson),
            ("next.config.js", nextConfig),
            ("pages/api/probe.js", probeRoute),
            ("pages/isr.js", isrPage));
    }

    private async Task<string> GetAsync(string hostname, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Host = hostname } };
        var response = await Client.SendAsync(request);
        Assert.Equal(200, (int)response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ---- helpers (mirrors SiteTests' own) -----------------------------------------------------

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

        var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();
        foreach (var id in containerIds)
        {
            try { await docker.StopAndRemoveAsync(id, CancellationToken.None); }
            catch { /* best-effort test cleanup */ }
        }
    }

    private async Task<string> CreateSiteAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites", operatorToken, new { key, name = key, rootDirectory = "" }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
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
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> WaitForDeploymentStatusAsync(
        string operatorToken, string projectId, string siteId, string deploymentId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(280);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{deploymentId}", operatorToken)));
            var status = deployment.GetProperty("status").GetString();
            if (status == targetStatus || status == "failed")
                return deployment;
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Deployment never reached status '{targetStatus}'.");
    }

    private async Task WaitForSiteActiveAsync(string operatorToken, string projectId, string siteId, string deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var site = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken)));
            if (site.TryGetProperty("activeDeploymentId", out var active) && active.GetString() == deploymentId
                && site.GetProperty("isRunning").GetBoolean())
                return;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Site never became active and running for deployment '{deploymentId}'.");
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
