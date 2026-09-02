using DotNet.Testcontainers.Images;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Praxy.Tests.Integration.Infrastructure;

/// <summary>
/// One Postgres container for the whole collection — container startup dominates test time
/// otherwise. Each test class gets its own database inside it for isolation.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private int _dbCounter;

    // PostGIS-flavored image (geo columns/`near` queries, docs/research/geo-nearby.md), verified
    // against a real pull rather than guessed. postgis/postgis publishes no arm64 manifest at all
    // (checked across every current Postgres-17/PostGIS-minor combination) — only amd64. The
    // explicit Platform pin makes this portable to an arm64 dev machine (this was written and
    // verified on one) via emulation, instead of a hard "no matching manifest" failure; without it,
    // Testcontainers would refuse to even pull the image on such a host.
    public PostgreSqlContainer Container { get; } =
        new PostgreSqlBuilder(new DockerImage("postgis/postgis:17-3.6-alpine", new Platform("linux/amd64"))).Build();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    public async Task<string> CreateFreshDatabaseAsync()
    {
        var name = $"praxy_test_{Interlocked.Increment(ref _dbCounter)}";
        await using var conn = new NpgsqlConnection(Container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = name,
        }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainerFixture>;
