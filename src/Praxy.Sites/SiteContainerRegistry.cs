namespace Praxy.Sites;

/// <summary>One running site container — <c>SiteContainerRegistry</c>'s unit of tracking.</summary>
public sealed record RunningSiteContainer(string ContainerId, string Host, int Port);

/// <summary>
/// In-memory map of <em>deployment</em> id → its running container address. Phase 1 kept this keyed by
/// site id ("one entry per site, no eviction") because only a site's active deployment ever ran; Phase
/// 2 adds on-demand preview containers for any <c>ready</c> deployment, so the same site can have
/// several containers live at once — the natural key is the deployment, not the site. Callers that
/// want "the site's current container" resolve <c>site.ActiveDeploymentId</c> first, then look that up
/// here, the same as any other deployment.
///
/// Every successful <see cref="TryGet"/> refreshes the entry's last-used timestamp — that's what
/// "idle" means for <see cref="SitePreviewSweeper"/>'s purposes: no proxied request has resolved to
/// this deployment recently. The active deployment's entry gets touched too (every proxied production
/// request, every <c>IsRunning</c> poll) but is never swept regardless, since the sweeper only removes
/// entries that are nobody's <c>ActiveDeploymentId</c> — see its own remarks.
///
/// Also owns the per-deployment start lock <see cref="StartOrJoinAsync"/> uses: starting a container
/// from inside <see cref="SiteProxyMiddleware"/> on a request thread (a new pattern for this codebase
/// — see docs/handoff/sites-phase-2-prompt.md's landmines) needs real concurrency control so two
/// simultaneous first-requests to the same cold preview don't race to start two containers. Co-located
/// here rather than a separate singleton because both concerns are "bookkeeping for one deployment's
/// container lifecycle" against the same in-memory state.
/// </summary>
public sealed class SiteContainerRegistry
{
    private sealed record Entry(Guid SiteId, RunningSiteContainer Container, DateTimeOffset LastUsedAt);

    private readonly Dictionary<Guid, Entry> _byDeployment = [];
    private readonly Dictionary<Guid, SemaphoreSlim> _startGates = [];
    private readonly Dictionary<string, SemaphoreSlim> _projectGates = [];
    private readonly object _lock = new();

    /// <summary>
    /// <paramref name="siteId"/> is recorded alongside the container so a later <see cref="TryGet"/>
    /// can prove ownership without a DB round trip — see that method's remarks for why this matters.
    /// </summary>
    public void Set(Guid deploymentId, Guid siteId, RunningSiteContainer container)
    {
        lock (_lock)
            _byDeployment[deploymentId] = new Entry(siteId, container, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Returns the running container for <paramref name="deploymentId"/> only if it was registered
    /// under <paramref name="expectedSiteId"/>. This id is mandatory, not defense-in-depth: the whole
    /// map is a single global, unpartitioned dictionary holding every project's production and warm
    /// preview containers at once, and <see cref="SiteProxyMiddleware"/>'s preview path resolves
    /// <paramref name="deploymentId"/> straight out of an attacker-suppliable hostname label. Without
    /// this check, a hit for a deployment id that happens to belong to a *different* site (guessed,
    /// leaked, or observed anywhere — the id itself needs no protecting) forwards that other site's
    /// live traffic to the caller regardless of which project's hostname they used to ask, since the
    /// cold-start branch's own <c>SiteId</c> check only runs on a registry miss. A caller can only
    /// ever reach a container they already know both a valid deployment id for <em>and</em> the
    /// correct owning site for.
    /// </summary>
    public bool TryGet(Guid deploymentId, Guid expectedSiteId, out RunningSiteContainer container)
    {
        lock (_lock)
        {
            if (_byDeployment.TryGetValue(deploymentId, out var entry) && entry.SiteId == expectedSiteId)
            {
                _byDeployment[deploymentId] = entry with { LastUsedAt = DateTimeOffset.UtcNow };
                container = entry.Container;
                return true;
            }
        }
        container = null!;
        return false;
    }

    public void Remove(Guid deploymentId)
    {
        lock (_lock)
            _byDeployment.Remove(deploymentId);
    }

    public bool TryRemove(Guid deploymentId, out RunningSiteContainer container)
    {
        lock (_lock)
        {
            if (_byDeployment.Remove(deploymentId, out var entry))
            {
                container = entry.Container;
                return true;
            }
        }
        container = null!;
        return false;
    }

    /// <summary>Removes and returns the entry only if it is still idle as of <paramref name="cutoff"/> at the exact moment of removal — guards the sweeper against a race with a proxied request that touched it a moment earlier, which would otherwise stop a container mid-use.</summary>
    public bool TryRemoveIfIdle(Guid deploymentId, DateTimeOffset cutoff, out RunningSiteContainer container)
    {
        lock (_lock)
        {
            if (_byDeployment.TryGetValue(deploymentId, out var entry) && entry.LastUsedAt < cutoff)
            {
                _byDeployment.Remove(deploymentId);
                container = entry.Container;
                return true;
            }
        }
        container = null!;
        return false;
    }

    /// <summary>Deployment ids currently tracked whose last-used timestamp is older than <paramref name="cutoff"/> — <see cref="SitePreviewSweeper"/>'s own candidate list, still to be filtered against every site's active deployment (and re-checked atomically via <see cref="TryRemoveIfIdle"/>) before anything is actually stopped.</summary>
    public List<Guid> IdleSince(DateTimeOffset cutoff)
    {
        lock (_lock)
            return [.. _byDeployment.Where(kv => kv.Value.LastUsedAt < cutoff).Select(kv => kv.Key)];
    }

    public List<Guid> TrackedDeploymentIds()
    {
        lock (_lock)
            return [.. _byDeployment.Keys];
    }

    /// <summary>
    /// Every tracked container regardless of owning site — cleanup/ops tooling only (a test fixture
    /// tearing down every container it started, an eventual admin endpoint), never anything on the
    /// request path. <see cref="TryGet"/> is the one to reach for there; its <c>expectedSiteId</c>
    /// check is exactly what this method deliberately skips.
    /// </summary>
    public List<RunningSiteContainer> AllContainers()
    {
        lock (_lock)
            return [.. _byDeployment.Values.Select(e => e.Container)];
    }

    /// <summary>
    /// Serializes <em>preview cold starts within one project</em>, so the per-project preview quota
    /// check and the registration that is supposed to satisfy it cannot interleave.
    ///
    /// <para>Without this, <c>QuotaService.EnsurePreviewQuotaAsync</c> reads
    /// <see cref="TrackedDeploymentIds"/>, and only later does <see cref="StartOrJoinAsync"/> add the
    /// new entry — so two concurrent cold starts for <em>different</em> deployments in the same
    /// project both saw a count below the cap and both started (the TOCTOU the quota's own doc
    /// comment recorded as accepted). An untrusted developer scripting concurrent preview requests
    /// could push well past <c>MaxPreviewContainersPerProject</c>, spending host-wide Docker capacity
    /// that isn't theirs.</para>
    ///
    /// <para><b>Lock ordering:</b> this gate is always taken <em>before</em> the per-deployment gate
    /// in <see cref="StartOrJoinAsync"/>, never after. Nothing acquires them in the other order, so
    /// the two cannot deadlock — keep it that way.</para>
    ///
    /// <para>Only cold starts serialize: the proxy's warm path (<see cref="TryGet"/>) never reaches
    /// here, so an already-running preview is unaffected, and different projects never wait on each
    /// other.</para>
    /// </summary>
    public async Task<IDisposable> EnterProjectColdStartAsync(string projectId, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_projectGates.TryGetValue(projectId, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                _projectGates[projectId] = gate;
            }
        }

        await gate.WaitAsync(ct);
        return new GateReleaser(gate);
    }

    private sealed class GateReleaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }

    /// <summary>
    /// Starts (or joins an in-flight start of) a cold deployment's container. Two concurrent callers
    /// for the same <paramref name="deploymentId"/> serialize on a per-deployment gate — the first one
    /// through actually runs <paramref name="start"/>; the rest wait for it to finish and then read
    /// whatever it registered, rather than each starting their own container. Bounded by the caller's
    /// <paramref name="ct"/> (expected to already carry a <c>StartupTimeoutSeconds</c> deadline).
    /// </summary>
    public async Task<RunningSiteContainer> StartOrJoinAsync(
        Guid deploymentId, Guid siteId, Func<CancellationToken, Task<RunningSiteContainer>> start,
        CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_startGates.TryGetValue(deploymentId, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                _startGates[deploymentId] = gate;
            }
        }

        await gate.WaitAsync(ct);
        try
        {
            // Another request may have already cold-started this deployment while we waited for
            // the gate — join its result instead of starting a second container.
            if (TryGet(deploymentId, siteId, out var already))
                return already;

            var container = await start(ct);
            Set(deploymentId, siteId, container);
            return container;
        }
        finally
        {
            gate.Release();
        }
    }
}
