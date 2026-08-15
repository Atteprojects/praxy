using Npgsql;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class CatalogTests(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Migration_creates_the_full_catalog_in_the_praxy_schema()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'praxy' ORDER BY table_name", conn);
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        string[] expected =
        [
            "api_keys", "audit_log", "events", "organization_members", "organizations",
            "platforms", "projects", "schema_jobs", "sessions", "users",
        ];
        foreach (var table in expected)
            Assert.Contains(table, tables);
        Assert.Contains("__ef_migrations_history", tables);
    }

    [Fact]
    public async Task Console_project_is_seeded_org_less()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT organization_id IS NULL FROM praxy.projects WHERE id = 'console'", conn);
        var orgIsNull = (bool?)await cmd.ExecuteScalarAsync();
        Assert.True(orgIsNull);
    }

    [Fact]
    public async Task Startup_is_idempotent_against_an_already_migrated_database()
    {
        // A second host against the same database — as in a restart or rolling deploy —
        // must come up clean: zero pending migrations, seed already present.
        using var second = new PraxyApiFactory(ConnectionString);
        var response = await second.CreateClient().GetAsync("/v1/health");
        Assert.Equal(200, (int)response.StatusCode);
    }
}
