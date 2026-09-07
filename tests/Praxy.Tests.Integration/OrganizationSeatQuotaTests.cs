using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Organizations-phase-2 review: membership became creatable this phase and had no ceiling. Its own
/// class rather than a case in <see cref="OrganizationMembershipApiTests"/> because proving the cap
/// means *setting* it low, and a class-wide seat limit would silently constrain any future test
/// added to that file — a landmine for whoever writes Phase 3's.
/// </summary>
public class OrganizationSeatQuotaTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const string InviteUrl = "https://console.praxy.test/accept-invite";

    // Small enough to reach quickly; the production default (25) proves the same property with
    // twenty-two more invites.
    private const int MaxMembers = 3;

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Quotas:MaxMembersPerOrganization"] = "3",
    };

    /// <summary>
    /// Organizations-phase-2 review: membership became creatable this phase and had no ceiling.
    /// The route's auth-email rate limit bounds outbound mail per window but not the total, and
    /// every other creatable resource in QuotaOptions has a cap — under managed hosting this is also
    /// the dimension a plan is sold by. Counts pending invites too: an unaccepted one has already
    /// created a console User row and sent an email, so it has spent what this bounds.
    /// </summary>
    [Fact]
    public async Task An_organization_cannot_invite_past_its_seat_quota()
    {
        var (ownerToken, _) = await ClaimAsync();
        var organizationId = await MyOrganizationIdAsync(ownerToken);

        // Signup already made the owner, so this reaches the cap exactly — and none of these are
        // accepted, proving a *pending* invite consumes a seat.
        for (var i = 2; i <= MaxMembers; i++)
        {
            var invited = await Client.SendAsync(Authed(HttpMethod.Post,
                $"/v1/console/organizations/{organizationId}/members", ownerToken,
                new { email = $"seat{i}@example.com", role = "member", url = InviteUrl }));
            Assert.Equal(201, (int)invited.StatusCode);
        }

        var refused = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/organizations/{organizationId}/members", ownerToken,
            new { email = "one.too.many@example.com", role = "member", url = InviteUrl }));
        Assert.Equal(400, (int)refused.StatusCode);
        Assert.Equal(ErrorTypes.GeneralResourceLimitExceeded,
            (await ReadJson(refused)).GetProperty("type").GetString());

        // The properties that matter, not the status code: no membership row was added, and — since
        // InviteAsync is what creates the console account and sends the mail — the refused address
        // never received an email either, proving the check runs before any of that.
        var members = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/organizations/{organizationId}/members", ownerToken)));
        Assert.Equal(MaxMembers, members.GetProperty("total").GetInt32());
        Assert.DoesNotContain(Email.Sent, m => m.To.Contains("one.too.many@example.com"));
    }

    private async Task<string> MyOrganizationIdAsync(string token)
    {
        var list = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", token)));
        return list.GetProperty("organizations")[0].GetProperty("id").GetString()!;
    }
}
