using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class RateLimitTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>
    {
        ["Praxy:RateLimits:Auth:PermitLimit"] = "2",
        ["Praxy:RateLimits:Auth:WindowSeconds"] = "60",
        ["Praxy:RateLimits:AuthEmail:PermitLimit"] = "1",
        ["Praxy:RateLimits:AuthEmail:WindowSeconds"] = "60",
        ["PRAXY_SECRET_KEY"] = "integration-test-instance-key",
    };

    [Fact]
    public async Task Third_login_attempt_gets_a_429_with_retry_after_and_ratelimit_headers()
    {
        var (_, projectId) = await SetupProjectAsync();

        for (var i = 0; i < 2; i++)
        {
            var allowed = await Client.SendAsync(DataPlane(
                HttpMethod.Post, "/v1/account/sessions/email", projectId,
                body: new { email = "x@example.com", password = "wrong-password-abc" }));
            Assert.Equal(401, (int)allowed.StatusCode);
        }

        var limited = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/account/sessions/email", projectId,
            body: new { email = "x@example.com", password = "wrong-password-abc" }));
        var body = await AssertError(limited, 429, "general_rate_limit_exceeded");
        _ = body;

        // Loud when tripped: Retry-After plus the RateLimit-* triplet.
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal("2", limited.Headers.GetValues("RateLimit-Limit").Single());
        Assert.Equal("0", limited.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.True(int.Parse(limited.Headers.GetValues("RateLimit-Reset").Single()) > 0);
    }

    [Fact]
    public async Task Buckets_partition_on_project_before_ip()
    {
        var (operatorToken, projectA) = await SetupProjectAsync();
        var createB = await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name = "Second" }));
        var projectB = (await ReadJson(createB)).GetProperty("id").GetString()!;

        // Exhaust project A's bucket from this IP…
        for (var i = 0; i < 3; i++)
            await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/sessions/email", projectA,
                body: new { email = "x@example.com", password = "wrong-password-abc" }));

        // …project B from the same IP still has its own budget.
        var other = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/sessions/email", projectB,
            body: new { email = "x@example.com", password = "wrong-password-abc" }));
        Assert.Equal(401, (int)other.StatusCode);
    }

    [Fact]
    public async Task Email_sending_endpoints_have_their_own_tighter_bucket()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "app.example.com");

        var first = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/recovery", projectId,
            body: new { email = "x@example.com", url = "https://app.example.com/reset" }));
        Assert.Equal(204, (int)first.StatusCode);

        var second = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/account/recovery", projectId,
            body: new { email = "x@example.com", url = "https://app.example.com/reset" }));
        await AssertError(second, 429, "general_rate_limit_exceeded");
        Assert.Equal("1", second.Headers.GetValues("RateLimit-Limit").Single());
    }
}

/// <summary>
/// The data plane's own ceilings. Before these existed only the auth endpoints opted into rate
/// limiting, so row CRUD, function invocation and realtime ticket minting were unbounded — and
/// function invocation is the one that starts a container per permitted request.
///
/// These tests deliberately hit routes that 404: the limiter is middleware and runs before the
/// endpoint, so a request consumes its permit whatever the handler goes on to return. What is under
/// test is that the bucket is attached at all, and which bucket it is.
/// </summary>
public class DataPlaneRateLimitTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const string MissingId = "0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4";

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:RateLimits:DataPlane:PermitLimit"] = "3",
        ["Praxy:RateLimits:DataPlane:WindowSeconds"] = "60",
        ["Praxy:RateLimits:Functions:PermitLimit"] = "2",
        ["Praxy:RateLimits:Functions:WindowSeconds"] = "60",
        ["Praxy:RateLimits:Realtime:PermitLimit"] = "2",
        ["Praxy:RateLimits:Realtime:WindowSeconds"] = "60",
    };

    private HttpRequestMessage Rows(string projectId, string? sessionToken = null) =>
        DataPlane(HttpMethod.Get, $"/v1/databases/{MissingId}/tables/{MissingId}/rows", projectId, sessionToken);

    [Fact]
    public async Task Row_endpoints_are_bounded()
    {
        var (_, projectId) = await SetupProjectAsync();

        for (var i = 0; i < 3; i++)
            Assert.Equal(404, (int)(await Client.SendAsync(Rows(projectId))).StatusCode);

        var limited = await Client.SendAsync(Rows(projectId));
        await AssertError(limited, 429, "general_rate_limit_exceeded");
        Assert.Equal("3", limited.Headers.GetValues("RateLimit-Limit").Single());
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    /// <summary>
    /// Functions get their own, tighter bucket rather than sharing the data plane's — exhausting
    /// the function budget must not also lock out row reads, and vice versa.
    /// </summary>
    [Fact]
    public async Task Function_invocation_has_its_own_tighter_bucket()
    {
        var (_, projectId) = await SetupProjectAsync();

        for (var i = 0; i < 2; i++)
        {
            var allowed = await Client.SendAsync(DataPlane(
                HttpMethod.Post, $"/v1/functions/{MissingId}/executions", projectId,
                body: new { method = "GET", path = "/" }));
            Assert.Equal(404, (int)allowed.StatusCode);
        }

        var limited = await Client.SendAsync(DataPlane(
            HttpMethod.Post, $"/v1/functions/{MissingId}/executions", projectId,
            body: new { method = "GET", path = "/" }));
        await AssertError(limited, 429, "general_rate_limit_exceeded");
        Assert.Equal("2", limited.Headers.GetValues("RateLimit-Limit").Single());

        // The row bucket is untouched by the exhausted function bucket.
        Assert.Equal(404, (int)(await Client.SendAsync(Rows(projectId))).StatusCode);
    }

    [Fact]
    public async Task Realtime_ticket_minting_is_bounded()
    {
        var (_, projectId) = await SetupProjectAsync();

        for (var i = 0; i < 2; i++)
            await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/realtime/ticket", projectId));

        var limited = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/realtime/ticket", projectId));
        await AssertError(limited, 429, "general_rate_limit_exceeded");
    }

    /// <summary>
    /// Buckets partition on caller identity before source address, so one heavy client cannot spend
    /// everyone else's budget — the shape that matters behind a NAT or a mobile carrier gateway,
    /// where every request arrives from one address.
    /// </summary>
    [Fact]
    public async Task Two_sessions_on_one_address_do_not_share_a_bucket()
    {
        var (_, projectId) = await SetupProjectAsync();
        var (heavy, _) = await SignupAsync(projectId, "heavy@example.com");
        var (quiet, _) = await SignupAsync(projectId, "quiet@example.com");

        for (var i = 0; i < 3; i++)
            await Client.SendAsync(Rows(projectId, heavy));
        await AssertError(await Client.SendAsync(Rows(projectId, heavy)), 429, "general_rate_limit_exceeded");

        Assert.Equal(404, (int)(await Client.SendAsync(Rows(projectId, quiet))).StatusCode);
    }
}

public class CorsTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private static HttpRequestMessage WithOrigin(HttpRequestMessage request, string origin)
    {
        request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Unknown_origin_is_a_403_until_the_platform_is_registered()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var blocked = await Client.SendAsync(WithOrigin(
            DataPlane(HttpMethod.Get, "/v1/ping", projectId), "https://app.example.com"));
        await AssertError(blocked, 403, "general_unknown_origin");

        await AddPlatformAsync(operatorToken, projectId, "app.example.com");

        var allowed = await Client.SendAsync(WithOrigin(
            DataPlane(HttpMethod.Get, "/v1/ping", projectId), "https://app.example.com"));
        Assert.Equal(200, (int)allowed.StatusCode);
        Assert.Equal("https://app.example.com",
            allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task Wildcard_platforms_match_subdomains()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "*.example.com");

        var sub = await Client.SendAsync(WithOrigin(
            DataPlane(HttpMethod.Get, "/v1/ping", projectId), "https://pr-42.example.com"));
        Assert.Equal(200, (int)sub.StatusCode);

        var bare = await Client.SendAsync(WithOrigin(
            DataPlane(HttpMethod.Get, "/v1/ping", projectId), "https://example.com"));
        await AssertError(bare, 403, "general_unknown_origin");
    }

    [Fact]
    public async Task Preflight_is_answered_without_project_knowledge()
    {
        var (_, projectId) = await SetupProjectAsync();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/account");
        preflight.Headers.Add("Origin", "https://anything.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,x-praxy-project");

        var response = await Client.SendAsync(preflight);
        Assert.Equal(204, (int)response.StatusCode);
        Assert.Equal("https://anything.example.com",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("x-praxy-project",
            response.Headers.GetValues("Access-Control-Allow-Headers").Single());
        _ = projectId;
    }

    [Fact]
    public async Task Requests_without_an_origin_are_untouched()
    {
        var (_, projectId) = await SetupProjectAsync();
        var response = await Client.SendAsync(DataPlane(HttpMethod.Get, "/v1/ping", projectId));
        Assert.Equal(200, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
