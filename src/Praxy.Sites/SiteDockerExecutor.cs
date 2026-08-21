using Docker.DotNet;
using Docker.DotNet.Models;

namespace Praxy.Sites;

/// <summary>One running site container — <c>SiteContainerRegistry</c>'s unit of tracking.</summary>
public sealed record RunningSiteContainer(string ContainerId, string Host, int Port);

/// <summary>
/// Thin wrapper over <c>Docker.DotNet.Enhanced</c>, the Sites-specific sibling of
/// <c>Praxy.Functions.DockerExecutor</c>. Deliberately duplicated rather than shared — the same
/// small amount of build/start/stop logic, but the shape genuinely differs: no
/// <c>InvokeAsync</c> (Sites is proxied, not invoked over a JSON envelope), a plain-HTTP readiness
/// probe instead of a shared-secret <c>/_health</c> contract (Sites doesn't control the app's
/// routes to add one), and containers get Docker's own <c>RestartPolicy: unless-stopped</c> since
/// they're meant to stay running rather than be idle-swept. praxy-sites.md's Data model section
/// calls this the implementation session's call to make once looking at the actual code — this is
/// that call.
/// </summary>
public sealed class SiteDockerExecutor : IDisposable
{
    private readonly IDockerClient _client;
    private readonly SitesOptions _options;

    // Same rationale as DockerExecutor's own ConnectTimeout: a container that dies right after
    // being assigned a bridge-network IP can leave that IP briefly blackholed rather than cleanly
    // refused, and a pending connect attempt isn't guaranteed to observe cancellation promptly.
    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public SiteDockerExecutor(SitesOptions options)
    {
        _options = options;
        _client = new DockerClientBuilder().WithEndpoint(new Uri(options.DockerEndpoint)).Build();
    }

    public sealed record BuildResult(bool Success, string Log, string? Error);

    /// <summary>Same NDJSON-streaming approach as <c>DockerExecutor.BuildImageAsync</c> — see its remarks for why the raw-stream overload is used instead of the library's own IProgress callback overload.</summary>
    public async Task<BuildResult> BuildImageAsync(
        Stream buildContext, string imageTag, IReadOnlyDictionary<string, string> buildArgs,
        Action<string> onLogLine, CancellationToken ct)
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
            BuildArgs = new Dictionary<string, string>(buildArgs),
            Remove = true,
            ForceRemove = true,
        };

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.BuildTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

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
    /// Same dual-networking mode as <c>DockerExecutor.StartContainerAsync</c> — see its remarks for
    /// the full explanation of why both <c>HostConfig.NetworkMode</c> and
    /// <c>NetworkingConfig.EndpointsConfig</c> must be set. The one behavioral difference: a site
    /// container gets <c>RestartPolicy: unless-stopped</c>, since it's meant to run continuously
    /// (crash-restarted by Docker itself) rather than be acquired/evicted like a warm-pool entry.
    /// </summary>
    public async Task<RunningSiteContainer> StartContainerAsync(
        string imageTag, IReadOnlyDictionary<string, string> envVars, string label, CancellationToken ct)
    {
        var env = envVars.Select(kv => $"{kv.Key}={kv.Value}").ToList();
        var portKey = $"{SiteRuntimeTemplates.RuntimePort}/tcp";
        var attachToNetwork = !string.IsNullOrEmpty(_options.DockerNetwork);

        var hostConfig = new HostConfig
        {
            Memory = _options.MemoryLimitMb * 1024 * 1024,
            NanoCPUs = (long)(_options.CpuLimit * 1_000_000_000),
            AutoRemove = false,
            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
        };
        NetworkingConfig? networkingConfig = null;
        if (attachToNetwork)
        {
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

        var created = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = imageTag,
            Env = env,
            ExposedPorts = new Dictionary<string, EmptyStruct> { [portKey] = default },
            Labels = new Dictionary<string, string> { ["praxy.site"] = "true", ["praxy.deployment"] = label },
            HostConfig = hostConfig,
            NetworkingConfig = networkingConfig,
        }, ct);

        try
        {
            await _client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            var inspected = await _client.Containers.InspectContainerAsync(created.ID, ct);

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
                port = SiteRuntimeTemplates.RuntimePort;
            }
            else
            {
                var binding = inspected.NetworkSettings?.Ports?[portKey]?.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Container {created.ID} has no published port binding for {portKey}.");
                host = "127.0.0.1";
                port = int.Parse(binding.HostPort);
            }

            await WaitUntilRespondingAsync(host, port, ct);
            return new RunningSiteContainer(created.ID, host, port);
        }
        catch
        {
            await StopAndRemoveAsync(created.ID, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Polls the container's root path until it answers with any HTTP response — Sites doesn't
    /// control the app's routes to require a specific status the way Functions' generated wrapper's
    /// <c>/_health</c> does (a real app's <c>/</c> may legitimately 404), so "the socket is open and
    /// something answered" is the readiness signal.
    /// </summary>
    private async Task WaitUntilRespondingAsync(string host, int port, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{port}/");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !linked.IsCancellationRequested)
            {
                // Not warm yet (connection refused, container still booting) — keep polling.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), linked.Token);
        }
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
            // Best-effort cleanup.
        }
    }

    /// <summary>Whether <paramref name="containerId"/> is currently running — SiteReconciler's own health check, distinct from the initial start-time readiness probe.</summary>
    public async Task<bool> IsRunningAsync(string containerId, CancellationToken ct)
    {
        try
        {
            var inspected = await _client.Containers.InspectContainerAsync(containerId, ct);
            return inspected.State?.Running ?? false;
        }
        catch (DockerContainerNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The address SiteReconciler needs to re-populate the in-memory registry for a container that's confirmed still running — never assumed, always read back from Docker.</summary>
    public async Task<RunningSiteContainer> InspectAddressAsync(string containerId, CancellationToken ct)
    {
        var inspected = await _client.Containers.InspectContainerAsync(containerId, ct);
        var attachToNetwork = !string.IsNullOrEmpty(_options.DockerNetwork);

        string host;
        int port;
        if (attachToNetwork)
        {
            if (inspected.NetworkSettings?.Networks is not { } networks
                || !networks.TryGetValue(_options.DockerNetwork, out var endpoint) || string.IsNullOrEmpty(endpoint.IPAddress))
                throw new InvalidOperationException($"Container {containerId} has no IP address on Docker network '{_options.DockerNetwork}'.");
            host = endpoint.IPAddress;
            port = SiteRuntimeTemplates.RuntimePort;
        }
        else
        {
            var portKey = $"{SiteRuntimeTemplates.RuntimePort}/tcp";
            var binding = inspected.NetworkSettings?.Ports?[portKey]?.FirstOrDefault()
                ?? throw new InvalidOperationException($"Container {containerId} has no published port binding for {portKey}.");
            host = "127.0.0.1";
            port = int.Parse(binding.HostPort);
        }
        return new RunningSiteContainer(containerId, host, port);
    }

    public void Dispose()
    {
        _http.Dispose();
        _client.Dispose();
    }
}
