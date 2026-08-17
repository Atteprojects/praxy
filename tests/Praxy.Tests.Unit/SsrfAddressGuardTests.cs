using Praxy.Core.Net;

namespace Praxy.Tests.Unit;

/// <summary>
/// <see cref="SsrfAddressGuard.EnsureHostResolvesToAnAllowedAddressAsync"/> — the pre-connect check
/// <see cref="Praxy.Auth.SmtpEmailSender"/> uses (Phase 9's security pass found the SMTP provider
/// path had no SSRF protection at all; <see cref="Praxy.Webhooks.SsrfGuard"/>'s own tests already
/// cover <see cref="SsrfAddressGuard.IsBlocked"/>, the shared predicate both guards now call).
/// IP-literal hosts exercise the fast path with no real DNS lookup, keeping this test deterministic.
/// </summary>
public class SsrfAddressGuardTests
{
    [Fact]
    public async Task A_private_ip_literal_is_rejected_by_default()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SsrfAddressGuard.EnsureHostResolvesToAnAllowedAddressAsync("127.0.0.1", allowPrivateNetworkTargets: false, CancellationToken.None));
    }

    [Fact]
    public async Task The_cloud_metadata_address_is_rejected_by_default()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SsrfAddressGuard.EnsureHostResolvesToAnAllowedAddressAsync("169.254.169.254", allowPrivateNetworkTargets: false, CancellationToken.None));
    }

    [Fact]
    public async Task A_public_ip_literal_is_allowed()
    {
        await SsrfAddressGuard.EnsureHostResolvesToAnAllowedAddressAsync("8.8.8.8", allowPrivateNetworkTargets: false, CancellationToken.None); // does not throw
    }

    [Fact]
    public async Task AllowPrivateNetworkTargets_opts_a_private_host_back_in()
    {
        await SsrfAddressGuard.EnsureHostResolvesToAnAllowedAddressAsync("10.0.0.5", allowPrivateNetworkTargets: true, CancellationToken.None); // does not throw
    }
}
