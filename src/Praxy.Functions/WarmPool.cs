namespace Praxy.Functions;

internal sealed record WarmEntry(RunningContainer Container, Guid DeploymentId, DateTimeOffset LastUsedAt);

/// <summary>
/// Keeps recently-used function containers alive across invocations, keyed by deployment id, so
/// the next invocation of a just-used function skips the cold start. Bounded to
/// <see cref="FunctionsOptions.WarmPoolSize"/> (LRU-evicted past that) and swept for idle entries
/// past <see cref="FunctionsOptions.MaxIdleSeconds"/> by <see cref="FunctionPoolSweeper"/> — a
/// silent cold pool is exactly the kind of "limit not loud when tripped" CLAUDE.md rules out, so
/// <see cref="IsWarm"/> is exposed for the execution record to note whether an invocation paid the
/// cold-start cost, and both knobs are configurable.
/// </summary>
public sealed class WarmPool(DockerExecutor docker, FunctionsOptions options) : IAsyncDisposable
{
    private readonly Dictionary<Guid, WarmEntry> _byDeployment = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Stops every warm container on shutdown — otherwise every restart leaks whatever was warm at the time, in dev and in production alike.</summary>
    public async ValueTask DisposeAsync()
    {
        List<WarmEntry> entries;
        await _lock.WaitAsync();
        try
        {
            entries = [.. _byDeployment.Values];
            _byDeployment.Clear();
        }
        finally
        {
            _lock.Release();
        }

        foreach (var entry in entries)
        {
            try
            {
                await docker.StopAndRemoveAsync(entry.Container.ContainerId, CancellationToken.None);
            }
            catch
            {
                // Best-effort on shutdown — nothing left to report to.
            }
        }
    }

    public bool IsWarm(Guid deploymentId)
    {
        _lock.Wait();
        try
        {
            return _byDeployment.ContainsKey(deploymentId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public int Count
    {
        get
        {
            _lock.Wait();
            try
            {
                return _byDeployment.Count;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    public async Task<RunningContainer> AcquireAsync(
        Guid deploymentId, string imageTag, IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_byDeployment.TryGetValue(deploymentId, out var existing))
            {
                _byDeployment[deploymentId] = existing with { LastUsedAt = DateTimeOffset.UtcNow };
                return existing.Container;
            }
        }
        finally
        {
            _lock.Release();
        }

        // The cold start itself happens outside the lock — a slow container boot for one function
        // must not block warm-pool lookups for every other function.
        var container = await docker.StartContainerAsync(imageTag, envVars, deploymentId.ToString(), ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_byDeployment.TryGetValue(deploymentId, out var raced))
            {
                // Another caller cold-started the same deployment concurrently — keep theirs,
                // discard ours so we don't leak a container the pool never tracks.
                _ = docker.StopAndRemoveAsync(container.ContainerId, CancellationToken.None);
                return raced.Container;
            }

            _byDeployment[deploymentId] = new WarmEntry(container, deploymentId, DateTimeOffset.UtcNow);
            await EvictOverflowAsync();
            return container;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forces eviction (used when a deployment is superseded or deleted) — a stale container must not keep serving invocations for code that's no longer active.</summary>
    public async Task EvictAsync(Guid deploymentId, CancellationToken ct)
    {
        WarmEntry? removed = null;
        await _lock.WaitAsync(ct);
        try
        {
            if (_byDeployment.Remove(deploymentId, out var entry))
                removed = entry;
        }
        finally
        {
            _lock.Release();
        }
        if (removed is not null)
            await docker.StopAndRemoveAsync(removed.Container.ContainerId, ct);
    }

    public async Task SweepIdleAsync(CancellationToken ct)
    {
        List<WarmEntry> idle;
        await _lock.WaitAsync(ct);
        try
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(options.MaxIdleSeconds);
            idle = [.. _byDeployment.Values.Where(e => e.LastUsedAt < cutoff)];
            foreach (var e in idle)
                _byDeployment.Remove(e.DeploymentId);
        }
        finally
        {
            _lock.Release();
        }

        foreach (var e in idle)
            await docker.StopAndRemoveAsync(e.Container.ContainerId, ct);
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private async Task EvictOverflowAsync()
    {
        if (_byDeployment.Count <= options.WarmPoolSize)
            return;
        var overflow = _byDeployment.Values
            .OrderBy(e => e.LastUsedAt)
            .Take(_byDeployment.Count - options.WarmPoolSize)
            .ToList();
        foreach (var e in overflow)
            _byDeployment.Remove(e.DeploymentId);
        foreach (var e in overflow)
            await docker.StopAndRemoveAsync(e.Container.ContainerId, CancellationToken.None);
    }
}
