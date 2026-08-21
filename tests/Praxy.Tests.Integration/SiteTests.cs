using System.Formats.Tar;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The Sites Phase 1 owner-test flow against a real Docker daemon — <c>SiteBuildWorker</c> is a real
/// hosted service in this process doing real <c>docker build</c>/<c>docker run</c> calls against a
/// genuine Next.js app (same "no in-memory transport on the outbound leg" discipline
/// <see cref="FunctionTests"/> uses for Functions, applied here to both the build and the proxied
/// HTTP leg — <see cref="Praxy.Sites.SiteProxyMiddleware"/>'s outbound call to the container is a
/// real socket even though the inbound request arrives over the WebApplicationFactory's in-memory
/// transport). Requires a reachable Docker daemon with outbound network access (`npm install` needs
/// to reach the real npm registry) — the same requirement Functions' own tests carry.
/// </summary>
public class SiteTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Sites:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Sites:BuildTimeoutSeconds"] = "300",
        // Real, but quiet — the reconciler's own periodic pass shouldn't race the test's explicit
        // activate/rollback calls mid-assertion.
        ["Praxy:Sites:ReconcileIntervalSeconds"] = "3600",
    };

    [Fact]
    public async Task Deploy_serve_redeploy_and_roll_back_a_real_nextjs_app()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "blog");
        await SetEnvVarAsync(operatorToken, projectId, siteId, "PRAXY_TEST_MESSAGE", "hello-from-env");

        // ---- v1: real Next.js standalone build, SSR (getServerSideProps) proves it isn't a static shell,
        //          and reads a runtime env var to prove env injection reaches the running container ----
        var v1Deployment = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildNextAppTar("v1"));
        var v1 = await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, v1Deployment, "ready");
        Assert.Equal("ready", v1.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(v1.GetProperty("imageTag").GetString()));

        // "ready" means the image built and can be activated — the same meaning a FunctionDeployment
        // carries, where a container isn't necessarily running either. Whether the site is actually
        // live is the separate signal isRunning/activeDeploymentId exposes, set once
        // SiteBuildWorker's auto-activate call (which starts the real container) finishes.
        var site = await WaitForSiteActiveAsync(operatorToken, projectId, siteId, v1Deployment);
        Assert.True(site.GetProperty("isRunning").GetBoolean());

        var hostname = $"blog.{projectId}.sites.localhost";
        var v1Body = await GetSiteBodyAsync(hostname);
        Assert.Contains("praxy-site-v1", v1Body);
        Assert.Contains("hello-from-env", v1Body);

        // ---- redeploy: v2 auto-activates, the old container stops, the new one serves ----
        var v2Deployment = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildNextAppTar("v2"));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, v2Deployment, "ready");
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, v2Deployment);

        var v2Body = await GetSiteBodyAsync(hostname);
        Assert.Contains("praxy-site-v2", v2Body);
        Assert.DoesNotContain("praxy-site-v1", v2Body);

        // ---- rollback: reactivating v1 makes it live again ----
        var activate = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{v1Deployment}/activate", operatorToken));
        Assert.Equal(200, (int)activate.StatusCode);

        var rolledBack = await GetSiteBodyAsync(hostname);
        Assert.Contains("praxy-site-v1", rolledBack);
    }

    [Fact]
    public async Task Build_missing_standalone_output_fails_with_the_actionable_error_not_an_opaque_docker_error()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "broken");

        // A "build" script that succeeds (exit 0) but never produces .next/standalone — no real
        // Next.js needed to prove this specific failure path, only that our own check fires.
        var tar = BuildRawTar(
            ("package.json", """{ "name": "broken", "scripts": { "build": "mkdir -p public" } }"""));
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, tar);

        var deployment = await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, deploymentId, "failed");
        Assert.Equal("failed", deployment.GetProperty("status").GetString());
        Assert.Contains("standalone", deployment.GetProperty("error").GetString());
        Assert.Contains("next.config", deployment.GetProperty("error").GetString());
        Assert.Contains("standalone", deployment.GetProperty("buildLog").GetString());

        var site = await GetSiteAsync(operatorToken, projectId, siteId);
        Assert.False(site.TryGetProperty("activeDeploymentId", out _));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string> CreateSiteAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites", operatorToken, new { key, name = key, rootDirectory = "" }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    private async Task SetEnvVarAsync(string operatorToken, string projectId, string siteId, string key, string value)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Put,
            $"/v1/console/projects/{projectId}/sites/{siteId}/env/{key}", operatorToken, new { value }));
        Assert.Equal(200, (int)response.StatusCode);
    }

    /// <summary>Waits until the site is actually serving <paramref name="deploymentId"/> — see this test's own remarks on why "ready" alone isn't enough.</summary>
    private async Task<JsonElement> WaitForSiteActiveAsync(string operatorToken, string projectId, string siteId, string deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var site = await GetSiteAsync(operatorToken, projectId, siteId);
            if (site.TryGetProperty("activeDeploymentId", out var active) && active.GetString() == deploymentId
                && site.GetProperty("isRunning").GetBoolean())
                return site;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Site never became active and running for deployment '{deploymentId}'.");
    }

    private async Task<JsonElement> GetSiteAsync(string operatorToken, string projectId, string siteId) =>
        await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/sites/{siteId}", operatorToken)));

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

    protected async Task<JsonElement> WaitForDeploymentStatusAsync(
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

    /// <summary>
    /// Sends a request through <see cref="Praxy.Sites.SiteProxyMiddleware"/> by setting the Host
    /// header to the site's public hostname — the exact same dispatch a real browser hitting
    /// <c>https://blog.&lt;projectId&gt;.sites.&lt;domain&gt;</c> would trigger. The inbound leg rides
    /// the WebApplicationFactory's in-memory transport; the outbound leg to the container is a real
    /// socket, so this genuinely exercises the proxy, not a stub.
    /// </summary>
    private async Task<string> GetSiteBodyAsync(string hostname)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = hostname } };
        var response = await Client.SendAsync(request);
        Assert.Equal(200, (int)response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// A minimal, genuine Next.js app: <c>output: "standalone"</c> (required), Pages Router with
    /// <c>getServerSideProps</c> (forces per-request SSR — a static shell would never see the env
    /// var change between v1 build-time and container runtime), reading a plain (non-
    /// <c>NEXT_PUBLIC_</c>) env var to prove runtime injection.
    /// </summary>
    private static byte[] BuildNextAppTar(string version)
    {
        var packageJson = """
            {
              "name": "praxy-site-test",
              "version": "1.0.0",
              "scripts": { "build": "next build" },
              "dependencies": { "next": "latest", "react": "latest", "react-dom": "latest" }
            }
            """;
        var nextConfig = """module.exports = { output: "standalone" };""";
        var indexPage = $$"""
            export async function getServerSideProps() {
              return { props: { envMessage: process.env.PRAXY_TEST_MESSAGE || "no-env-var" } };
            }
            export default function Home({ envMessage }) {
              return <div>praxy-site-{{version}} says: {envMessage}</div>;
            }
            """;
        return BuildRawTar(
            ("package.json", packageJson),
            ("next.config.js", nextConfig),
            ("pages/index.js", indexPage));
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
