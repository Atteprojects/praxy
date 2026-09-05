using System.Formats.Tar;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// security-review-phase-1, finding B: neither executor set any isolation flag beyond memory and
/// CPU — zero repo-wide matches for <c>PidsLimit</c>, <c>CapDrop</c>, <c>SecurityOpt</c>, or a
/// non-root <c>User</c> before this phase. Real Docker, same discipline as <see cref="FunctionTests"/>
/// — this deploys and invokes a real function through the real <c>DockerExecutor.StartContainerAsync</c>
/// code path (the exact <c>HostConfig</c> a production invocation gets), not a synthetic
/// <c>docker run</c> against a hand-built config. The property under test is the one that actually
/// has to hold — "a fork bomb is contained" — not the config value; asserting
/// <c>HostConfig.PidsLimit == 256</c> would pass even if Docker stopped honoring the field.
/// </summary>
public class FunctionContainerIsolationTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Functions:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Functions:ExecutionPollIntervalSeconds"] = "1",
        ["Praxy:Functions:BuildTimeoutSeconds"] = "120",
        // A small, deterministic cap makes the isolation property fast and unambiguous to observe —
        // the production default (256) would prove the same thing, just after spawning ten times as
        // many processes first.
        ["Praxy:Functions:PidsLimit"] = "20",
    };

    /// <summary>
    /// Fires off as many background child processes as it can, as fast as it can. Node's
    /// <c>child_process.spawn</c> never throws synchronously for a fork failure — a cgroup-refused
    /// fork surfaces later as an async <c>'error'</c> event on the child (confirmed by hand against a
    /// plain <c>docker run --pids-limit=20</c> before writing this test: 200 rapid-fire spawns, ~187
    /// <c>'error'</c> events, and <c>/proc</c> settling at well under 20 entries) — so this counts
    /// those, plus reads <c>/proc</c> once the dust settles. The <c>/proc</c> count is the one that
    /// actually matters: however high the loop bound goes, a capped live-process count proves the
    /// container's own process table was bounded, not that the loop happened to stop early for an
    /// unrelated reason.
    /// </summary>
    private const string ForkBombJs = """
        const { spawn } = require('child_process');
        const fs = require('fs');
        module.exports = async () => {
          let errors = 0;
          for (let i = 0; i < 300; i++) {
            const child = spawn('sleep', ['5'], { stdio: 'ignore' });
            child.on('error', () => { errors++; });
          }
          await new Promise((resolve) => setTimeout(resolve, 1500));
          let procCount = -1;
          try { procCount = fs.readdirSync('/proc').filter((n) => /^[0-9]+$/.test(n)).length; } catch (e) {}
          return { statusCode: 200, body: JSON.stringify({ errors, procCount }), headers: {} };
        };
        """;

    [Fact]
    public async Task A_function_that_tries_to_fork_bomb_is_capped_by_PidsLimit()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var functionId = await CreateFunctionAsync(operatorToken, projectId, "fork-bomb");
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, functionId,
            BuildTar(("index.js", ForkBombJs)));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "ready");

        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/executions", operatorToken,
            new { method = "GET", path = "/" }));
        Assert.Equal(200, (int)response.StatusCode);
        var execution = await ReadJson(response);
        Assert.Equal("completed", execution.GetProperty("status").GetString());

        var body = JsonDocument.Parse(execution.GetProperty("responseBody").GetString()!).RootElement;
        var errors = body.GetProperty("errors").GetInt32();
        var procCount = body.GetProperty("procCount").GetInt32();

        // The loop asked for 300 children under a PidsLimit of 20; a container with no limit at all
        // (this repo's state before this phase) forks essentially all of them with zero errors. Both
        // assertions failing would mean the limit isn't actually being enforced, not that the test
        // asked for too few.
        Assert.True(errors > 0, "expected most spawn attempts to fail once the container's PidsLimit was hit, but none did");
        Assert.True(procCount >= 0, "could not read /proc inside the container to confirm its live process count");
        Assert.True(procCount <= 20, $"expected /proc's own process count to respect PidsLimit=20, saw {procCount}");
    }

    // ---- helpers (mirrors FunctionTests' own private helpers) ---------------------------------

    private async Task<string> CreateFunctionAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new { key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 25 }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
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
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> WaitForDeploymentStatusAsync(
        string operatorToken, string projectId, string functionId, string deploymentId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var deployment = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/functions/{functionId}/deployments/{deploymentId}", operatorToken)));
            var status = deployment.GetProperty("status").GetString();
            if (status == targetStatus)
                return deployment;
            if (status == "failed")
                throw new InvalidOperationException(
                    $"Deployment failed: {deployment.GetProperty("error").GetString()}");
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
