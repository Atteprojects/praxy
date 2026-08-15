using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class TeamsTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Client_invite_emails_a_link_and_acceptance_creates_a_session_and_the_team_role()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var (ownerToken, _) = await SignupAsync(projectId, "owner@example.com");

        // Creating a team makes the creator its confirmed owner.
        var create = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/teams", projectId, ownerToken,
            body: new { name = "Rocket" }));
        var team = await ReadJson(create);
        Assert.Equal(201, (int)create.StatusCode);
        var teamId = team.GetProperty("id").GetString()!;
        Assert.Contains($"team:{teamId}", await RolesAsync(projectId, ownerToken));
        Assert.Contains($"team:{teamId}/owner", await RolesAsync(projectId, ownerToken));

        // Session call = invitation email (Appwrite client semantics).
        var invite = await Client.SendAsync(DataPlane(
            HttpMethod.Post, $"/v1/teams/{teamId}/memberships", projectId, ownerToken,
            body: new { email = "member@example.com", roles = new[] { "editor" }, url = "https://app.example.com/join" }));
        var membership = await ReadJson(invite);
        Assert.Equal(201, (int)invite.StatusCode);
        Assert.False(membership.GetProperty("confirmed").GetBoolean());

        var link = Email.LastLinkParams();
        Assert.Equal(teamId, link["teamId"]);

        // Acceptance is authenticated by the emailed secret and walks away with a session.
        var accept = await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/teams/{link["teamId"]}/memberships/{link["membershipId"]}/status", projectId,
            body: new { userId = link["userId"], secret = link["secret"] }));
        var accepted = await ReadJson(accept);
        Assert.Equal(200, (int)accept.StatusCode);
        Assert.True(accepted.GetProperty("membership").GetProperty("confirmed").GetBoolean());

        var memberToken = accepted.GetProperty("session").GetProperty("token").GetString()!;
        var roles = await RolesAsync(projectId, memberToken);
        Assert.Contains($"team:{teamId}", roles);
        Assert.Contains($"team:{teamId}/editor", roles);
        Assert.Contains($"member:{link["membershipId"]}", roles);

        // Replaying the invite secret is refused.
        var replay = await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/teams/{link["teamId"]}/memberships/{link["membershipId"]}/status", projectId,
            body: new { userId = link["userId"], secret = link["secret"] }));
        await AssertError(replay, 409, "membership_already_confirmed");
    }

    [Fact]
    public async Task Wrong_invite_secret_is_refused()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var (ownerToken, _) = await SignupAsync(projectId, "owner@example.com");
        var team = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/teams", projectId, ownerToken, body: new { name = "Rocket" })));
        var teamId = team.GetProperty("id").GetString()!;

        await Client.SendAsync(DataPlane(HttpMethod.Post, $"/v1/teams/{teamId}/memberships", projectId, ownerToken,
            body: new { email = "member@example.com", url = "https://app.example.com/join" }));
        var link = Email.LastLinkParams();

        var bad = await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/teams/{teamId}/memberships/{link["membershipId"]}/status", projectId,
            body: new { userId = link["userId"], secret = "definitely-not-the-secret" }));
        await AssertError(bad, 401, "team_invalid_secret");
    }

    [Fact]
    public async Task Api_key_membership_creation_adds_directly_without_email()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, key) = await CreateApiKeyAsync(operatorToken, projectId, "teams.read", "teams.write");

        var team = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/teams", projectId, apiKey: key, body: new { name = "Server Team" })));
        var teamId = team.GetProperty("id").GetString()!;

        var emailsBefore = Email.Sent.Count;
        var add = await Client.SendAsync(DataPlane(
            HttpMethod.Post, $"/v1/teams/{teamId}/memberships", projectId, apiKey: key,
            body: new { email = "direct@example.com", roles = new[] { "viewer" } }));
        var membership = await ReadJson(add);
        Assert.Equal(201, (int)add.StatusCode);
        Assert.True(membership.GetProperty("confirmed").GetBoolean());
        Assert.Equal(emailsBefore, Email.Sent.Count);
    }

    [Fact]
    public async Task Non_owner_cannot_invite_or_delete_the_team()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var (ownerToken, _) = await SignupAsync(projectId, "owner@example.com");
        var (strangerToken, _) = await SignupAsync(projectId, "stranger@example.com");

        var team = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/teams", projectId, ownerToken, body: new { name = "Rocket" })));
        var teamId = team.GetProperty("id").GetString()!;

        var invite = await Client.SendAsync(DataPlane(
            HttpMethod.Post, $"/v1/teams/{teamId}/memberships", projectId, strangerToken,
            body: new { email = "x@example.com", url = "https://app.example.com/join" }));
        await AssertError(invite, 401, "general_unauthorized");

        var delete = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/teams/{teamId}", projectId, strangerToken));
        await AssertError(delete, 401, "general_unauthorized");
    }

    [Fact]
    public async Task Members_see_their_teams_guests_see_nothing()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (ownerToken, _) = await SignupAsync(projectId, "owner@example.com");
        await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/teams", projectId, ownerToken,
            body: new { name = "Visible" }));

        var mine = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Get, "/v1/teams", projectId, ownerToken)));
        Assert.Equal(1, mine.GetProperty("total").GetInt32());

        var guest = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/teams", projectId));
        await AssertError(guest, 401, "general_unauthorized");
        _ = operatorToken;
    }
}
