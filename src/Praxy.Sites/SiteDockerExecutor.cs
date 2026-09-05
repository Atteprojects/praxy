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
            // security-review-phase-1: same fix and same reasoning as DockerExecutor's own
            // HostConfig — see its comment. ReadonlyRootfs is deliberately NOT set here: a real
            // Next.js standalone server was tested read-only with a tmpfs /tmp and served
            // correctly, but ISR/image-optimization cache writes under .next/cache were not
            // exercised by that test and are a plausible break — see docs/handoff/security-review-phase-1-report.md.
            PidsLimit = _options.PidsLimit,
            CapDrop = ["ALL"],
            SecurityOpt = ["no-new-privileges"],
            // security-review-phase-1 follow-up (finding F): same reasoning as DockerExecutor —
            // Docker has no portable per-container disk quota, so the writable layer is removed
            // rather than measured. Next.js standalone is the reason this needs two mounts rather
            // than one: /tmp for the usual things, and .next/cache because ISR revalidation and the
            // image optimizer write their caches there at *runtime*. Getting that second mount
            // wrong is what made ReadonlyRootfs look infeasible in the first pass — verified here
            // against a site that actually exercises both, not a static page.
            ReadonlyRootfs = true,
            Tmpfs = new Dictionary<string, string>
            {
                // The container runs as 65534 (SiteRuntimeTemplates' USER directive) and a tmpfs
                // mount defaults to root-owned — /tmp gets the usual sticky world-writable mode,
                // and .next/cache is owned outright by the runtime user, since Next.js creates
                // subdirectories under it rather than just files.
                ["/tmp"] = $"rw,nosuid,nodev,mode=1777,size={_options.TmpfsSizeMb}m",
                ["/app/.next/cache"] = $"rw,nosuid,nodev,uid=65534,gid=65534,size={_options.TmpfsSizeMb}m",
            },
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


    /// <summary>
    /// The repo digest an image reference actually resolved to, or <c>null</c> if it can't be read.
    ///
    /// <para>security-review-phase-1 follow-up (finding E): base images are pinned by <em>tag</em>
    /// (<c>node:22-alpine</c>), which floats within its line. Digest-pinning the default was
    /// considered and rejected — with no auto-update mechanism it would freeze every self-hoster on
    /// one Node build until they bumped it by hand, trading silent drift for silently missing
    /// security patches, which is the worse failure. What was actually wrong was that the drift was
    /// <em>invisible</em>: nothing recorded which image a deployment was built against, so "did this
    /// build pick up a new base?" was unanswerable after the fact. Recording the digest in the build
    /// log makes it answerable, and an operator who does want a frozen base can set
    /// <c>Praxy:Sites:NodeBaseImage</c> to a
    /// <c>name@sha256:...</c> reference — that already works, and is now documented.</para>
    /// </summary>
    public async Task<string?> TryResolveImageDigestAsync(string imageRef, CancellationToken ct)
    {
        try
        {
            var image = await _client.Images.InspectImageAsync(imageRef, ct);
            return image.RepoDigests is { Count: > 0 } digests ? digests[0] : image.ID;
        }
        catch
        {
            // Best-effort provenance, never a build failure.
            return null;
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

    public void Dispose()
    {
        _http.Dispose();
        _client.Dispose();
    }
}
