using System.Text.Json;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Geo columns and `near` queries, Phase 1 (docs/research/geo-nearby.md,
/// docs/handoff/geo-nearby-phase-1-prompt.md): the scalar `geo` point column type against a real
/// PostGIS-enabled Postgres instance, the `spatial` (GiST) index, and `near(lat, lng, radiusMeters)`
/// as a pure radius filter gated on that index.
///
/// Real-world coordinates, verified against the actual container this fixture runs (see
/// docs/handoff/geo-nearby-phase-1-report.md): SF City Hall (37.7749, -122.4194), the Golden Gate
/// Bridge (37.8199, -122.4783, ~7201m from City Hall) and the Ferry Building (37.7955, -122.3937,
/// ~3217m from City Hall) — real `ST_Distance` values, not made-up numbers.
/// </summary>
public class GeoEngineTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const double CityHallLat = 37.7749, CityHallLng = -122.4194;
    private const double GoldenGateLat = 37.8199, GoldenGateLng = -122.4783; // ~7201m from City Hall
    private const double FerryBuildingLat = 37.7955, FerryBuildingLng = -122.3937; // ~3217m from City Hall

    [Fact]
    public async Task Geo_column_round_trips_lat_lng_without_precision_loss()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();

        var row = await CreateRowAsync(projectId, apiKey, databaseId, placesId,
            new { data = new { name = "City Hall", location = new { lat = CityHallLat, lng = CityHallLng } } });
        var location = row.GetProperty("location");
        Assert.Equal(CityHallLat, location.GetProperty("lat").GetDouble(), precision: 9);
        Assert.Equal(CityHallLng, location.GetProperty("lng").GetDouble(), precision: 9);

        var read = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{placesId}/rows/{row.GetProperty("$id").GetString()}", projectId, apiKey: apiKey));
        var readBody = await ReadJson(read);
        Assert.Equal(CityHallLat, readBody.GetProperty("location").GetProperty("lat").GetDouble(), precision: 9);
        Assert.Equal(CityHallLng, readBody.GetProperty("location").GetProperty("lng").GetDouble(), precision: 9);
    }

    [Fact]
    public async Task A_max_length_key_leaves_room_for_the_lat_lng_alias_suffix()
    {
        // PhysicalNaming caps a physical name at 63 chars total; a geo column's read path derives
        // "{physicalName}_lng"/"{physicalName}_lat" on top of that. A 64-char key (Keys.MaxLength)
        // sanitizes to a physical name that would otherwise consume the entire 63-char budget,
        // leaving no room for the suffix — this must not throw on the very first row write/read.
        var (projectId, apiKey, databaseId) = await SetupBareAsync();
        var placesId = await CreateTableAsync(projectId, apiKey, databaseId, "places");
        var longKey = "a" + new string('b', 63);
        Assert.Equal(64, longKey.Length);
        await CreateColumnAsync(projectId, apiKey, databaseId, placesId, "geo", new { key = longKey });
        await GrantPublicAsync(projectId, apiKey, databaseId, placesId);

        var row = await CreateRowAsync(projectId, apiKey, databaseId, placesId,
            new { data = new Dictionary<string, object> { [longKey] = new { lat = CityHallLat, lng = CityHallLng } } });
        Assert.Equal(CityHallLat, row.GetProperty(longKey).GetProperty("lat").GetDouble(), precision: 9);

        var read = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{placesId}/rows/{row.GetProperty("$id").GetString()}", projectId, apiKey: apiKey));
        Assert.Equal(200, (int)read.StatusCode);
    }

    [Fact]
    public async Task A_null_geo_value_round_trips_as_null()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();
        var row = await CreateRowAsync(projectId, apiKey, databaseId, placesId, new { data = new { name = "No location" } });
        Assert.Equal(JsonValueKind.Null, row.GetProperty("location").ValueKind);
    }

    [Fact]
    public async Task Updating_a_geo_value_persists_the_new_point()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();
        var row = await CreateRowAsync(projectId, apiKey, databaseId, placesId,
            new { data = new { name = "Movable", location = new { lat = CityHallLat, lng = CityHallLng } } });
        var rowId = row.GetProperty("$id").GetString();

        var update = await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{placesId}/rows/{rowId}", projectId, apiKey: apiKey,
            body: new { data = new { location = new { lat = GoldenGateLat, lng = GoldenGateLng } } }));
        Assert.Equal(200, (int)update.StatusCode);
        var updated = await ReadJson(update);
        Assert.Equal(GoldenGateLat, updated.GetProperty("location").GetProperty("lat").GetDouble(), precision: 9);
        Assert.Equal(GoldenGateLng, updated.GetProperty("location").GetProperty("lng").GetDouble(), precision: 9);
    }

    [Fact]
    public async Task Creating_a_geo_column_rejects_array_and_default()
    {
        var (projectId, apiKey, databaseId) = await SetupBareAsync();
        var placesId = await CreateTableAsync(projectId, apiKey, databaseId, "places");

        var arrayResponse = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{placesId}/columns/geo", projectId, apiKey: apiKey,
            body: new { key = "location", array = true }));
        var arrayBody = await AssertError(arrayResponse, 400, ErrorTypes.GeneralArgumentInvalid);
        Assert.True(arrayBody.GetProperty("fields").TryGetProperty("array", out _));

        var defaultResponse = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{placesId}/columns/geo", projectId, apiKey: apiKey,
            body: new { key = "location", @default = new { lat = 1.0, lng = 2.0 } }));
        var defaultBody = await AssertError(defaultResponse, 400, ErrorTypes.GeneralArgumentInvalid);
        Assert.True(defaultBody.GetProperty("fields").TryGetProperty("default", out _));
    }

    [Fact]
    public async Task Out_of_range_coordinates_are_a_clean_400_not_a_raw_postgres_error()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{placesId}/rows", projectId, apiKey: apiKey,
            body: new { data = new { name = "Bad point", location = new { lat = 200.0, lng = 0.0 } } }));
        var body = await AssertError(response, 400, ErrorTypes.RowInvalidStructure);
        Assert.True(body.GetProperty("fields").TryGetProperty("location", out _));
    }

    [Fact]
    public async Task Near_without_a_spatial_index_is_rejected_with_a_clear_error()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();
        await CreateRowAsync(projectId, apiKey, databaseId, placesId,
            new { data = new { name = "City Hall", location = new { lat = CityHallLat, lng = CityHallLng } } });

        var query = $$"""{"method":"near","attribute":"location","values":[{{CityHallLat}},{{CityHallLng}},5000]}""";
        var url = $"/v1/databases/{databaseId}/tables/{placesId}/rows?queries[]={Uri.EscapeDataString(query)}";
        var response = await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey));
        await AssertError(response, 400, ErrorTypes.GeneralQueryInvalid);
    }

    [Fact]
    public async Task Near_on_a_non_geo_column_is_rejected()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();
        var query = """{"method":"near","attribute":"name","values":[1,2,3]}""";
        var url = $"/v1/databases/{databaseId}/tables/{placesId}/rows?queries[]={Uri.EscapeDataString(query)}";
        var response = await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey));
        await AssertError(response, 400, ErrorTypes.GeneralQueryInvalid);
    }

    [Fact]
    public async Task Generic_operators_on_a_geo_column_are_a_clean_400_not_a_crash()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();
        var query = """{"method":"equal","attribute":"location","values":[{"lat":1,"lng":2}]}""";
        var url = $"/v1/databases/{databaseId}/tables/{placesId}/rows?queries[]={Uri.EscapeDataString(query)}";
        var response = await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey));
        await AssertError(response, 400, ErrorTypes.GeneralQueryInvalid);
    }

    [Fact]
    public async Task Spatial_index_settles_from_processing_to_available()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();

        var indexResponse = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{placesId}/indexes", projectId, apiKey: apiKey,
            body: new { key = "idx_location", type = "spatial", columns = new[] { "location" } }));
        Assert.Equal(201, (int)indexResponse.StatusCode);
        var created = await ReadJson(indexResponse);
        Assert.Equal("spatial", created.GetProperty("type").GetString());
        var indexId = created.GetProperty("id").GetString()!;

        var settled = await WaitForIndexStatusAsync(projectId, apiKey, databaseId, placesId, indexId, "available");
        Assert.Equal("available", settled.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Spatial_index_requires_a_geo_column()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{placesId}/indexes", projectId, apiKey: apiKey,
            body: new { key = "idx_bad", type = "spatial", columns = new[] { "name" } }));
        await AssertError(response, 400, ErrorTypes.IndexInvalid);
    }

    [Fact]
    public async Task Near_with_a_spatial_index_includes_the_closer_row_and_excludes_the_farther_one()
    {
        var (projectId, apiKey, databaseId, placesId) = await SetupAsync();

        var indexResponse = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{placesId}/indexes", projectId, apiKey: apiKey,
            body: new { key = "idx_location", type = "spatial", columns = new[] { "location" } }));
        var indexId = (await ReadJson(indexResponse)).GetProperty("id").GetString()!;
        await WaitForIndexStatusAsync(projectId, apiKey, databaseId, placesId, indexId, "available");

        await CreateRowAsync(projectId, apiKey, databaseId, placesId,
            new { data = new { name = "City Hall", location = new { lat = CityHallLat, lng = CityHallLng } } });
        await CreateRowAsync(projectId, apiKey, databaseId, placesId,
            new { data = new { name = "Golden Gate Bridge", location = new { lat = GoldenGateLat, lng = GoldenGateLng } } });
        await CreateRowAsync(projectId, apiKey, databaseId, placesId,
            new { data = new { name = "Ferry Building", location = new { lat = FerryBuildingLat, lng = FerryBuildingLng } } });

        // Centered on City Hall, radius 5000m: excludes the Golden Gate Bridge (~7201m away),
        // includes the Ferry Building (~3217m away) and City Hall itself (0m) — real ST_Distance
        // values (Phase 1 verification), not made-up numbers.
        var query = $$"""{"method":"near","attribute":"location","values":[{{CityHallLat}},{{CityHallLng}},5000]}""";
        var url = $"/v1/databases/{databaseId}/tables/{placesId}/rows?queries[]={Uri.EscapeDataString(query)}";
        var response = await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey));
        Assert.Equal(200, (int)response.StatusCode);
        var body = await ReadJson(response);
        var names = body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToHashSet();
        Assert.Equal(2, names.Count);
        Assert.Contains("City Hall", names);
        Assert.Contains("Ferry Building", names);
        Assert.DoesNotContain("Golden Gate Bridge", names);
    }

    // ---- setup helpers ------------------------------------------------------------------------

    private async Task<(string ProjectId, string ApiKey, string DatabaseId, string PlacesId)> SetupAsync()
    {
        var (projectId, apiKey, databaseId) = await SetupBareAsync();
        var placesId = await CreateTableAsync(projectId, apiKey, databaseId, "places");
        await CreateColumnAsync(projectId, apiKey, databaseId, placesId, "string",
            new { key = "name", size = 200, required = true });
        await CreateColumnAsync(projectId, apiKey, databaseId, placesId, "geo", new { key = "location" });
        await GrantPublicAsync(projectId, apiKey, databaseId, placesId);
        return (projectId, apiKey, databaseId, placesId);
    }

    private async Task<(string ProjectId, string ApiKey, string DatabaseId)> SetupBareAsync()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var databaseId = await CreateDatabaseAsync(projectId, apiKey, "geo");
        return (projectId, apiKey, databaseId);
    }

    private async Task<string> CreateDatabaseAsync(string projectId, string apiKey, string key)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey,
            body: new { key, name = key }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateTableAsync(string projectId, string apiKey, string databaseId, string key)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables", projectId, apiKey: apiKey, body: new { key, name = key }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> CreateColumnAsync(
        string projectId, string apiKey, string databaseId, string tableId, string type, object requestBody)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/{type}", projectId, apiKey: apiKey, body: requestBody));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body;
    }

    private async Task GrantPublicAsync(string projectId, string apiKey, string databaseId, string tableId) =>
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { permissions = new[] { "create(\"any\")", "read(\"any\")", "update(\"any\")", "delete(\"any\")" } }));

    private async Task<JsonElement> CreateRowAsync(
        string projectId, string apiKey, string databaseId, string tableId, object body)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey, body: body));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private async Task<JsonElement> WaitForIndexStatusAsync(
        string projectId, string apiKey, string databaseId, string tableId, string indexId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var response = await Client.SendAsync(DataPlane(HttpMethod.Get,
                $"/v1/databases/{databaseId}/tables/{tableId}/indexes/{indexId}", projectId, apiKey: apiKey));
            var body = await ReadJson(response);
            var status = body.GetProperty("status").GetString();
            if (status == targetStatus)
                return body;
            if (status == "failed" && targetStatus != "failed")
                throw new Exception($"Index failed unexpectedly: {body.GetProperty("error").GetString()}");
            await Task.Delay(150);
        }
        throw new TimeoutException($"Index did not reach status '{targetStatus}' in time.");
    }
}
