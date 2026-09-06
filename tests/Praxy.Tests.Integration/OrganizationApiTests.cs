using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The console's org identity surface: who owns the projects list, and — since
/// organizations-phase-1 — the lifecycle itself (create, rename, delete) and what changes once an
/// operator can belong to more than one. These assert the property (a project survives a blocked
/// delete, a third org stays unreadable), not just the status code, per that phase's own standard.
/// </summary>
public class OrganizationApiTests(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    // Small enough that the cap is fast and unambiguous to reach; the production default (10) proves
    // the same property after nine more rows.
    private const int MaxOrganizations = 3;

    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>(
        base.ExtraSettings ?? new Dictionary<string, string?>())
    {
        ["Praxy:Quotas:MaxOrganizationsPerOperator"] = "3",
    };

    /// <summary>
    /// Organizations were the only creatable resource in Praxy with no quota — and the one every
    /// other quota is scoped to, so an operator at their MaxProjects ceiling could create another
    /// organization and get a fresh allowance, making each per-org limit advisory. Asserts the cap
    /// actually binds and that the refused create writes nothing, not that a knob round-trips.
    /// </summary>
    [Fact]
    public async Task An_operator_cannot_create_unlimited_organizations()
    {
        var (token, _) = await ClaimAsync();

        // Signup already made one ("Personal"), so this reaches the cap exactly.
        for (var i = 2; i <= MaxOrganizations; i++)
        {
            var ok = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/organizations", token,
                new { name = $"Org {i}" }));
            Assert.Equal(201, (int)ok.StatusCode);
        }

        var refused = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/organizations", token,
            new { name = "One too many" }));
        // 400, not 429 — QuotaService.Exceeded's own shape for every dimension, unchanged here.
        Assert.Equal(400, (int)refused.StatusCode);
        Assert.Equal(ErrorTypes.GeneralResourceLimitExceeded,
            (await ReadJson(refused)).GetProperty("type").GetString());

        // The property that matters: nothing was written. The operator still has exactly the cap.
        var list = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", token)));
        Assert.Equal(MaxOrganizations, list.GetProperty("total").GetInt32());
    }

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

    [Fact]
    public async Task Creating_an_organization_makes_its_creator_the_owner_with_a_working_project_list()
    {
        var (token, account) = await ClaimAsync();
        var operatorId = account.GetProperty("id").GetString()!;

        var created = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Post, "/v1/console/organizations", token, new { name = "Acme" })));
        Assert.Equal("Acme", created.GetProperty("name").GetString());
        var orgId = created.GetProperty("id").GetString()!;

        Assert.Equal("owner", await ScalarStringAsync(
            "SELECT role FROM praxy.organization_members WHERE organization_id = $1 AND user_id = $2",
            Guid.Parse(orgId), Guid.Parse(operatorId)));

        var project = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "App", organizationId = orgId })));
        Assert.Equal(orgId, project.GetProperty("organizationId").GetString());

        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/projects", token)));
        Assert.Contains(list.GetProperty("projects").EnumerateArray(),
            p => p.GetProperty("id").GetString() == project.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Renaming_an_organization_updates_the_name_and_leaves_the_id_untouched()
    {
        var (token, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(token);

        var renamed = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Patch, $"/v1/console/organizations/{orgId}", token, new { name = "Renamed" })));
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());
        Assert.Equal(orgId, renamed.GetProperty("id").GetString());

        var fetched = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}", token)));
        Assert.Equal("Renamed", fetched.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_organization_holding_a_project_cannot_be_deleted()
    {
        var (token, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(token);
        var project = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Post, "/v1/console/projects", token, new { name = "App" })));
        var projectId = project.GetProperty("id").GetString()!;

        var delete = await Client.SendAsync(Authed(HttpMethod.Delete, $"/v1/console/organizations/{orgId}", token));
        await AssertError(delete, 409, ErrorTypes.OrganizationHasProjects);

        // The property, not just the status code: the project and the org both still exist.
        var stillThere = await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/projects/{projectId}", token));
        Assert.Equal(200, (int)stillThere.StatusCode);
        var orgStillThere = await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{orgId}", token));
        Assert.Equal(200, (int)orgStillThere.StatusCode);
    }

    [Fact]
    public async Task An_operators_last_organization_cannot_be_deleted()
    {
        var (token, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(token);

        var delete = await Client.SendAsync(Authed(HttpMethod.Delete, $"/v1/console/organizations/{orgId}", token));
        await AssertError(delete, 409, ErrorTypes.OrganizationLastOne);

        var stillThere = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", token)));
        Assert.Equal(1, stillThere.GetProperty("total").GetInt32());

        // Emptying the *last* slot by creating a second org first, then deleting the original
        // (now-empty) one, must succeed — the guard is "your last one", not "any empty one".
        var second = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Post, "/v1/console/organizations", token, new { name = "Second" })));
        var secondId = second.GetProperty("id").GetString()!;

        var deletedFirst = await Client.SendAsync(
            Authed(HttpMethod.Delete, $"/v1/console/organizations/{orgId}", token));
        Assert.Equal(204, (int)deletedFirst.StatusCode);

        // ...and now that the second org is the only one left, the same guard reapplies to it.
        var deleteSecond = await Client.SendAsync(
            Authed(HttpMethod.Delete, $"/v1/console/organizations/{secondId}", token));
        await AssertError(deleteSecond, 409, ErrorTypes.OrganizationLastOne);
    }

    [Fact]
    public async Task An_operator_in_two_organizations_sees_exactly_those_two_and_cannot_touch_a_third()
    {
        var (token, _) = await ClaimAsync();
        var orgId = await MyOrganizationIdAsync(token);
        var second = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Post, "/v1/console/organizations", token, new { name = "Second" })));
        var secondId = second.GetProperty("id").GetString()!;

        var (otherToken, _) = await CreateSecondOperatorAsync();
        var thirdId = await MyOrganizationIdAsync(otherToken);

        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/organizations", token)));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var ids = list.GetProperty("organizations").EnumerateArray()
            .Select(o => o.GetProperty("id").GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { orgId, secondId }, ids);

        var read = await Client.SendAsync(Authed(HttpMethod.Get, $"/v1/console/organizations/{thirdId}", token));
        await AssertError(read, 404, ErrorTypes.OrganizationNotFound);

        var rename = await Client.SendAsync(Authed(HttpMethod.Patch, $"/v1/console/organizations/{thirdId}", token,
            new { name = "Hijacked" }));
        await AssertError(rename, 404, ErrorTypes.OrganizationNotFound);

        var delete = await Client.SendAsync(Authed(HttpMethod.Delete, $"/v1/console/organizations/{thirdId}", token));
        await AssertError(delete, 404, ErrorTypes.OrganizationNotFound);

        // Untouched, seen through the eyes of the operator who actually owns it.
        var untouched = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/organizations/{thirdId}", otherToken)));
        Assert.Equal("Second", untouched.GetProperty("name").GetString());
    }

    /// <summary>
    /// The landmine organizations-phase-1-prompt.md calls out by name: <c>ProjectEndpoints.Create</c>
    /// used to silently pick the operator's oldest membership. Once a second org exists, that would
    /// quietly put a project in the wrong place; this asserts it fails loudly instead.
    /// </summary>
    [Fact]
    public async Task Creating_a_project_with_no_organization_id_fails_once_ambiguous_but_still_works_when_not()
    {
        var (token, _) = await ClaimAsync();
        var second = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Post, "/v1/console/organizations", token, new { name = "Second" })));
        var secondId = second.GetProperty("id").GetString()!;

        var ambiguous = await Client.SendAsync(
            Authed(HttpMethod.Post, "/v1/console/projects", token, new { name = "Ambiguous" }));
        var error = await AssertError(ambiguous, 400, ErrorTypes.GeneralArgumentInvalid);
        Assert.True(error.GetProperty("fields").TryGetProperty("organizationId", out _));

        var explicitOrg = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "Explicit", organizationId = secondId })));
        Assert.Equal(secondId, explicitOrg.GetProperty("organizationId").GetString());

        // The failed ambiguous attempt created nothing; the explicit one is the only project.
        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/projects", token)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Creating_a_project_in_an_organization_the_operator_does_not_belong_to_is_rejected()
    {
        var (token, _) = await ClaimAsync();
        var (otherToken, _) = await CreateSecondOperatorAsync();
        var theirOrgId = await MyOrganizationIdAsync(otherToken);

        var response = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "Trespasser", organizationId = theirOrgId }));
        await AssertError(response, 404, ErrorTypes.OrganizationNotFound);

        Assert.Equal(0L, await ScalarLongAsync(
            "SELECT COUNT(*) FROM praxy.projects WHERE organization_id = $1", Guid.Parse(theirOrgId)));
    }

    private async Task<string> MyOrganizationIdAsync(string token)
    {
        var list = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/organizations", token)));
        return list.GetProperty("organizations")[0].GetProperty("id").GetString()!;
    }

    private async Task<string> ScalarStringAsync(string sql, params object[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> ScalarLongAsync(string sql, params object[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
