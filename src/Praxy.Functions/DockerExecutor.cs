using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Praxy.Auth;

namespace Praxy.Functions;

/// <summary>One running function container — the warm pool's unit of tracking.</summary>
public sealed record RunningContainer(string ContainerId, string Host, int Port, string Secret);

/// <summary>
/// Thin wrapper over <c>Docker.DotNet.Enhanced</c> (dotnet-stack.md's Phase-7 pin — the
/// Testcontainers-maintained fork; <c>Docker.DotNet</c> itself is stale). Owns exactly the four
/// verbs Functions needs: build an image, start/stop a container, nothing else — no exec, no
/// attach, no swarm. Talks to the container via plain HTTP (<see cref="InvokeAsync"/>), not the
/// Docker attach/exec API, so invocation latency doesn't pay for Docker's own multiplexed-stream
/// framing on every call — see <see cref="StartContainerAsync"/> for how the container is reached,
/// which depends on whether <c>api</c> itself runs on the bare host or inside a container.
/// </summary>
public sealed class DockerExecutor : IDisposable
{
    private readonly IDockerClient _client;
    private readonly FunctionsOptions _options;
    private readonly ILogger<DockerExecutor> _logger;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public DockerExecutor(FunctionsOptions options, ILogger<DockerExecutor> logger)
    {
        _options = options;
        _logger = logger;
        _client = new DockerClientBuilder().WithEndpoint(new Uri(options.DockerEndpoint)).Build();
    }

    public sealed record BuildResult(bool Success, string Log, string? Error);

    /// <summary>
    /// Streams the build log line-by-line via <paramref name="onLogLine"/> as it happens (the
    /// caller persists it incrementally — build logs are a queryable row, not an in-memory buffer
    /// that vanishes if the process restarts mid-build) and returns the final success/failure once
    /// the build completes. A Docker build failure does not throw — it surfaces as an
    /// <c>{"error": ...}</c> line in the same NDJSON stream, so failure is a value, not an exception.
    /// </summary>
    /// <remarks>
    /// Deliberately parses the raw NDJSON stream (the
    /// <c>Task&lt;Stream&gt; BuildImageFromDockerfileAsync(Stream, ImageBuildParameters, ...)</c>
    /// overload) rather than the library's own <c>IProgress&lt;JSONMessage&gt;</c>-callback overload —
    /// the latter was observed to hang indefinitely (not honoring cancellation) on some failed
    /// builds against this Docker Engine version, intermittently. Reading the stream directly means
    /// <c>StreamReader.ReadLineAsync(ct)</c> is the only thing that can block, and it actually
    /// respects the token.
    /// </remarks>
    public async Task<BuildResult> BuildImageAsync(
        Stream buildContext, string imageTag, Action<string> onLogLine, CancellationToken ct)
    {
        var log = new System.Text.StringBuilder();
        string? error = null;
        void Append(string line)
        {
            log.Append(line);
            onLogLine(line);
        }

        var parameters = new ImageBuildParameters
        {
            Tags = [imageTag],
            Dockerfile = "Dockerfile",
            Remove = true,
            ForceRemove = true,
        };

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.BuildTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Obsolete because the returned Task completes once the response starts, not once the
            // build finishes — true for callers who don't read the stream, but we do: the while
            // loop below reads to EOF, which *is* "wait for the build to complete" for our purposes.
#pragma warning disable CS0618
            await using var responseStream = await _client.Images.BuildImageFromDockerfileAsync(
                buildContext, parameters, linked.Token);
#pragma warning restore CS0618
            using var reader = new StreamReader(responseStream);

            string? line;
            while ((line = await reader.ReadLineAsync(linked.Token)) is not null)
            {
                if (line.Length == 0)
                    continue;
                System.Text.Json.Nodes.JsonNode? node;
                try
                {
                    node = System.Text.Json.Nodes.JsonNode.Parse(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    Append(line + "\n");
                    continue;
                }

                if (node?["stream"]?.GetValue<string>() is { } streamText)
                    Append(streamText);
                if (node?["status"]?.GetValue<string>() is { } statusText)
                    Append(statusText + "\n");
                if (node?["error"]?.GetValue<string>() is { } errorText)
                {
                    error = errorText;
                    Append($"ERROR: {errorText}\n");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = $"Build timed out after {_options.BuildTimeoutSeconds}s.";
            Append($"ERROR: {error}\n");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            Append($"ERROR: {ex.Message}\n");
        }

        return new BuildResult(error is null, log.ToString(), error);
    }

    /// <summary>
    /// Two ways to reach the container, chosen by <see cref="FunctionsOptions.DockerNetwork"/>:
    /// <list type="bullet">
    /// <item>Unset (dev mode — <c>api</c> runs bare on the host): publish the container's port to
    /// the host's own <c>127.0.0.1</c> on a random port and connect there. Only correct when the
    /// caller's <c>127.0.0.1</c> and the Docker host's <c>127.0.0.1</c> are the same network
    /// namespace.</item>
    /// <item>Set (the Docker Compose self-host stack — <c>api</c> runs inside its own container):
    /// join the container to that Docker network instead of publishing a host port at all, and
    /// connect to its container IP on <see cref="RuntimeTemplates.RuntimePort"/> directly. A
    /// sibling container published to the real host's loopback is unreachable from inside
    /// <c>api</c>'s own container — its <c>127.0.0.1</c> is a different, isolated loopback — so this
    /// is the only path that works there. It also never puts the function's port on the host's
    /// network stack at all, which is strictly more secure than the host-publish path.</item>
    /// </list>
    /// </summary>
    public async Task<RunningContainer> StartContainerAsync(
        string imageTag, IReadOnlyDictionary<string, string> envVars, string label, CancellationToken ct)
    {
        var (secret, _) = Secrets.Generate();
        var env = envVars.Select(kv => $"{kv.Key}={kv.Value}").ToList();
        env.Add($"OPEN_RUNTIMES_SECRET={secret}");

        var portKey = $"{RuntimeTemplates.RuntimePort}/tcp";
        var attachToNetwork = !string.IsNullOrEmpty(_options.DockerNetwork);

        var hostConfig = new HostConfig
        {
            Memory = _options.MemoryLimitMb * 1024 * 1024,
            NanoCPUs = (long)(_options.CpuLimit * 1_000_000_000),
            AutoRemove = false,
        };
        NetworkingConfig? networkingConfig = null;
        if (attachToNetwork)
        {
            // Both set, matching what `docker run --network=<name>` itself does under the hood —
            // NetworkMode is what actually attaches the container at creation; EndpointsConfig
            // alone (without a matching NetworkMode) is a documented Docker Engine API footgun that
            // silently leaves the container on the default bridge instead.
            hostConfig.NetworkMode = _options.DockerNetwork;
            networkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [_options.DockerNetwork] = new EndpointSettings(),
                },
            };
        }
        else
        {
            hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                [portKey] = [new PortBinding { HostIP = "127.0.0.1", HostPort = "0" }],
            };
        }

        _logger.LogWarning("DIAG: about to call CreateContainerAsync for {Label}", label);
        var created = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = imageTag,
            Env = env,
            ExposedPorts = new Dictionary<string, EmptyStruct> { [portKey] = default },
            Labels = new Dictionary<string, string> { ["praxy.function"] = "true", ["praxy.deployment"] = label },
            HostConfig = hostConfig,
            NetworkingConfig = networkingConfig,
        }, ct);
        _logger.LogWarning("DIAG: CreateContainerAsync returned {ContainerId}", created.ID);

        try
        {
            _logger.LogWarning("DIAG: about to call StartContainerAsync for {ContainerId}", created.ID);
            await _client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            _logger.LogWarning("DIAG: StartContainerAsync returned for {ContainerId}", created.ID);
            var inspected = await _client.Containers.InspectContainerAsync(created.ID, ct);
            _logger.LogWarning("DIAG: InspectContainerAsync returned for {ContainerId}", created.ID);

            string host;
            int port;
            if (attachToNetwork)
            {
                if (inspected.NetworkSettings?.Networks is not { } networks
                    || !networks.TryGetValue(_options.DockerNetwork, out var endpoint))
                    throw new InvalidOperationException(
                        $"Container {created.ID} is not attached to configured Docker network '{_options.DockerNetwork}'.");
                host = !string.IsNullOrEmpty(endpoint.IPAddress)
                    ? endpoint.IPAddress
                    : throw new InvalidOperationException(
                        $"Container {created.ID} has no IP address on Docker network '{_options.DockerNetwork}'.");
                port = RuntimeTemplates.RuntimePort;
            }
            else
            {
                var binding = inspected.NetworkSettings?.Ports?[portKey]?.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Container {created.ID} has no published port binding for {portKey}.");
                host = "127.0.0.1";
                port = int.Parse(binding.HostPort);
            }

            await WaitUntilHealthyAsync(host, port, secret, ct);
            return new RunningContainer(created.ID, host, port, secret);
        }
        catch
        {
            await StopAndRemoveAsync(created.ID, CancellationToken.None);
            throw;
        }
    }

    private async Task WaitUntilHealthyAsync(string host, int port, string secret, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ColdStartTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var attempt = 0;
        while (true)
        {
            attempt++;
            _logger.LogWarning("DIAG: health-check attempt {Attempt} for {Host}:{Port}", attempt, host, port);
            linked.Token.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{port}{RuntimeTemplates.HealthPath}");
                req.Headers.Add(RuntimeTemplates.SecretHeader, secret);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var resp = await _http.SendAsync(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !linked.IsCancellationRequested)
            {
                // Not warm yet (connection refused, container still booting) — keep polling.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), linked.Token);
        }
    }

    public sealed record InvokeResult(int StatusCode, string Body, Dictionary<string, string> Headers, string Logs, string? Errors);

    public async Task<InvokeResult> InvokeAsync(
        RunningContainer container, string method, string path, string body,
        IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken ct)
    {
        var envelope = new System.Text.Json.Nodes.JsonObject
        {
            ["method"] = method,
            ["path"] = path,
            ["body"] = body,
            ["headers"] = new System.Text.Json.Nodes.JsonObject(
                headers.Select(kv => KeyValuePair.Create(kv.Key, (System.Text.Json.Nodes.JsonNode?)kv.Value))),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{container.Host}:{container.Port}/")
        {
            Content = new StringContent(envelope.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(RuntimeTemplates.SecretHeader, container.Secret);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var resp = await _http.SendAsync(req, cts.Token);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return new InvokeResult((int)resp.StatusCode, "", [], "", $"Runtime returned HTTP {(int)resp.StatusCode}.");

        var parsed = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject;
        return new InvokeResult(
            (int?)parsed?["statusCode"] ?? 200,
            parsed?["body"]?.GetValue<string>() ?? "",
            parsed?["headers"] is System.Text.Json.Nodes.JsonObject h
                ? h.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "")
                : [],
            parsed?["logs"]?.GetValue<string>() ?? "",
            string.IsNullOrEmpty(parsed?["errors"]?.GetValue<string>()) ? null : parsed!["errors"]!.GetValue<string>());
    }

    public async Task StopAndRemoveAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await _client.Containers.StopContainerAsync(
                containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, ct);
        }
        catch
        {
            // Already stopped/gone — proceed to remove regardless.
        }
        try
        {
            await _client.Containers.RemoveContainerAsync(
                containerId, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch
        {
            // Best-effort cleanup; a leaked container is a warm-pool accounting bug to fix, not
            // something worth failing the caller's request over.
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _client.Dispose();
    }
}
