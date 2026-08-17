using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// architecture.md §11 claims "statement_timeout on every connection" as a resource-exhaustion
/// mitigation. Phase 9's security pass found that true only for DDL/schema-job connections (each
/// sets its own via <c>SET LOCAL</c>) — the shared pool the data plane's row reads/writes actually
/// run on had no timeout at all. Fixed in <c>Program.cs</c> via the connection string's
/// <c>Options</c> startup parameter; this proves it against the real DI-registered
/// <see cref="NpgsqlDataSource"/>, not a standalone connection string.
/// </summary>
public class StatementTimeoutTests(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings => new Dictionary<string, string?>
    {
        ["Praxy:Database:StatementTimeoutSeconds"] = "1",
    };

    [Fact]
    public async Task The_shared_connection_pool_honors_a_configured_statement_timeout()
    {
        var dataSource = Factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT pg_sleep(5)", conn);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("57014", ex.SqlState); // query_canceled
    }
}
