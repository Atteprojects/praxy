using System.Text.RegularExpressions;
using Npgsql;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Every <c>praxy.audit_log</c> row today comes from a console-authenticated action (roadmap Phase
/// 9: "admin actions distinguished from user actions") — the actor tag must read unambiguously as an
/// operator, never as the <c>user:&lt;id&gt;</c> permission-role format architecture.md §4.3 already
/// reserves for app users.
/// </summary>
public partial class AuditLogTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [GeneratedRegex(@"^admin:[0-9a-fA-F-]{36}$")]
    private static partial Regex AdminActorFormat();

    [Fact]
    public async Task Console_operator_actions_are_tagged_as_admin_not_user()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        // A second console-admin action beyond project creation, so more than one call site is covered.
        await AddPlatformAsync(operatorToken, projectId, "example.com");

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT actor, action FROM praxy.audit_log ORDER BY created_at", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var seenActions = new List<string>();
        while (await reader.ReadAsync())
        {
            var actor = reader.GetString(0);
            var action = reader.GetString(1);
            seenActions.Add(action);
            Assert.True(AdminActorFormat().IsMatch(actor), $"'{actor}' (action '{action}') is not tagged admin:<id>.");
            Assert.DoesNotContain("user:", actor, StringComparison.Ordinal);
        }

        Assert.Contains("projects.create", seenActions);
        Assert.Contains("platforms.create", seenActions);
    }
}
