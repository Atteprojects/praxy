using System.Net;
using System.Net.Sockets;
using Praxy.Core.Net;

namespace Praxy.Webhooks;

/// <summary>
/// The webhook delivery client's connect-time SSRF guard (architecture.md §11 threat model,
/// roadmap.md Phase 6 scope: "deny private/loopback/link-local ranges by default; self-host config
/// can allow them for reverse-proxied internal targets"). Resolves DNS once and connects directly to
/// the resolved address rather than letting the HTTP stack resolve-then-connect on its own — that's
/// what closes the DNS-rebinding gap a header/URL-only check would leave open (a hostname that
/// resolves to a public IP at validation time and a private one at connect time).
/// </summary>
public static class SsrfGuard
{
    /// <summary>The shared range predicate (<see cref="SsrfAddressGuard"/>) — kept as a same-named passthrough so nothing calling this class needs to change.</summary>
    public static bool IsBlockedAddress(IPAddress address) => SsrfAddressGuard.IsBlocked(address);

    /// <summary>
    /// Resolves <paramref name="host"/> and connects to the first candidate address that passes the
    /// guard (every address, unfiltered, when <paramref name="allowPrivateNetworkTargets"/> is set).
    /// Throws when resolution fails or every candidate is blocked/unreachable.
    /// </summary>
    public static async Task<Stream> ConnectAsync(
        string host, int port, bool allowPrivateNetworkTargets, CancellationToken ct)
    {
        var resolved = await Dns.GetHostAddressesAsync(host, ct);
        var candidates = allowPrivateNetworkTargets
            ? resolved
            : [.. resolved.Where(a => !IsBlockedAddress(a))];
        if (candidates.Length == 0)
            throw new InvalidOperationException(
                resolved.Length == 0
                    ? $"'{host}' did not resolve to any address."
                    : $"'{host}' resolves only to addresses blocked by the webhook SSRF guard.");

        Exception? last = null;
        foreach (var address in candidates)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
