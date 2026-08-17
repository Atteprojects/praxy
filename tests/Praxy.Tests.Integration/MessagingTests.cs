using System.Text.Json;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The Phase 8 owner-test flow end to end: create topic → subscribe two users → compose + send →
/// delivery status per target → auth verification email still renders with the project template.
/// No provider is configured in any of these tests, so every send falls back to the same
/// <see cref="AuthTestBase.Email"/> capturing sender <c>VerificationRecoveryTests</c>/<c>TeamsTests</c>
/// already use — this is what proves a project that never visits the Providers screen keeps working.
/// </summary>
public class MessagingTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Messaging:SendPollIntervalSeconds"] = "1",
    };

    [Fact]
    public async Task Owner_test_flow_topic_subscribe_send_and_delivery_status_per_target()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var (_, ada) = await SignupAsync(projectId, "ada@example.com");
        var (_, bob) = await SignupAsync(projectId, "bob@example.com");
        var adaId = ada.GetProperty("id").GetString()!;
        var bobId = bob.GetProperty("id").GetString()!;

        var topicId = await CreateTopicAsync(operatorToken, projectId, "announcements", "Announcements");
        await SubscribeAsync(operatorToken, projectId, topicId, adaId);
        await SubscribeAsync(operatorToken, projectId, topicId, bobId);

        var topic = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/messaging/topics/{topicId}", operatorToken)));
        Assert.Equal(2, topic.GetProperty("subscriberCount").GetInt32());

        var sentBefore = Email.Sent.Count;
        var messageId = await SendAsync(operatorToken, projectId, "Big news", "We shipped Messaging.", topicIds: [topicId]);

        var message = await WaitForMessageStatusAsync(operatorToken, projectId, messageId, "completed");
        var targets = message.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Equal(2, targets.Length);
        Assert.All(targets, t => Assert.Equal("sent", t.GetProperty("status").GetString()));
        Assert.Contains(targets, t => t.GetProperty("identifier").GetString() == "ada@example.com");
        Assert.Contains(targets, t => t.GetProperty("identifier").GetString() == "bob@example.com");

        // Fell back to the instance-wide (test-captured) sender since no provider is configured.
        Assert.Equal(sentBefore + 2, Email.Sent.Count);
        Assert.Contains(Email.Sent, m => m.To == "ada@example.com" && m.Subject == "Big news");
        Assert.Contains(Email.Sent, m => m.To == "bob@example.com" && m.Subject == "Big news");
    }

    [Fact]
    public async Task Send_to_explicit_users_needs_no_topic()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, carol) = await SignupAsync(projectId, "carol@example.com");
        var carolId = carol.GetProperty("id").GetString()!;

        var messageId = await SendAsync(operatorToken, projectId, "Direct", "Just for you.", userIds: [carolId]);
        var message = await WaitForMessageStatusAsync(operatorToken, projectId, messageId, "completed");
        var targets = message.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Single(targets);
        Assert.Equal("carol@example.com", targets[0].GetProperty("identifier").GetString());
        Assert.Equal("sent", targets[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_user_subscribed_and_named_explicitly_is_only_delivered_to_once()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, dana) = await SignupAsync(projectId, "dana@example.com");
        var danaId = dana.GetProperty("id").GetString()!;
        var topicId = await CreateTopicAsync(operatorToken, projectId, "dupe-check", "Dupe check");
        await SubscribeAsync(operatorToken, projectId, topicId, danaId);

        var messageId = await SendAsync(operatorToken, projectId, "Once", "Only once.", topicIds: [topicId], userIds: [danaId]);
        var message = await WaitForMessageStatusAsync(operatorToken, projectId, messageId, "completed");
        Assert.Single(message.GetProperty("targets").EnumerateArray());
    }

    [Fact]
    public async Task Sending_with_no_topics_or_users_is_refused()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/messaging/messages", operatorToken,
            new { subject = "s", body = "b" }));
        await AssertError(response, 400, "messaging_message_invalid");
    }

    [Fact]
    public async Task Verification_email_renders_the_default_then_a_project_override()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");
        var (token, _) = await SignupAsync(projectId, "eve@example.com");

        await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/verification", projectId, token,
            body: new { url = "https://app.example.com/verify" }));
        Assert.Contains($"Verify your email for Acme", Email.Sent.Last().Subject);
        var defaultLink = Email.LastLinkParams();
        Assert.NotEmpty(defaultLink["secret"]);

        var setResponse = await Client.SendAsync(Authed(HttpMethod.Put,
            $"/v1/console/projects/{projectId}/messaging/templates/verification", operatorToken,
            new { subject = "Confirm your {{project}} account", body = "Click here: {{url}}" }));
        var overridden = await ReadJson(setResponse);
        Assert.True(overridden.GetProperty("overridden").GetBoolean());

        await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/verification", projectId, token,
            body: new { url = "https://app.example.com/verify" }));
        Assert.Equal("Confirm your Acme account", Email.Sent.Last().Subject);
        Assert.StartsWith("Click here: https://app.example.com/verify", Email.Sent.Last().TextBody);

        var resetResponse = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/projects/{projectId}/messaging/templates/verification", operatorToken));
        var reset = await ReadJson(resetResponse);
        Assert.False(reset.GetProperty("overridden").GetBoolean());
    }

    [Fact]
    public async Task First_provider_becomes_default_automatically_and_setting_a_new_default_clears_the_old_one()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var first = await CreateProviderAsync(operatorToken, projectId, "Primary");
        Assert.True(first.GetProperty("isDefault").GetBoolean());

        var second = await CreateProviderAsync(operatorToken, projectId, "Backup");
        Assert.False(second.GetProperty("isDefault").GetBoolean());

        var secondId = second.GetProperty("id").GetString()!;
        await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/messaging/providers/{secondId}", operatorToken,
            new { isDefault = true }));

        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/messaging/providers", operatorToken)));
        var providers = list.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Single(providers, p => p.GetProperty("isDefault").GetBoolean());
        Assert.Equal(secondId, providers.Single(p => p.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetString());

        // The stored password is never echoed back — reveal-once, same as an API key or webhook secret.
        Assert.All(providers, p => Assert.False(p.TryGetProperty("secret", out _)));
        Assert.True(first.GetProperty("hasSecret").GetBoolean());
    }

    /// <summary>
    /// Found by Phase 9's security pass: a per-project provider's <c>host</c>/<c>port</c> is exactly
    /// as attacker-steerable as a webhook URL (any project's own console operator sets it), but had
    /// no SSRF protection at all before this phase — unlike Webhooks' own guard since Phase 6. This
    /// proves the fix through the real send path (console → MessagesService → MessageSendWorker →
    /// EmailProviderResolver → SmtpEmailSender → SsrfAddressGuard), not just the guard in isolation.
    /// </summary>
    [Fact]
    public async Task A_provider_pointed_at_a_private_address_is_blocked_not_attempted()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/messaging/providers", operatorToken,
            new { type = "email", name = "Loopback", host = "127.0.0.1", port = 2525, from = "noreply@example.com", useTls = false }));
        Assert.Equal(201, (int)response.StatusCode);

        var (_, victim) = await SignupAsync(projectId, "victim@example.com");
        var messageId = await SendAsync(operatorToken, projectId, "probe", "probe", userIds: [victim.GetProperty("id").GetString()!]);

        var message = await WaitForMessageStatusAsync(operatorToken, projectId, messageId, "completed");
        var target = Assert.Single(message.GetProperty("targets").EnumerateArray());
        Assert.Equal("failed", target.GetProperty("status").GetString());
        Assert.Contains("blocked by the SSRF guard", target.GetProperty("error").GetString());
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<string> CreateTopicAsync(string operatorToken, string projectId, string key, string name)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/messaging/topics", operatorToken, new { key, name }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    private async Task SubscribeAsync(string operatorToken, string projectId, string topicId, string userId)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/messaging/topics/{topicId}/subscribers", operatorToken,
            new { userId }));
        Assert.Equal(201, (int)response.StatusCode);
    }

    private async Task<string> SendAsync(
        string operatorToken, string projectId, string subject, string body,
        string[]? topicIds = null, string[]? userIds = null)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/messaging/messages", operatorToken,
            new { subject, body, topicIds = topicIds ?? [], userIds = userIds ?? [] }));
        Assert.Equal(201, (int)response.StatusCode);
        var created = await ReadJson(response);
        return created.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> CreateProviderAsync(string operatorToken, string projectId, string name)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/messaging/providers", operatorToken,
            new
            {
                type = "email", name, host = "smtp.example.com", port = 587, username = "svc",
                from = "noreply@example.com", useTls = true, secret = "s3cret",
            }));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private async Task<JsonElement> WaitForMessageStatusAsync(
        string operatorToken, string projectId, string messageId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var detail = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/messaging/messages/{messageId}", operatorToken)));
            if (detail.GetProperty("message").GetProperty("status").GetString() == targetStatus)
                return detail;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Message never reached status '{targetStatus}'.");
    }
}
