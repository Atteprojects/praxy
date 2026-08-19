using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Praxy.Core.Errors;
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

    [Fact]
    public async Task Entries_come_back_newest_first_and_paginate()
    {
        var (operatorToken, projectId) = await SetupProjectAsync(); // 1: projects.create
        for (var i = 0; i < 4; i++)
            await AddPlatformAsync(operatorToken, projectId, $"host{i}.example.com"); // 4: platforms.create

        var page1 = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/audit?limit=2&offset=0", operatorToken)));
        var page2 = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/audit?limit=2&offset=2", operatorToken)));
        var page3 = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/audit?limit=2&offset=4", operatorToken)));

        Assert.Equal(5, page1.GetProperty("total").GetInt32());
        var page1Ids = EntryIds(page1);
        var page2Ids = EntryIds(page2);
        Assert.Equal(2, page1Ids.Count);
        Assert.Equal(2, page2Ids.Count);
        Assert.Empty(page1Ids.Intersect(page2Ids));

        // Newest-first: projects.create happened before any platform, so it must be the one entry
        // left over on the third page, not lost or duplicated across pages.
        var page3Entries = page3.GetProperty("entries").EnumerateArray().ToList();
        Assert.Single(page3Entries);
        Assert.Equal("projects.create", page3Entries[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Filters_narrow_correctly_and_compose()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "one.example.com");
        var (_, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;
        await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/users/{userId}/name", operatorToken,
            new { name = "Ada" }));

        var byAction = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/audit?action=platforms.create", operatorToken)));
        Assert.Equal(1, byAction.GetProperty("total").GetInt32());

        var byResource = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get,
            $"/v1/console/projects/{projectId}/audit?resource={Uri.EscapeDataString($"user/{userId}")}",
            operatorToken)));
        Assert.Equal(1, byResource.GetProperty("total").GetInt32());
        Assert.Equal("users.name.update", byResource.GetProperty("entries")[0].GetProperty("action").GetString());

        // Compose: actor AND action together narrow past what either alone would (both operator
        // actions match the actor filter; only one is also users.name.update).
        var account = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get, "/v1/console/account", operatorToken)));
        var actor = $"admin:{account.GetProperty("id").GetString()}";
        var composed = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get,
            $"/v1/console/projects/{projectId}/audit?actor={Uri.EscapeDataString(actor)}&action=users.name.update",
            operatorToken)));
        Assert.Equal(1, composed.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_second_operator_gets_project_not_found_and_the_reserved_console_project_is_refused()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "one.example.com");
        var (otherToken, _) = await CreateSecondOperatorAsync();

        var response = await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/audit", otherToken));
        await AssertError(response, 404, ErrorTypes.ProjectNotFound);

        var reserved = await Client.SendAsync(Authed(
            HttpMethod.Get, "/v1/console/projects/console/audit", operatorToken));
        await AssertError(reserved, 404, ErrorTypes.ProjectNotFound);
    }

    /// <summary>
    /// <c>instance.claim</c> has a NULL <c>project_id</c> and would be invisible to any project-scoped
    /// query — that's why <c>GET /v1/console/audit</c> exists as a separate surface, and why a
    /// project-scoped read must never see it.
    /// </summary>
    [Fact]
    public async Task Instance_level_entries_surface_only_through_the_instance_endpoint()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var instance = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, "/v1/console/audit", operatorToken)));
        var instanceEntries = instance.GetProperty("entries").EnumerateArray().ToList();
        Assert.Contains(instanceEntries, e => e.GetProperty("action").GetString() == "instance.claim");
        Assert.DoesNotContain(instanceEntries, e => e.GetProperty("action").GetString() == "projects.create");
        // Null properties are omitted from the wire entirely (WhenWritingNull), so "no projectId
        // key" and "a null projectId" are the same thing here.
        Assert.All(instanceEntries, e =>
            Assert.True(!e.TryGetProperty("projectId", out var pid) || pid.ValueKind is JsonValueKind.Null));

        var project = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/audit", operatorToken)));
        var projectEntries = project.GetProperty("entries").EnumerateArray().ToList();
        Assert.Contains(projectEntries, e => e.GetProperty("action").GetString() == "projects.create");
        Assert.DoesNotContain(projectEntries, e => e.GetProperty("action").GetString() == "instance.claim");
    }

    /// <summary>
    /// The regression this whole item exists to close: a <c>users.write</c> key can reset a
    /// password exactly like a console operator, and until now none of it left a trace.
    /// </summary>
    [Fact]
    public async Task A_key_driven_password_reset_writes_a_key_actor_and_shows_up_in_the_read_surface()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, user) = await SignupAsync(projectId, "ada@example.com");
        var userId = user.GetProperty("id").GetString()!;
        var (keyId, keySecret) = await CreateApiKeyAsync(operatorToken, projectId, "users.write");

        var reset = await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/users/{userId}/password", projectId, apiKey: keySecret,
            body: new { password = "new-correct-horse-battery" }));
        Assert.Equal(200, (int)reset.StatusCode);

        var audit = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/audit?action=users.password.reset", operatorToken)));
        Assert.Equal(1, audit.GetProperty("total").GetInt32());
        var entry = audit.GetProperty("entries")[0];
        Assert.Equal($"key:{keyId}", entry.GetProperty("actor").GetString());
        Assert.Equal($"user/{userId}", entry.GetProperty("resource").GetString());
    }

    /// <summary>
    /// There is no FK from <c>audit_log</c> to <c>projects</c> — deliberately, so a project delete
    /// (gap #5, not built yet) can never erase an audit trail. Simulates what that delete will
    /// eventually do by removing the project row directly, and asserts the orphaned entries neither
    /// cascade away nor break any other query against the same table.
    /// </summary>
    [Fact]
    public async Task A_deleted_projects_audit_rows_dont_break_other_queries()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await AddPlatformAsync(operatorToken, projectId, "doomed.example.com");

        var survivorId = (await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name = "Survivor" }))))
            .GetProperty("id").GetString()!;
        await AddPlatformAsync(operatorToken, survivorId, "survivor.example.com");

        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM praxy.projects WHERE id = $1", conn);
            cmd.Parameters.AddWithValue(projectId);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM praxy.audit_log WHERE project_id = $1", conn);
            cmd.Parameters.AddWithValue(projectId);
            Assert.True((long)(await cmd.ExecuteScalarAsync())! > 0);
        }

        // The surviving project's own audit trail is unaffected by the orphaned rows sitting
        // elsewhere in the same table...
        var survivorAudit = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{survivorId}/audit", operatorToken)));
        Assert.Equal(2, survivorAudit.GetProperty("total").GetInt32());

        // ...and the now-dangling project id 404s cleanly (ConsoleProjectFilter needs a live,
        // owned project — that's correct authorization, not the query breaking).
        var orphaned = await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/audit", operatorToken));
        await AssertError(orphaned, 404, ErrorTypes.ProjectNotFound);
    }

    private static List<string?> EntryIds(JsonElement listResponse) =>
        [.. listResponse.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString())];
}
