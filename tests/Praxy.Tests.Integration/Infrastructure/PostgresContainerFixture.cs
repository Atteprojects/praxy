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

    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:17-alpine").Build();

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
