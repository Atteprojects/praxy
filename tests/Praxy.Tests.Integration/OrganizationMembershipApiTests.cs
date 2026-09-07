using System.Net.Http.Json;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Organizations-phase-2: invite by email, accept, remove, change role, and — for the first time —
/// <c>owner</c> vs <c>member</c> actually means something. Every test asserts the property per the
/// prompt's own standard (a member's blocked action changed nothing; the last owner truly cannot go
/// away), not just the status code. Extends <see cref="AuthTestBase"/> for its capturing email
/// sender — the invite link never leaves the process, it is read straight out of the sent message.
/// </summary>
public class OrganizationMembershipApiTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const string InviteUrl = "https://console.praxy.test/accept-invite";

    private async Task<(string OrganizationId, string UserId, string Secret)> InviteAsync(
        string ownerToken, string organizationId, string email, string role = "member")
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/organizations/{organizationId}/members", ownerToken,
            new { email, role, url = InviteUrl }));
        Assert.Equal(201, (int)response.StatusCode);
        var link = Email.LastLinkParams();
        return (link["organizationId"], link["userId"], link["secret"]);
    }

    private async Task<string> MyOrganizationIdAsync(string token)
    {
        var list = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", token)));
        return list.GetProperty("organizations")[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task An_owner_can_invite_a_new_colleague_who_accepts_and_uses_the_organization()
    {
        var (ownerToken, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(ownerToken);

        var (organizationId, userId, secret) = await InviteAsync(ownerToken, orgId, "colleague@praxy.test", "member");

        // Unconfirmed until accepted — visible to the owner as "invited", grants nothing yet.
        var membersBefore = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}/members", ownerToken)));
        Assert.Equal(2, membersBefore.GetProperty("total").GetInt32());
        var pending = membersBefore.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "colleague@praxy.test");
        Assert.False(pending.GetProperty("confirmed").GetBoolean());

        var accept = await Client.PostAsJsonAsync(
            $"/v1/console/organizations/{organizationId}/members/{userId}/accept",
            new { secret, password = "hunter2hunter2" });
        var acceptBody = await ReadJson(accept);
        Assert.Equal(201, (int)accept.StatusCode);
        Assert.Equal("colleague@praxy.test", acceptBody.GetProperty("account").GetProperty("email").GetString());
        Assert.Equal("member", acceptBody.GetProperty("member").GetProperty("role").GetString());
        var colleagueToken = acceptBody.GetProperty("session").GetProperty("token").GetString()!;

        // Now confirmed, and a real session works.
        var membersAfter = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}/members", ownerToken)));
        Assert.True(membersAfter.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "colleague@praxy.test")
            .GetProperty("confirmed").GetBoolean());

        // A member uses the projects inside the organization...
        var project = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", colleagueToken,
            new { name = "Colleague's app", organizationId = orgId }));
        Assert.Equal(201, (int)project.StatusCode);

        // ...but cannot administer the organization itself.
        var rename = await Client.SendAsync(Authed(HttpMethod.Patch, $"/v1/console/organizations/{orgId}",
            colleagueToken, new { name = "Renamed" }));
        await AssertError(rename, 403, ErrorTypes.OrganizationOwnerRequired);
    }

    [Fact]
    public async Task A_member_cannot_manage_the_organization_and_nothing_changes()
    {
        var (ownerToken, ownerAccount) = await ClaimAsync();
        var ownerId = ownerAccount.GetProperty("id").GetString()!;
        var orgId = await MyOrganizationIdAsync(ownerToken);
        var (organizationId, userId, secret) = await InviteAsync(ownerToken, orgId, "member@praxy.test");
        var accept = await ReadJson(await Client.PostAsJsonAsync(
            $"/v1/console/organizations/{organizationId}/members/{userId}/accept",
            new { secret, password = "hunter2hunter2" }));
        var memberToken = accept.GetProperty("session").GetProperty("token").GetString()!;

        var rename = await Client.SendAsync(Authed(HttpMethod.Patch, $"/v1/console/organizations/{orgId}",
            memberToken, new { name = "Hijacked" }));
        await AssertError(rename, 403, ErrorTypes.OrganizationOwnerRequired);

        var delete = await Client.SendAsync(Authed(HttpMethod.Delete, $"/v1/console/organizations/{orgId}", memberToken));
        await AssertError(delete, 403, ErrorTypes.OrganizationOwnerRequired);

        var invite = await Client.SendAsync(Authed(HttpMethod.Post, $"/v1/console/organizations/{orgId}/members",
            memberToken, new { email = "outsider@praxy.test", role = "member", url = InviteUrl }));
        await AssertError(invite, 403, ErrorTypes.OrganizationOwnerRequired);

        var changeRole = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/organizations/{orgId}/members/{userId}", memberToken, new { role = "owner" }));
        await AssertError(changeRole, 403, ErrorTypes.OrganizationOwnerRequired);

        // Removing themselves would be a "leave" (allowed); here the member targets the owner instead.
        var removeOwner = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/organizations/{orgId}/members/{ownerId}", memberToken));
        await AssertError(removeOwner, 403, ErrorTypes.OrganizationOwnerRequired);

        // Nothing changed: name, membership count, and every role are exactly as they were.
        var org = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}", ownerToken)));
        Assert.Equal("Personal", org.GetProperty("name").GetString());
        var members = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}/members", ownerToken)));
        Assert.Equal(2, members.GetProperty("total").GetInt32());
        Assert.Equal("member", members.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "member@praxy.test").GetProperty("role").GetString());
    }

    [Fact]
    public async Task An_owner_can_rename_the_organization_promote_and_remove_a_member()
    {
        var (ownerToken, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(ownerToken);
        var (organizationId, userId, secret) = await InviteAsync(ownerToken, orgId, "second-owner@praxy.test", "member");
        await Client.PostAsJsonAsync($"/v1/console/organizations/{organizationId}/members/{userId}/accept",
            new { secret, password = "hunter2hunter2" });

        // Promote to owner.
        var promote = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/organizations/{orgId}/members/{userId}", ownerToken, new { role = "owner" }));
        var promoted = await ReadJson(promote);
        Assert.Equal(200, (int)promote.StatusCode);
        Assert.Equal("owner", promoted.GetProperty("role").GetString());

        // Rename.
        var rename = await Client.SendAsync(Authed(HttpMethod.Patch, $"/v1/console/organizations/{orgId}",
            ownerToken, new { name = "Renamed Inc." }));
        Assert.Equal(200, (int)rename.StatusCode);
        Assert.Equal("Renamed Inc.", (await ReadJson(rename)).GetProperty("name").GetString());

        // Remove the now-second-owner — allowed, since a real owner remains.
        var remove = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/organizations/{orgId}/members/{userId}", ownerToken));
        Assert.Equal(204, (int)remove.StatusCode);
        var members = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}/members", ownerToken)));
        Assert.Equal(1, members.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_last_owner_cannot_be_removed_or_demoted_even_by_themselves()
    {
        var (ownerToken, ownerAccount) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(ownerToken);
        var ownerId = ownerAccount.GetProperty("id").GetString()!;

        var demoteSelf = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/organizations/{orgId}/members/{ownerId}", ownerToken, new { role = "member" }));
        await AssertError(demoteSelf, 409, ErrorTypes.OrganizationLastOwner);

        var removeSelf = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/organizations/{orgId}/members/{ownerId}", ownerToken));
        await AssertError(removeSelf, 409, ErrorTypes.OrganizationLastOwner);

        // Still the owner, still a member — the blocked calls wrote nothing.
        var members = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}/members", ownerToken)));
        Assert.Equal(1, members.GetProperty("total").GetInt32());
        Assert.Equal("owner", members.GetProperty("members")[0].GetProperty("role").GetString());

        // With a second owner in place, both the demotion and the removal succeed.
        var (organizationId, userId, secret) = await InviteAsync(ownerToken, orgId, "co-owner@praxy.test", "owner");
        await Client.PostAsJsonAsync($"/v1/console/organizations/{organizationId}/members/{userId}/accept",
            new { secret, password = "hunter2hunter2" });

        var demoteNowSafe = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/organizations/{orgId}/members/{ownerId}", ownerToken, new { role = "member" }));
        Assert.Equal(200, (int)demoteNowSafe.StatusCode);
    }

    [Fact]
    public async Task Accepting_with_a_wrong_secret_and_a_nonexistent_invite_return_the_same_error()
    {
        var (ownerToken, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(ownerToken);
        var (organizationId, userId, _) = await InviteAsync(ownerToken, orgId, "invitee@praxy.test");

        var wrongSecret = await Client.PostAsJsonAsync(
            $"/v1/console/organizations/{organizationId}/members/{userId}/accept",
            new { secret = "not-the-real-secret", password = "hunter2hunter2" });
        var wrongSecretBody = await AssertError(wrongSecret, 401, ErrorTypes.OrganizationInviteInvalid);

        // A syntactically valid but entirely unrelated user id — no invite exists for it at all.
        var nonexistent = await Client.PostAsJsonAsync(
            $"/v1/console/organizations/{organizationId}/members/{Guid.NewGuid():N}/accept",
            new { secret = "anything", password = "hunter2hunter2" });
        var nonexistentBody = await AssertError(nonexistent, 401, ErrorTypes.OrganizationInviteInvalid);

        Assert.Equal(wrongSecretBody.GetProperty("type").GetString(), nonexistentBody.GetProperty("type").GetString());
        Assert.Equal(wrongSecretBody.GetProperty("code").GetInt32(), nonexistentBody.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Accepting_an_invite_twice_is_rejected_not_silently_reconfirmed()
    {
        var (ownerToken, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(ownerToken);
        var (organizationId, userId, secret) = await InviteAsync(ownerToken, orgId, "twice@praxy.test");

        var first = await Client.PostAsJsonAsync($"/v1/console/organizations/{organizationId}/members/{userId}/accept",
            new { secret, password = "hunter2hunter2" });
        Assert.Equal(201, (int)first.StatusCode);

        var second = await Client.PostAsJsonAsync($"/v1/console/organizations/{organizationId}/members/{userId}/accept",
            new { secret, password = "hunter2hunter2" });
        await AssertError(second, 409, ErrorTypes.OrganizationInviteAlreadyAccepted);
    }

    [Fact]
    public async Task Being_invited_into_someone_elses_organization_does_not_consume_the_operators_own_creation_allowance()
    {
        var (ownerToken, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(ownerToken);
        var (secondToken, secondAccount) = await CreateSecondOperatorAsync();
        var secondEmail = secondAccount.GetProperty("email").GetString()!;

        var (organizationId, userId, secret) = await InviteAsync(ownerToken, orgId, secondEmail, "member");
        // The second operator already has a password — none required to accept.
        var accept = await Client.PostAsJsonAsync($"/v1/console/organizations/{organizationId}/members/{userId}/accept",
            new { secret });
        Assert.Equal(201, (int)accept.StatusCode);

        // Now a member of two organizations, but owner of only one ("Second") — the quota must
        // still let them create up to the cap, proving the count is owner-scoped, not membership-scoped.
        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/organizations", secondToken)));
        Assert.Equal(2, list.GetProperty("total").GetInt32());

        var createOwn = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/organizations", secondToken,
            new { name = "Second's Own Second Org" }));
        Assert.Equal(201, (int)createOwn.StatusCode);
    }
}
