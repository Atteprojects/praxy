using System.Formats.Tar;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace Praxy.Sites;

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
    private readonly ILogger<SiteDockerExecutor> _logger;

    // Same rationale as DockerExecutor's own ConnectTimeout: a container that dies right after
    // being assigned a bridge-network IP can leave that IP briefly blackholed rather than cleanly
    // refused, and a pending connect attempt isn't guaranteed to observe cancellation promptly.
    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public SiteDockerExecutor(SitesOptions options, ILogger<SiteDockerExecutor> logger)
    {
        _options = options;
        _logger = logger;
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

    /// <summary>
    /// How many currently-running containers carry a given Docker label (<c>key=value</c>) — a real
    /// <c>Docker.DotNet</c> query, kept here so test code (e.g. <c>SiteTests</c>' preview-container
    /// assertions) never needs to shell out to a raw <c>docker ps</c> CLI, matching this class's own
    /// "no raw CLI shell-outs" discipline that its cleanup callers already follow.
    /// </summary>
    public async Task<int> CountRunningContainersAsync(string label, CancellationToken ct)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [label] = true },
            },
        }, ct);
        return containers.Count;
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

    /// <summary>
    /// Best-effort: spins up an ephemeral headless-Chromium container on the same network as
    /// <paramref name="target"/>, screenshots its root path, copies the PNG out, and always tears the
    /// capture container down — never throws, returns null on any failure (image pull, container
    /// startup, timeout, page crash, missing output file). Called by <c>SiteScreenshotWorker</c>, kept
    /// off the activation path itself so a slow or failed capture can never delay a site going live.
    /// </summary>
    public async Task<byte[]?> CaptureScreenshotAsync(RunningSiteContainer target, CancellationToken ct)
    {
        var attachToNetwork = !string.IsNullOrEmpty(_options.DockerNetwork);
        // Mirrors StartContainerAsync's own dual-mode story: on the shared Docker network, the
        // site's own container IP is reachable directly. Off it (bare `dotnet run` dev mode — the
        // site container is published to 127.0.0.1 on the *host*, not reachable at "127.0.0.1" from
        // inside a second, unrelated bridge-network container), Docker Desktop and Docker 20.10+'s
        // ExtraHosts/host-gateway both expose the host under this well-known name instead.
        var targetHost = attachToNetwork ? target.Host : "host.docker.internal";
        var url = $"http://{targetHost}:{target.Port}/";

        try
        {
            await PullImageIfMissingAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // No registry access and no local copy — nothing to run. Best-effort, not fatal, but
            // logged (unlike the failures below) since a pull failure is the one case worth an
            // operator's attention — everything past this point is expected, routine flakiness.
            _logger.LogWarning(ex, "Failed to pull screenshot image {Image}", _options.ScreenshotImage);
            return null;
        }

        var hostConfig = new HostConfig
        {
            AutoRemove = false,
            ExtraHosts = attachToNetwork ? [] : ["host.docker.internal:host-gateway"],
        };
        NetworkingConfig? networkingConfig = null;
        if (attachToNetwork)
        {
            hostConfig.NetworkMode = _options.DockerNetwork;
            networkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings> { [_options.DockerNetwork] = new EndpointSettings() },
            };
        }

        // The image's own ENTRYPOINT is ["chromium-browser", "--headless"] — these are appended to it.
        var cmd = new List<string>
        {
            "--no-sandbox",
            "--disable-gpu",
            "--disable-dev-shm-usage",
            "--hide-scrollbars",
            $"--window-size={_options.ScreenshotWidth},{_options.ScreenshotHeight}",
            // Lets client-rendered content (Next.js hydration, client-side data fetching) actually
            // paint before the shot is taken, without a real wall-clock sleep on our own side.
            "--virtual-time-budget=4000",
            "--screenshot=screenshot.png",
            url,
        };

        var created = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = _options.ScreenshotImage,
            Cmd = cmd,
            Labels = new Dictionary<string, string> { ["praxy.site.screenshot"] = "true" },
            HostConfig = hostConfig,
            NetworkingConfig = networkingConfig,
        }, ct);

        try
        {
            await _client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ScreenshotTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var waited = await _client.Containers.WaitContainerAsync(created.ID, linked.Token);
            if (waited.StatusCode != 0)
                return null;

            var archive = await _client.Containers.GetArchiveFromContainerAsync(
                created.ID, new ContainerPathStatParameters { Path = "/usr/src/app/screenshot.png" }, false, ct);
            if (archive.Stream is not { } archiveStream)
                return null;
            await using var _ = archiveStream;
            using var tarReader = new TarReader(archiveStream, leaveOpen: false);
            while (await tarReader.GetNextEntryAsync(copyData: true, ct) is { } entry)
            {
                if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                    continue;
                using var buffer = new MemoryStream();
                await entry.DataStream.CopyToAsync(buffer, ct);
                return buffer.ToArray();
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Covers our own ScreenshotTimeoutSeconds firing (not the caller's ct), a page that
            // crashed instead of rendering, and the archive endpoint 404ing because chromium never
            // wrote the file — all of these mean "no screenshot this time," never a hard failure.
            // Debug, not Warning: routine enough (a cold container just needing another attempt)
            // that Warning-level would be noise — turn on Debug when actually investigating.
            _logger.LogDebug(ex, "Screenshot capture failed for container {ContainerId}", created.ID);
            return null;
        }
        finally
        {
            await StopAndRemoveAsync(created.ID, CancellationToken.None);
        }
    }

    /// <summary>Pulls <see cref="SitesOptions.ScreenshotImage"/> on first use — a third-party image, unlike a site's own Dockerfile-built one, so nothing else in this codebase ever pulls it implicitly.</summary>
    private async Task PullImageIfMissingAsync(CancellationToken ct)
    {
        try
        {
            await _client.Images.InspectImageAsync(_options.ScreenshotImage, ct);
            return;
        }
        catch (DockerImageNotFoundException)
        {
            // Not present locally yet — pull below.
        }

        var colon = _options.ScreenshotImage.LastIndexOf(':');
        var repo = colon < 0 ? _options.ScreenshotImage : _options.ScreenshotImage[..colon];
        var tag = colon < 0 ? "latest" : _options.ScreenshotImage[(colon + 1)..];
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag }, null, new Progress<JSONMessage>(), ct);
    }

    public void Dispose()
    {
        _http.Dispose();
        _client.Dispose();
    }
}
