using System.Formats.Tar;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// security-review-phase-1, finding: warm-pool credential reuse. Real Docker, same discipline as
/// <see cref="FunctionTests"/> — <c>FunctionExecutionService</c>, <c>WarmPool</c> and
/// <c>DockerExecutor</c> are doing real <c>docker run</c> calls, not stubbed.
///
/// Before the fix, <c>WarmPool.AcquireAsync</c> baked <c>PRAXY_FUNCTION_JWT</c>/
/// <c>PRAXY_FUNCTION_USER_ID</c> into a container's environment only on cold start and reused the
/// same container — env untouched — for every later invocation of the same deployment. Two
/// different app users invoking the same function back-to-back would both land on the one warm
/// container: the second invocation would see the *first* user's JWT and user id, not its own,
/// because nothing about acquiring an already-warm container ever looks at the newly-computed env
/// again. This test proves the isolation property directly — the thing that actually has to hold,
/// not the config value — by invoking as two different app users in immediate succession and
/// asserting each invocation's container only ever reports *that* invocation's own credential.
/// </summary>
public class FunctionWarmPoolCredentialIsolationTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Functions:BuildPollIntervalSeconds"] = "1",
        ["Praxy:Functions:ExecutionPollIntervalSeconds"] = "1",
        ["Praxy:Functions:BuildTimeoutSeconds"] = "120",
    };

    private const string EchoIdentityJs = """
        module.exports = async () => ({
          statusCode: 200,
          body: JSON.stringify({
            jwt: process.env.PRAXY_FUNCTION_JWT || null,
            userId: process.env.PRAXY_FUNCTION_USER_ID || null,
          }),
          headers: {},
        });
        """;

    [Fact]
    public async Task Back_to_back_invocations_by_different_app_users_never_cross_credentials()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var (tokenA, userA) = await SignupAsync(projectId, "user-a@example.com");
        var (tokenB, userB) = await SignupAsync(projectId, "user-b@example.com");
        var userAId = userA.GetProperty("id").GetString()!;
        var userBId = userB.GetProperty("id").GetString()!;
        Assert.NotEqual(userAId, userBId);

        var functionId = await CreateFunctionAsync(operatorToken, projectId, "echo-identity", execute: ["users"]);
        var deploymentId = await UploadDeploymentAsync(operatorToken, projectId, functionId,
            BuildTar(("index.js", EchoIdentityJs)));
        await WaitForDeploymentStatusAsync(operatorToken, projectId, functionId, deploymentId, "ready");

        var responseA = await InvokeAsUserAsync(projectId, functionId, tokenA);
        Assert.Equal(userAId, responseA.GetProperty("userId").GetString());
        var jwtA = responseA.GetProperty("jwt").GetString();
        Assert.False(string.IsNullOrEmpty(jwtA));

        // Same function, same (still-warm, per the pre-fix pool key of deploymentId alone)
        // deployment, a different app user, invoked immediately after — the exact sequence that
        // used to leak user A's env into user B's invocation.
        var responseB = await InvokeAsUserAsync(projectId, functionId, tokenB);
        Assert.Equal(userBId, responseB.GetProperty("userId").GetString());
        var jwtB = responseB.GetProperty("jwt").GetString();
        Assert.False(string.IsNullOrEmpty(jwtB));
        Assert.NotEqual(jwtA, jwtB);

        // And a third invocation back as user A must still see its own identity, not whatever the
        // most recent cold start happened to carry.
        var responseA2 = await InvokeAsUserAsync(projectId, functionId, tokenA);
        Assert.Equal(userAId, responseA2.GetProperty("userId").GetString());
    }

    private async Task<JsonElement> InvokeAsUserAsync(string projectId, string functionId, string sessionToken)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/functions/{functionId}/executions", projectId, sessionToken: sessionToken,
            body: new { method = "GET", path = "/" }));
        Assert.Equal(200, (int)response.StatusCode);
        var execution = await ReadJson(response);
        Assert.Equal("completed", execution.GetProperty("status").GetString());
        return JsonDocument.Parse(execution.GetProperty("responseBody").GetString()!).RootElement;
    }

    // ---- helpers (mirrors FunctionTests' own private helpers) ---------------------------------

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
