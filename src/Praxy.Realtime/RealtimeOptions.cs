namespace Praxy.Realtime;

/// <summary>
/// A connection cap has no <c>Retry-After</c> equivalent (CLAUDE.md's "every limit is
/// configurable and loud when tripped" rule) — a clear close code and reason on the rejected
/// socket is the loud part here (see <see cref="RealtimeCloseCodes.Overloaded"/>).
/// </summary>
public sealed record RealtimeOptions(int MaxConnectionsPerProject);
