using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// security-review-phase-1 follow-up: the credential-isolation fix (a container carrying an
/// invocation-scoped credential is never pooled) removed the only bound on how many function
/// containers can exist at once. A non-poolable container never enters <c>WarmPool</c>'s
/// <c>_byDeployment</c> dictionary, so <c>EvictOverflowAsync</c> never sees it and
/// <c>WarmPoolSize</c> does not apply — and since <em>every</em> invocation triggered by an app
/// user is non-poolable (it carries that user's JWT), that is the primary data-plane path for a
/// BaaS, not an edge case.
///
/// <para>Nothing else bounded it: the functions rate limiter partitions per caller
/// (<c>{project}|{token-hash}</c>) and is a fixed window with <c>QueueLimit = 0</c>, so it caps a
/// caller's arrival *rate*, not their concurrency — and a sync invocation holds its container for
/// up to <c>MaxSyncTimeoutSeconds</c>. One signed-up app user could therefore hold dozens of
/// containers at once, each with its own <c>MemoryLimitMb</c>.</para>
///
/// <para>This asserts the isolation property directly — the live count of running function
/// containers — not the config value, matching the discipline
/// <see cref="FunctionContainerIsolationTests"/> established.</para>
/// </summary>
public class FunctionIsolatedContainerCapacityTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const int Cap = 2;
    private const int Concurrent = 6;

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Functions:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Functions:ExecutionPollIntervalSeconds"] = "1",
        ["Praxy:Functions:BuildTimeoutSeconds"] = "120",
        // A small, deterministic cap makes the property fast and unambiguous to observe; the
        // production default (16) would prove the same thing with eight times the containers.
        ["Praxy:Functions:MaxConcurrentIsolatedContainers"] = "2",
        // Short enough that the invocations that lose the race fail rather than queueing behind a
        // 3-second sleep — the point is to observe the cap binding, not to wait it out.
        ["Praxy:Functions:IsolatedContainerWaitSeconds"] = "1",
    };

    /// <summary>Holds its container long enough for every concurrent invocation to overlap.</summary>
    private const string SlowJs = """
        module.exports = async () => {
          await new Promise((r) => setTimeout(r, 3000));
          return { statusCode: 200, body: JSON.stringify({ ok: true }), headers: {} };
        };
        """;

    [Fact]
    public async Task Concurrent_user_triggered_invocations_cannot_exceed_the_isolated_container_cap()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (sessionToken, _) = await SignupAsync(projectId, "capacity@example.com");

        var functionId = await CreateFunctionAsync(operatorToken, projectId, "slow-fn", execute: ["users"]);
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, functionId,
            BuildTar(("index.js", SlowJs)));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "ready");

        using var dockerClient = new DockerClientBuilder().WithEndpoint(new Uri("unix:///var/run/docker.sock")).Build();

        // Other tests in this collection may leave pooled containers alive (MaxIdleSeconds), so the
        // property under test is the *delta* over the count at the moment we start firing — every
        // container this test causes is non-poolable, and nothing else invokes this function.
        var baseline = await RunningFunctionContainersAsync(dockerClient);

        using var polling = new CancellationTokenSource();
        var peak = 0;
        var poller = Task.Run(async () =>
        {
            while (!polling.IsCancellationRequested)
            {
                peak = Math.Max(peak, await RunningFunctionContainersAsync(dockerClient) - baseline);
                try { await Task.Delay(100, polling.Token); } catch (OperationCanceledException) { break; }
            }
        });

        // A single app user, well inside the 60/min rate limit — concurrency, not rate, is the axis
        // that was unbounded.
        var responses = await Task.WhenAll(Enumerable.Range(0, Concurrent)
            .Select(_ => InvokeAsUserAsync(projectId, functionId, sessionToken)));

        polling.Cancel();
        await poller;

        // The property: however many callers pile on, the host never runs more than the cap.
        Assert.True(peak <= Cap, $"Peak concurrent function containers was {peak}, expected at most {Cap}.");

        // And the cap is loud when it binds, per CLAUDE.md's cross-phase rule — a typed, retryable
        // 503, not a silent queue or a generic 500.
        var rejected = responses.Where(r => r.Status == 503).ToList();
        Assert.NotEmpty(rejected);
        foreach (var r in rejected)
        {
            Assert.Equal("function_capacity_exceeded", r.Type);
            Assert.NotNull(r.RetryAfter);
        }

        // The cap must throttle, not break: the invocations that did get a slot still succeeded.
        Assert.Contains(responses, r => r.Status == 200);
    }

    private static async Task<int> RunningFunctionContainersAsync(IDockerClient docker)
    {
        var containers = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { ["praxy.function=true"] = true },
                ["status"] = new Dictionary<string, bool> { ["running"] = true },
            },
        });
        return containers.Count;
    }

    private sealed record InvokeResult(int Status, string? Type, string? RetryAfter);

    private async Task<InvokeResult> InvokeAsUserAsync(string projectId, string functionId, string sessionToken)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/functions/{functionId}/executions", projectId, sessionToken: sessionToken,
            body: new { method = "GET", path = "/" }));
        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
            ? values.FirstOrDefault()
            : null;
        if ((int)response.StatusCode == 200)
            return new InvokeResult(200, null, retryAfter);

        var body = await ReadJson(response);
        return new InvokeResult((int)response.StatusCode,
            body.TryGetProperty("type", out var type) ? type.GetString() : null, retryAfter);
    }

    // ---- helpers (mirrors FunctionWarmPoolCredentialIsolationTests' own) ----------------------

    private async Task<string> CreateFunctionAsync(
        string operatorToken, string projectId, string key, string[]? execute = null)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new
            {
                key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 15,
                execute = execute ?? [],
            }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> UploadDeploymentAsync(
        string operatorToken, string projectId, string functionId, byte[] tar)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/deployments")
        {
            Content = new ByteArrayContent(tar),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        request.Headers.Add("X-Praxy-Session", operatorToken);
        var response = await Client.SendAsync(request);
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task WaitForDeploymentStatusAsync(
        string operatorToken, string projectId, string functionId, string deploymentId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/deployments/{deploymentId}", operatorToken)));
            var status = deployment.GetProperty("status").GetString();
            if (status == targetStatus)
                return;
            if (status == "failed")
                throw new InvalidOperationException($"Deployment failed: {deployment.GetProperty("error").GetString()}");
            await Task.Delay(500);
        }
        throw new TimeoutException($"Deployment never reached status '{targetStatus}'.");
    }

    private static byte[] BuildTar(params (string Name, string Content)[] files)
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
