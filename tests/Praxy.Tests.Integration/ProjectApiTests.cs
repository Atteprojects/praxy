using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class ProjectApiTests(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Create_list_get_round_trip()
    {
        var (token, _) = await ClaimAsync();

        var created = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "My App" }));
        Assert.Equal(201, (int)created.StatusCode);
        var project = await ReadJson(created);
        var id = project.GetProperty("id").GetString()!;
        Assert.Equal("My App", project.GetProperty("name").GetString());

        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/projects", token)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(id, list.GetProperty("projects")[0].GetProperty("id").GetString());

        var fetched = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/projects/{id}", token)));
        Assert.Equal("My App", fetched.GetProperty("name").GetString());
    }

    /// <summary>
    /// The overview endpoint's counts, and — the part worth testing — that every one of them is
    /// scoped to its own project. A count that silently includes a second project's rows is exactly
    /// the kind of bug that looks plausible on screen, so this asserts against a *neighbouring*
    /// project that has resources of its own rather than against an empty instance.
    ///
    /// `tables` is the one count not scoped by `project_id` directly (tables hang off a database),
    /// so it is the one most likely to leak, and it is checked in both directions here.
    /// </summary>
    [Fact]
    public async Task Overview_counts_are_scoped_to_their_own_project()
    {
        var (token, _) = await ClaimAsync();

        var mine = await CreateProjectAsync(token, "Mine");
        var theirs = await CreateProjectAsync(token, "Theirs");

        // One database with two tables here; one database with one table next door.
        var myDatabase = await CreateDatabaseAsync(token, mine, "app");
        await CreateTableAsync(token, mine, myDatabase, "posts");
        await CreateTableAsync(token, mine, myDatabase, "comments");

        var theirDatabase = await CreateDatabaseAsync(token, theirs, "other");
        await CreateTableAsync(token, theirs, theirDatabase, "unrelated");

        var overview = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/projects/{mine}/overview", token)));

        Assert.Equal(1, overview.GetProperty("databases").GetInt32());
        Assert.Equal(2, overview.GetProperty("tables").GetInt32());

        var neighbour = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/projects/{theirs}/overview", token)));

        Assert.Equal(1, neighbour.GetProperty("databases").GetInt32());
        Assert.Equal(1, neighbour.GetProperty("tables").GetInt32());

        // Everything else is zero on a project nothing else was created in — asserted so a count
        // wired to the wrong table (or to no filter at all) fails here rather than looking right.
        foreach (var field in new[]
                 {
                     "users", "teams", "functions", "sites", "buckets", "apiKeys", "platforms",
                     "webhooks", "messagingTopics", "siteRequestsLast7Days", "functionExecutionsLast7Days",
                 })
            Assert.Equal(0, neighbour.GetProperty(field).GetInt32());
    }

    private async Task<string> CreateProjectAsync(string token, string name)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token, new { name }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateDatabaseAsync(string token, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/databases", token, new { key, name = key }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task CreateTableAsync(string token, string projectId, string databaseId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/databases/{databaseId}/tables", token, new { key, name = key }));
        Assert.Equal(201, (int)response.StatusCode);
    }

    [Fact]
    public async Task Custom_ids_validate_and_conflict()
    {
        var (token, _) = await ClaimAsync();

        var invalid = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "Bad", projectId = "Not_Valid!" }));
        await AssertError(invalid, 400, ErrorTypes.ProjectInvalidId);

        var reserved = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "Sneaky", projectId = "console" }));
        await AssertError(reserved, 400, ErrorTypes.ProjectReserved);

        var first = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "First", projectId = "taken" }));
        Assert.Equal(201, (int)first.StatusCode);

        var duplicate = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "Second", projectId = "taken" }));
        await AssertError(duplicate, 409, ErrorTypes.ProjectAlreadyExists);
    }

    [Fact]
    public async Task Console_project_is_invisible_to_the_projects_api()
    {
        var (token, _) = await ClaimAsync();

        // Seeded by the migrator, but org-less — it must never show up or resolve.
        var direct = await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/projects/console", token));
        await AssertError(direct, 404, ErrorTypes.ProjectNotFound);

        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/projects", token)));
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Unknown_routes_get_the_public_404_envelope()
    {
        var response = await Client.GetAsync("/v1/nonsense/route");
        await AssertError(response, 404, ErrorTypes.GeneralRouteNotFound);
    }

    [Fact]
    public async Task Health_endpoint_reports_ok()
    {
        var body = await ReadJson(await Client.GetAsync("/v1/health"));
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }
}
