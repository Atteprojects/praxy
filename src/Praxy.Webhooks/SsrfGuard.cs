using System.Net;
using System.Net.Sockets;

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
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0) return true;                             // 0.0.0.0/8 — "this network"
            if (b[0] == 10) return true;                            // 10.0.0.0/8
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;             // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;             // 169.254.0.0/16 — link-local, incl. cloud metadata (169.254.169.254)
            if (b[0] >= 224) return true;                            // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return true;
            // fc00::/7 — unique local addresses (IsIPv6SiteLocal only covers the deprecated fec0::/10 form).
            if ((address.GetAddressBytes()[0] & 0xfe) == 0xfc)
                return true;
            return false;
        }

        return true; // unknown address family: deny by default
    }

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
