using System.Formats.Tar;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
        // Short enough that the idle-sweep test observes a real sweep within its own timeout,
        // without waiting anywhere close to the 600s production default.
        ["Praxy:Sites:PreviewIdleSeconds"] = "3",
        ["Praxy:Sites:PreviewSweepIntervalSeconds"] = "1",
    };

    /// <summary>
    /// Unlike Functions' WarmPool (stopped on every shutdown — an ephemeral cache, correctly cleared),
    /// a site's container is deliberately left running across an api restart (Docker's own
    /// RestartPolicy: unless-stopped, SiteReconciler re-attaches rather than restarting) — the whole
    /// point of the design. That means nothing in production code ever stops a test's containers when
    /// the WebApplicationFactory disposes, so this test's own containers would otherwise accumulate
    /// on the host, one leaked container (and image) per run, forever. Reads them back from the
    /// (still-live) test database before the factory tears down and stops them the same way
    /// production would on a real redeploy — via the real SiteDockerExecutor, not a raw docker CLI
    /// shell-out.
    /// </summary>
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
        foreach (var container in registry.AllContainers())
            containerIds.Add(container.ContainerId);

        if (containerIds.Count == 0)
            return;

        var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();
        foreach (var containerId in containerIds)
            await docker.StopAndRemoveAsync(containerId, CancellationToken.None);
    }

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

    /// <summary>
    /// Sites Phase 2's own owner-test flow: a superseded deployment stays reachable at its own
    /// preview URL while production serves whatever's currently active; concurrent cold starts of
    /// the same preview don't race into two containers; an idle preview gets swept automatically
    /// without ever touching the still-serving production container; a very-stale preview cold-starts
    /// correctly again on demand; and re-activating a preview (a graceful blue-green swap) never lets
    /// a tight polling loop against production observe a single failed request across the swap.
    /// </summary>
    [Fact]
    public async Task Preview_urls_serve_independently_idle_sweep_runs_and_activation_has_no_gap()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "previews");
        var prodHostname = $"previews.{projectId}.sites.localhost";
        var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();

        var v1Deployment = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildNextAppTar("v1"));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, v1Deployment, "ready");
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, v1Deployment);
        Assert.Contains("praxy-site-v1", await GetSiteBodyAsync(prodHostname));

        // v2 auto-activates on success, superseding v1 — v1 is now "ready, but not active": exactly
        // the preview shape (an older, still-reachable build that's no longer production).
        var v2Deployment = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildNextAppTar("v2"));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, v2Deployment, "ready");
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, v2Deployment);
        Assert.Contains("praxy-site-v2", await GetSiteBodyAsync(prodHostname));

        var previewHostname = $"{v1Deployment}.previews.{projectId}.sites.localhost";
        // SiteDockerExecutor.StartContainerAsync labels a container with the deployment id's raw
        // Guid.ToString() (dashed), not its wire form (v1Deployment, 32 hex chars no dashes) — the
        // same convention Phase 1's ActivateAsync already used for the active deployment's container.
        var previewLabel = $"praxy.deployment={Guid.ParseExact(v1Deployment, "N")}";

        // ---- cold start: several concurrent first-requests to the same cold preview must not race
        //      into two containers ----
        var bodies = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => GetSiteBodyAsync(previewHostname)));
        Assert.All(bodies, b => Assert.Contains("praxy-site-v1", b));
        Assert.Equal(1, await docker.CountRunningContainersAsync(previewLabel, CancellationToken.None));
        // Production was never touched by any of this.
        Assert.Contains("praxy-site-v2", await GetSiteBodyAsync(prodHostname));

        // ---- idle sweep: nobody hits the preview again, so SitePreviewSweeper (PreviewIdleSeconds=3
        //      / PreviewSweepIntervalSeconds=1 in this test class) stops it on its own ----
        var sweepDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < sweepDeadline
               && await docker.CountRunningContainersAsync(previewLabel, CancellationToken.None) > 0)
            await Task.Delay(500);
        Assert.Equal(0, await docker.CountRunningContainersAsync(previewLabel, CancellationToken.None));
        // The sweep must never have touched the still-active production container.
        Assert.Contains("praxy-site-v2", await GetSiteBodyAsync(prodHostname));

        // ---- a very-stale preview cold-starts correctly again on demand ----
        Assert.Contains("praxy-site-v1", await GetSiteBodyAsync(previewHostname));

        // ---- graceful activation: re-activating v1 (a blue-green swap, not stop-old-then-start-new)
        //      must never let a tight concurrent poll against production see a failed request ----
        var failures = 0;
        using var pollCts = new CancellationTokenSource();
        var pollTask = Task.Run(async () =>
        {
            while (!pollCts.IsCancellationRequested)
            {
                try
                {
                    var response = await Client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = prodHostname } });
                    if ((int)response.StatusCode != 200)
                        Interlocked.Increment(ref failures);
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
                await Task.Delay(20);
            }
        });

        var activate = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{v1Deployment}/activate", operatorToken));
        Assert.Equal(200, (int)activate.StatusCode);
        await Task.Delay(500);
        pollCts.Cancel();
        await pollTask;

        Assert.Equal(0, failures);
        Assert.Contains("praxy-site-v1", await GetSiteBodyAsync(prodHostname));
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// The redeploy that abandoned a container, at its source. <c>SitesService.ActivateAsync</c>
    /// cleared the outgoing deployment's <c>container_id</c> unconditionally but stopped the
    /// container only when the in-memory <see cref="Praxy.Sites.SiteContainerRegistry"/> still held
    /// an entry for it — so a redeploy in a process that had never populated one left a container
    /// running that nothing could reach (the column is the only way anything resolves a site
    /// container) and nothing could stop (the id had just been erased).
    ///
    /// The registry is emptied by every restart, which is what this test reproduces: evicting the
    /// entry is exactly the state an api process is in after starting up and not yet having
    /// reconciled or served this site. Reverting the fix leaves v1's container running here.
    /// </summary>
    [Fact]
    public async Task Redeploying_stops_the_outgoing_container_even_when_the_registry_has_forgotten_it()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "swap");
        var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();
        var registry = Factory.Services.GetRequiredService<Praxy.Sites.SiteContainerRegistry>();

        var v1 = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildNextAppTar("v1"));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, v1, "ready");
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, v1);

        var v1Label = $"praxy.deployment={Guid.ParseExact(v1, "N")}";
        Assert.Equal(1, await docker.CountRunningContainersAsync(v1Label, CancellationToken.None));

        // What a restart does to the registry, without restarting the host: the row still records
        // v1's container, nothing in memory does.
        Assert.True(registry.TryRemove(Guid.ParseExact(v1, "N"), out _));

        var v2 = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildNextAppTar("v2"));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, v2, "ready");
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, v2);
        Assert.Contains("praxy-site-v2", await GetSiteBodyAsync($"swap.{projectId}.sites.localhost"));

        // The swap flips the DB pointer first and stops the outgoing container after, and that stop
        // is a graceful one (WaitBeforeKillSeconds), so "the site is serving v2" is reached strictly
        // before v1's container is gone — poll rather than assert instantly. Without the fix this
        // deadline expires with the container still up, which is the failure this exists to catch.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline
               && await docker.CountRunningContainersAsync(v1Label, CancellationToken.None) > 0)
            await Task.Delay(500);
        Assert.Equal(0, await docker.CountRunningContainersAsync(v1Label, CancellationToken.None));
    }

    /// <summary>
    /// The leak this class's own DisposeAsync comment predicted, from the other direction. That
    /// cleanup is best-effort and needs an orderly shutdown; a killed run, or a redeploy whose
    /// previous container was never in the in-memory registry (<c>SitesService.ActivateAsync</c>
    /// nulls the row's container id and only stops the container if the registry held it), leaves a
    /// container running that no <c>site_deployments</c> row mentions. Nothing looked for those
    /// until <c>SiteDockerExecutor.RemoveUnreferencedContainersAsync</c>, and they accumulated one
    /// per occurrence — found as 17 of them on a dev machine and 2 on the production droplet, the
    /// oldest running for two weeks.
    ///
    /// Asserts the rule in both directions, because a sweep that removes too much takes hosted sites
    /// down on every restart — which is exactly why the Functions sweep declined to cover Sites at
    /// all: a referenced container survives, an unreferenced one does not, and a container younger
    /// than the cutoff survives regardless (the guard against reclaiming what SiteReconciler is
    /// starting concurrently).
    ///
    /// Not hermetic by nature: the sweep is host-wide, so it also reclaims any unreferenced site
    /// container this machine was already carrying. That is the behavior under test, not a
    /// side effect to design around.
    /// </summary>
    [Fact]
    public async Task Unreferenced_site_containers_are_reclaimed_and_referenced_ones_are_left_alone()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "reclaim");
        var docker = Factory.Services.GetRequiredService<Praxy.Sites.SiteDockerExecutor>();

        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, siteId, BuildNextAppTar("v1"));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, siteId, deploymentId, "ready");
        await WaitForSiteActiveAsync(operatorToken, projectId, siteId, deploymentId);

        var containerId = await ReadContainerIdAsync(deploymentId);
        Assert.NotNull(containerId);
        var label = $"praxy.deployment={Guid.ParseExact(deploymentId, "N")}";
        Assert.Equal(1, await docker.CountRunningContainersAsync(label, CancellationToken.None));

        // Still referenced: a sweep with every container old enough to qualify must not touch it.
        await docker.RemoveUnreferencedContainersAsync(
            await ReferencedContainerIdsAsync(), DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(1, await docker.CountRunningContainersAsync(label, CancellationToken.None));

        // Too young: unreferenced now, but the cutoff predates it, so it is still left alone —
        // this is the container SiteReconciler might be starting right now.
        await ClearContainerIdAsync(deploymentId);
        await docker.RemoveUnreferencedContainersAsync(
            await ReferencedContainerIdsAsync(), DateTime.UtcNow.AddHours(-1), CancellationToken.None);
        Assert.Equal(1, await docker.CountRunningContainersAsync(label, CancellationToken.None));

        // Unreferenced and old enough: reclaimed.
        var reclaimed = await docker.RemoveUnreferencedContainersAsync(
            await ReferencedContainerIdsAsync(), DateTime.UtcNow, CancellationToken.None);
        Assert.True(reclaimed >= 1, $"Expected at least this test's own container to be reclaimed, got {reclaimed}.");
        Assert.Equal(0, await docker.CountRunningContainersAsync(label, CancellationToken.None));
    }

    private async Task<HashSet<string>> ReferencedContainerIdsAsync()
    {
        var ids = new HashSet<string>();
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT container_id FROM praxy.site_deployments WHERE container_id IS NOT NULL", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task<string?> ReadContainerIdAsync(string deploymentId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT container_id FROM praxy.site_deployments WHERE id = $1", conn);
        cmd.Parameters.AddWithValue(Guid.ParseExact(deploymentId, "N"));
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Reproduces the abandoned-container state SitesService.ActivateAsync can leave behind.</summary>
    private async Task ClearContainerIdAsync(string deploymentId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "UPDATE praxy.site_deployments SET container_id = NULL WHERE id = $1", conn);
        cmd.Parameters.AddWithValue(Guid.ParseExact(deploymentId, "N"));
        await cmd.ExecuteNonQueryAsync();
    }

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
