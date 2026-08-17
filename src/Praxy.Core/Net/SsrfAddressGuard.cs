using System.Net;
using System.Net.Sockets;

namespace Praxy.Core.Net;

/// <summary>
/// The private/loopback/link-local/multicast address-range predicate behind every outbound-
/// connection SSRF guard in Praxy (architecture.md §11's threat model). Defined once here so
/// <c>Praxy.Webhooks.SsrfGuard</c> (Phase 6, HTTP delivery) and the SMTP provider guard (Phase 9 —
/// found unprotected during the hardening security pass, see docs/handoff/phase-9-report.md) share
/// the exact same range table rather than risking two guards drifting apart.
/// </summary>
public static class SsrfAddressGuard
{
    public static bool IsBlocked(IPAddress address)
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
    /// Resolves <paramref name="host"/> and throws when every candidate address is blocked (or
    /// resolution fails) — the pre-connect check a transport that can't take a custom connect
    /// callback (like <see cref="System.Net.Mail.SmtpClient"/>) falls back to. Weaker than
    /// resolve-once-and-connect-to-that-address (a DNS-rebinding race between this check and the
    /// transport's own connect is theoretically possible), but closes the overwhelmingly common
    /// case — a static private-IP or internal-hostname target — which had no protection at all
    /// before Phase 9.
    /// </summary>
    public static async Task EnsureHostResolvesToAnAllowedAddressAsync(
        string host, bool allowPrivateNetworkTargets, CancellationToken ct)
    {
        if (allowPrivateNetworkTargets)
            return;

        var resolved = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        if (resolved.Length == 0 || resolved.All(IsBlocked))
            throw new InvalidOperationException(
                resolved.Length == 0
                    ? $"'{host}' did not resolve to any address."
                    : $"'{host}' resolves only to addresses blocked by the SSRF guard.");
    }
}
