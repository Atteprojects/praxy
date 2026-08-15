using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class VerificationRecoveryTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Verification_link_flips_the_flag_and_the_roles()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var (token, user) = await SignupAsync(projectId, "ada@example.com");

        // Redirects only to allowlisted hostnames — this is the phishing guard.
        var refused = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/verification", projectId, token,
            body: new { url = "https://evil.example.net/verify" }));
        await AssertError(refused, 400, "general_argument_invalid");

        var send = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/verification", projectId, token,
            body: new { url = "https://app.example.com/verify" }));
        Assert.Equal(204, (int)send.StatusCode);

        var link = Email.LastLinkParams();
        Assert.Equal(user.GetProperty("id").GetString(), link["userId"]);

        var confirm = await Client.SendAsync(DataPlane(HttpMethod.Put, "/v1/account/verification", projectId,
            body: new { userId = link["userId"], secret = link["secret"] }));
        var confirmed = await ReadJson(confirm);
        Assert.Equal(200, (int)confirm.StatusCode);
        Assert.True(confirmed.GetProperty("emailVerified").GetBoolean());

        var roles = await RolesAsync(projectId, token);
        Assert.Contains("users/verified", roles);
        Assert.Contains($"user:{link["userId"]}/verified", roles);

        // The token is single-use.
        var replay = await Client.SendAsync(DataPlane(HttpMethod.Put, "/v1/account/verification", projectId,
            body: new { userId = link["userId"], secret = link["secret"] }));
        await AssertError(replay, 401, "user_invalid_token");
    }

    [Fact]
    public async Task Recovery_resets_the_password_and_revokes_every_session()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var (oldToken, user) = await SignupAsync(projectId, "ada@example.com");

        // Unknown email answers 204 identically — no account enumeration through recovery.
        var unknown = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/recovery", projectId,
            body: new { email = "nobody@example.com", url = "https://app.example.com/reset" }));
        Assert.Equal(204, (int)unknown.StatusCode);
        var sentBefore = Email.Sent.Count;

        var send = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/recovery", projectId,
            body: new { email = "ada@example.com", url = "https://app.example.com/reset" }));
        Assert.Equal(204, (int)send.StatusCode);
        Assert.Equal(sentBefore + 1, Email.Sent.Count);

        var link = Email.LastLinkParams();
        var reset = await Client.SendAsync(DataPlane(HttpMethod.Put, "/v1/account/recovery", projectId,
            body: new { userId = link["userId"], secret = link["secret"], password = "brand-new-password" }));
        Assert.Equal(204, (int)reset.StatusCode);

        // The pre-reset session is dead; the new password signs in.
        var old = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/account", projectId, oldToken));
        await AssertError(old, 401, "general_unauthorized");
        await LoginAsync(projectId, "ada@example.com", "brand-new-password");
        _ = user;
    }
}
