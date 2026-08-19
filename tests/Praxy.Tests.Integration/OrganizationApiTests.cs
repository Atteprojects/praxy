using System.Net.Http.Json;
using System.Text.Json;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The console's org identity surface: who owns the projects list. Single-org by construction,
/// so these mostly pin the scoping and the wire format the console URL depends on.
/// </summary>
public class OrganizationApiTests(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task List_returns_the_operators_own_organization()
    {
        var (token, _) = await ClaimAsync();

        var list = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", token)));

        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var org = list.GetProperty("organizations")[0];
        Assert.Equal("Personal", org.GetProperty("name").GetString());

        // Wire form: hex32, never the dashed Guid.
        var id = org.GetProperty("id").GetString()!;
        Assert.Equal(32, id.Length);
        Assert.DoesNotContain('-', id);

        var fetched = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{id}", token)));
        Assert.Equal(id, fetched.GetProperty("id").GetString());
        Assert.Equal("Personal", fetched.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Another_operators_organization_is_not_readable()
    {
        var (token, _) = await ClaimAsync();
        var mine = await MyOrganizationIdAsync(token);

        // The instance can only be claimed once, so the second operator is seeded directly:
        // same password hash as the owner, their own org, their own membership.
        var (otherToken, _) = await CreateSecondOperatorAsync();

        var theirs = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", otherToken)));
        Assert.Equal(1, theirs.GetProperty("total").GetInt32());
        Assert.Equal("Second", theirs.GetProperty("organizations")[0].GetProperty("name").GetString());
        Assert.NotEqual(mine, theirs.GetProperty("organizations")[0].GetProperty("id").GetString());

        var crossRead = await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{mine}", otherToken));
        await AssertError(crossRead, 404, ErrorTypes.OrganizationNotFound);
    }

    [Fact]
    public async Task Unknown_and_malformed_ids_get_the_same_404()
    {
        var (token, _) = await ClaimAsync();

        var unknown = await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/organizations/{Guid.NewGuid():n}", token));
        await AssertError(unknown, 404, ErrorTypes.OrganizationNotFound);

        var malformed = await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations/not-an-id", token));
        await AssertError(malformed, 404, ErrorTypes.OrganizationNotFound);
    }

    [Fact]
    public async Task Anonymous_callers_are_refused()
    {
        await ClaimAsync();

        var list = await Client.GetAsync("/v1/console/organizations");
        await AssertError(list, 401, ErrorTypes.GeneralUnauthorized);
    }

    /// <summary>
    /// The regression test for the id-format decision: the console builds its org URL from
    /// <c>organizationId</c> on a project and then fetches the org by it, so a mismatch here is a
    /// 404 that only reproduces on a real instance.
    /// </summary>
    [Fact]
    public async Task Project_organization_ids_match_the_organization_endpoint_byte_for_byte()
    {
        var (token, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(token);

        var created = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "My App" }));
        Assert.Equal(201, (int)created.StatusCode);
        Assert.Equal(orgId, (await ReadJson(created)).GetProperty("organizationId").GetString());

        var list = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/projects", token)));
        var projects = list.GetProperty("projects").EnumerateArray().ToList();
        Assert.NotEmpty(projects);
        Assert.All(projects, p => Assert.Equal(orgId, p.GetProperty("organizationId").GetString()));

        var projectId = projects[0].GetProperty("id").GetString()!;
        var fetched = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/projects/{projectId}", token)));
        Assert.Equal(orgId, fetched.GetProperty("organizationId").GetString());

        // …and that exact string resolves as a URL segment.
        var org = await Client.SendAsync(Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}", token));
        Assert.Equal(200, (int)org.StatusCode);
    }

    [Fact]
    public async Task Reserved_console_project_stays_invisible_to_both_surfaces()
    {
        var (token, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(token);

        // The console project is org-less by design; nothing here may hand it out.
        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/projects", token)));
        Assert.Equal(0, list.GetProperty("total").GetInt32());

        var direct = await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/projects/console", token));
        await AssertError(direct, 404, ErrorTypes.ProjectNotFound);

        var organizations = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", token)));
        Assert.Equal(1, organizations.GetProperty("total").GetInt32());
        Assert.Equal(orgId, organizations.GetProperty("organizations")[0].GetProperty("id").GetString());
    }

    private async Task<string> MyOrganizationIdAsync(string token)
    {
        var list = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", token)));
        return list.GetProperty("organizations")[0].GetProperty("id").GetString()!;
    }
}
