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

    /// <summary>
    /// <see cref="Praxy.Persistence.CatalogMigrator"/> raises this timeout away for its own session,
    /// because a migration is not request work and being cancelled mid-upgrade stops the instance
    /// from starting at all. That opt-out is only safe because Npgsql resets session state when the
    /// connection goes back to the pool — without that, one migration would silently disarm the
    /// data-plane protection the test above exists to guarantee, for every connection afterward.
    /// Both halves are asserted here rather than assumed, since the leak would be invisible: the
    /// only symptom is a timeout that no longer fires.
    /// </summary>
    [Fact]
    public async Task Raising_the_timeout_for_one_session_does_not_leak_it_back_to_the_pool()
    {
        var dataSource = Factory.Services.GetRequiredService<NpgsqlDataSource>();

        // What CatalogMigrator does for the duration of a migration. Two seconds would be cancelled
        // under the configured one-second budget, so this succeeding is the opt-out working.
        await using (var raised = await dataSource.OpenConnectionAsync())
        {
            await using var optOut = new NpgsqlCommand("SET statement_timeout = 0", raised);
            await optOut.ExecuteNonQueryAsync();
            await using var slow = new NpgsqlCommand("SELECT pg_sleep(2)", raised);
            await slow.ExecuteNonQueryAsync();
        }

        // The pool has exactly one physical connection here, so this is necessarily the same one
        // handed back — and it must have the pool's own timeout again, not the raised one.
        await using var reused = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT pg_sleep(5)", reused);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("57014", ex.SqlState);
    }
}
