using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// docs/handoff/sites-build-caching-prompt.md's owner test, automated: <c>SiteRuntimeTemplates.
/// Dockerfile</c> now copies and installs <c>package.json</c>/<c>package-lock.json</c> before the full
/// <c>COPY . .</c>, so Docker's already-enabled local layer cache (<c>SiteDockerExecutor.
/// BuildImageAsync</c> sets no <c>NoCache</c>) should skip <c>npm install</c> on a redeploy that only
/// changes app code. Asserts the real build log text, not just that both builds succeed — that would
/// pass even under the old COPY-everything-before-install ordering this fixes.
/// </summary>
public class SiteBuildCachingTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Sites:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Sites:BuildTimeoutSeconds"] = "120",
        ["Praxy:Sites:ReconcileIntervalSeconds"] = "3600",
    };

    /// <summary>Same reasoning as SiteTests'/SiteGitDeploymentTests' own overrides — a site's container is deliberately left running when api shuts down.</summary>
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
    public async Task Redeploying_with_only_an_app_code_change_reuses_the_cached_npm_install_layer()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "cache-test");

        // A GUID baked into package.json makes this run's dependency layer content unique on the
        // Docker host, so v1's own build is guaranteed to be a genuine first-ever miss even when this
        // suite has run (and left cache behind) on the same machine before — while staying identical
        // between v1 and v2 below, which is the actual condition being tested.
        var cacheTestId = Guid.NewGuid().ToString("N");

        var v1Deployment = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildAppTar(cacheTestId, "v1"));
        var v1 = await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, v1Deployment, "ready");
        Assert.Equal("ready", v1.GetProperty("status").GetString());
        Assert.DoesNotContain("Using cache", InstallStepSegment(v1.GetProperty("buildLog").GetString()!));

        // Only app-marker.txt changes between v1 and v2 — package.json/package-lock.json* are
        // byte-identical, exactly the condition SiteRuntimeTemplates.Dockerfile's reordering (install
        // deps before COPY . .) is meant to exploit.
        var v2Deployment = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildAppTar(cacheTestId, "v2"));
        var v2 = await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, v2Deployment, "ready");
        Assert.Equal("ready", v2.GetProperty("status").GetString());
        Assert.Contains("Using cache", InstallStepSegment(v2.GetProperty("buildLog").GetString()!));
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// The classic (non-BuildKit) Docker builder — what <c>Docker.DotNet</c>'s
    /// <c>BuildImageFromDockerfileAsync</c> actually calls, per <c>SiteDockerExecutor.BuildImageAsync</c>'s
    /// own remarks — logs each Dockerfile instruction as its own <c>"Step N/M : &lt;instruction&gt;"</c>
    /// line, immediately followed by <c>" ---&gt; Using cache"</c> when that layer's content hash matches
    /// an already-built layer (verified against a real daemon while writing this test). Isolates the
    /// <c>RUN npm install</c> step's own slice of the log so a cache hit anywhere else in the build
    /// (e.g. the unavoidable <c>WORKDIR</c> steps) can't produce a false positive.
    /// </summary>
    private static string InstallStepSegment(string buildLog)
    {
        var stepIndex = buildLog.IndexOf("RUN npm install", StringComparison.Ordinal);
        Assert.True(stepIndex >= 0, $"Expected to find a 'RUN npm install' build step in the log:\n{buildLog}");
        var searchFrom = stepIndex + "RUN npm install".Length;
        var nextStepIndex = buildLog.IndexOf("Step ", searchFrom, StringComparison.Ordinal);
        return nextStepIndex >= 0 ? buildLog[searchFrom..nextStepIndex] : buildLog[searchFrom..];
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

    /// <summary>
    /// A minimal but real, respondable site: the build script needs no real npm dependencies (a zero-
    /// package <c>npm install</c> still exercises the real Docker layer-caching path — the reordering
    /// in <c>SiteRuntimeTemplates.Dockerfile</c> doesn't care whether npm has work to do), server.js is
    /// byte-identical across versions, and app-marker.txt is the one file that differs — a "trivial
    /// app-code change" with no dependency change, matching the kickoff prompt's test requirement.
    /// </summary>
    private static byte[] BuildAppTar(string cacheTestId, string version)
    {
        var packageJson = $$"""
            {
              "name": "cache-test-site",
              "version": "1.0.0",
              "_cacheTestId": "{{cacheTestId}}",
              "scripts": { "build": "mkdir -p .next/standalone .next/static public && cp server.js .next/standalone/server.js" }
            }
            """;
        var serverJs = """
            require('http').createServer((req, res) => { res.end('ok'); })
              .listen(process.env.PORT || 3000, process.env.HOSTNAME || '0.0.0.0');
            """;
        return BuildRawTar(
            ("package.json", packageJson),
            ("server.js", serverJs),
            ("app-marker.txt", version));
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
