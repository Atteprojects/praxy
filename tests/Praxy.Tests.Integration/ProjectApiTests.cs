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
