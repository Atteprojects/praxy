namespace Praxy.Sites;

/// <summary>
/// In-memory map of site id → its active deployment's running container address — the Sites
/// equivalent of Functions' <c>WarmPool</c>, but simpler: one entry per site (not per deployment),
/// no idle eviction, no LRU bound, because a site's container is meant to stay running rather than
/// be acquired lazily per-invocation. <see cref="SiteProxyMiddleware"/> reads this on every proxied
/// request — a Docker inspect call per HTTP request would be far too slow, so the address is cached
/// here the moment a container starts and kept current by <see cref="SiteReconciler"/>.
/// </summary>
public sealed class SiteContainerRegistry
{
    private readonly Dictionary<Guid, RunningSiteContainer> _bySite = [];
    private readonly object _lock = new();

    public void Set(Guid siteId, RunningSiteContainer container)
    {
        lock (_lock)
            _bySite[siteId] = container;
    }

    public bool TryGet(Guid siteId, out RunningSiteContainer container)
    {
        lock (_lock)
            return _bySite.TryGetValue(siteId, out container!);
    }

    public void Remove(Guid siteId)
    {
        lock (_lock)
            _bySite.Remove(siteId);
    }
}
