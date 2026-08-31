using System.Threading.Channels;

namespace Praxy.Sites;

/// <summary>One proxied request, captured on the response path — <see cref="SiteRequestLogWorker"/>'s unit of work.</summary>
public sealed record SiteRequestLogEntry(
    Guid SiteId, string ProjectId, Guid? DeploymentId, string Method, string Path, int StatusCode,
    int DurationMs, DateTimeOffset CreatedAt);

/// <summary>
/// Sits between <see cref="SiteProxyMiddleware"/> (the writer) and <see cref="SiteRequestLogWorker"/>
/// (the reader) so logging a request never costs the request itself a synchronous DB round trip —
/// docs/handoff/sites-request-logs-prompt.md's own landmine: "Sites serving traffic must never be
/// slowed down or failed by logging pressure." <see cref="TryEnqueue"/> never blocks and never
/// throws: a full channel just drops the entry (<see cref="BoundedChannelFullMode.DropWrite"/>) rather
/// than applying backpressure to the proxy — this is best-effort observability, not a durability
/// guarantee, the same distinction the prompt draws explicitly.
/// </summary>
public sealed class SiteRequestLogWriter
{
    private readonly Channel<SiteRequestLogEntry> _channel;

    public SiteRequestLogWriter(SitesOptions options)
    {
        _channel = Channel.CreateBounded<SiteRequestLogEntry>(new BoundedChannelOptions(options.RequestLogChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<SiteRequestLogEntry> Reader => _channel.Reader;

    public void TryEnqueue(SiteRequestLogEntry entry) => _channel.Writer.TryWrite(entry);
}
