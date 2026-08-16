using System.Net;
using Praxy.Webhooks;

namespace Praxy.Tests.Unit;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("169.254.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")] // multicast
    [InlineData("240.0.0.1")] // reserved
    [InlineData("::1")]
    [InlineData("fe80::1")] // link-local
    [InlineData("fc00::1")] // unique local
    [InlineData("fd00::1")] // unique local
    public void Private_loopback_link_local_and_multicast_addresses_are_blocked(string address) =>
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")] // just outside 172.16.0.0/12
    [InlineData("172.32.0.0")]     // just outside 172.16.0.0/12
    [InlineData("169.253.255.255")] // just outside 169.254.0.0/16
    [InlineData("2606:4700:4700::1111")] // public IPv6 (Cloudflare)
    public void Public_addresses_are_not_blocked(string address) =>
        Assert.False(SsrfGuard.IsBlockedAddress(IPAddress.Parse(address)));

    [Fact]
    public void IPv4_mapped_IPv6_loopback_is_blocked()
    {
        // ::ffff:127.0.0.1 — a naive check that only inspects the outer AddressFamily would miss this.
        var mapped = IPAddress.Parse("127.0.0.1").MapToIPv6();
        Assert.True(SsrfGuard.IsBlockedAddress(mapped));
    }

    [Fact]
    public async Task ConnectAsync_throws_when_every_resolved_candidate_is_blocked()
    {
        // "localhost" always resolves to a loopback address, which the default (non-self-host-override)
        // guard must reject before ever attempting a TCP connect.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SsrfGuard.ConnectAsync("localhost", 80, allowPrivateNetworkTargets: false, CancellationToken.None));
    }
}
