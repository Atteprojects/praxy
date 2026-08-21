using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Praxy.Tests.Integration.Infrastructure;

/// <summary>
/// Boots a fresh database + API host per test class. Classes share the collection's
/// Postgres container; xUnit runs classes in one collection sequentially.
/// </summary>
[Collection("postgres")]
public abstract class ApiTestBase(PostgresContainerFixture pg) : IAsyncLifetime
{
    protected PostgresContainerFixture Postgres { get; } = pg;
    protected string ConnectionString { get; private set; } = "";
    protected PraxyApiFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;

    protected virtual IDictionary<string, string?>? ExtraSettings => null;

    /// <summary>Test-only service overrides (fake email sender, fake OAuth provider, …).</summary>
    protected virtual Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? TestServices => null;

    public async Task InitializeAsync()
    {
        ConnectionString = await Postgres.CreateFreshDatabaseAsync();
        Factory = new PraxyApiFactory(ConnectionString, ExtraSettings, TestServices);
        // No cookie jar: tests authenticate explicitly via X-Praxy-Session so that
        // "unauthenticated" requests are actually unauthenticated.
        Client = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
    }

    /// <summary>Virtual so a subclass can clean up resources the disposed factory itself won't (see SiteTests' own override — a site's container is deliberately left running when api shuts down, so nothing else stops it).</summary>
    public virtual Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }

    protected async Task<(string SessionToken, JsonElement Account)> ClaimAsync(
        string email = "owner@praxy.test", string password = "hunter2hunter2", string? setupToken = null)
    {
        var response = await Client.PostAsJsonAsync("/v1/console/claim",
            new { email, password, name = "Owner", setupToken });
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return (body.GetProperty("session").GetProperty("token").GetString()!, body.GetProperty("account"));
    }

    /// <summary>
    /// Seeds a second console operator with their own organization. Claim is one-shot, and there
    /// is no operator-invite endpoint, so the row goes in directly — reusing the owner's password
    /// hash so the account can log in through the real endpoint.
    /// </summary>
    protected async Task<(string Token, JsonElement Account)> CreateSecondOperatorAsync(
        string email = "second@praxy.test", string password = "hunter2hunter2")
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using (var cmd = new NpgsqlCommand(
            """
            WITH owner AS (
                SELECT password_hash FROM praxy.users
                WHERE project_id = 'console' ORDER BY created_at LIMIT 1
            ), new_user AS (
                INSERT INTO praxy.users
                    (id, project_id, email, password_hash, name, email_verified, status, labels, prefs,
                     created_at, updated_at)
                SELECT $1, 'console', $2, owner.password_hash, 'Second', true, true, '{}', '{}', now(), now()
                FROM owner
                RETURNING id
            ), new_org AS (
                INSERT INTO praxy.organizations (id, name, limits, created_at)
                VALUES ($3, 'Second', '{}', now())
                RETURNING id
            )
            INSERT INTO praxy.organization_members (organization_id, user_id, role, created_at)
            SELECT new_org.id, new_user.id, 'owner', now() FROM new_org, new_user
            """, conn))
        {
            cmd.Parameters.AddWithValue(Guid.CreateVersion7());
            cmd.Parameters.AddWithValue(email);
            cmd.Parameters.AddWithValue(Guid.CreateVersion7());
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        var response = await Client.PostAsJsonAsync("/v1/console/sessions", new { email, password });
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return (body.GetProperty("session").GetProperty("token").GetString()!, body.GetProperty("account"));
    }

    protected static HttpRequestMessage Authed(HttpMethod method, string url, string sessionToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Praxy-Session", sessionToken);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    protected static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Asserts the public error envelope shape and returns it.</summary>
    protected static async Task<JsonElement> AssertError(
        HttpResponseMessage response, int expectedCode, string expectedType)
    {
        var body = await ReadJson(response);
        Assert.Equal(expectedCode, (int)response.StatusCode);
        Assert.Equal(expectedCode, body.GetProperty("code").GetInt32());
        Assert.Equal(expectedType, body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("version").GetString()));
        var requestId = body.GetProperty("requestId").GetString();
        Assert.False(string.IsNullOrEmpty(requestId));
        Assert.Equal(requestId, response.Headers.GetValues("X-Praxy-Request-Id").Single());
        return body;
    }
}
