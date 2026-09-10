using System.Net;
using System.Text;
using System.Text.Json;
using Praxy.Sdk;

namespace Praxy.Sdk.Tests;

/// <summary>
/// The generated C# surface, exercised rather than inspected. The generator's own tests assert the
/// emitted <em>text</em>; these assert that the emitted code sends the right request and decodes
/// the right response — the part a template change can break while still looking correct in a diff.
/// </summary>
public class UsersServiceTests
{
    private static string UserJson(string id = "u1") => $$"""
        {"id":"{{id}}","email":"a@b.com","name":"A","emailVerified":false,"status":true,
         "labels":[],"prefs":{},"createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z"}
        """;

    [Fact]
    public async Task List_sends_the_key_and_project_headers()
    {
        var transport = new RecordingHandler("""{"total":0,"users":[]}""");
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        await client.Users.List();

        Assert.Equal("key1.secret", transport.Request!.Headers.GetValues("X-Praxy-Key").Single());
        Assert.Equal("proj1", transport.Request.Headers.GetValues("X-Praxy-Project").Single());
    }

    [Fact]
    public async Task List_puts_paging_in_the_query_string()
    {
        // These parameters only exist because the API declares the query keys its handler reads.
        // If that declaration were lost, this method would regenerate without them and fail here.
        var transport = new RecordingHandler("""{"total":0,"users":[]}""");
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        await client.Users.List(limit: 50, offset: 100, search: "ada");

        var query = transport.Request!.RequestUri!.Query;
        Assert.Contains("limit=50", query, StringComparison.Ordinal);
        Assert.Contains("offset=100", query, StringComparison.Ordinal);
        Assert.Contains("search=ada", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unset_optional_parameter_is_omitted_from_the_query_string()
    {
        var transport = new RecordingHandler("""{"total":0,"users":[]}""");
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        await client.Users.List(limit: 10);

        var query = transport.Request!.RequestUri!.Query;
        Assert.Contains("limit=10", query, StringComparison.Ordinal);
        Assert.DoesNotContain("offset", query, StringComparison.Ordinal);
        Assert.DoesNotContain("search", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_omits_an_unset_optional_body_field_rather_than_sending_null()
    {
        // Mirrors the server's own WhenWritingNull: omitting a field is not the same request as
        // sending an explicit null, and several endpoints read the difference.
        var transport = new RecordingHandler(UserJson());
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        await client.Users.Create(email: "a@b.com", password: "hunter2");

        using var body = JsonDocument.Parse(transport.RequestBody!);
        Assert.Equal("a@b.com", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("hunter2", body.RootElement.GetProperty("password").GetString());
        Assert.False(body.RootElement.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task A_path_parameter_lands_in_the_url_not_the_body()
    {
        var transport = new RecordingHandler(UserJson());
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        await client.Users.UpdateEmail("u1", email: "new@b.com");

        Assert.Equal("/v1/users/u1/email", transport.Request!.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(transport.RequestBody!);
        Assert.Equal("new@b.com", body.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_204_method_completes_without_decoding_a_body()
    {
        var transport = new RecordingHandler("", HttpStatusCode.NoContent);
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        await client.Users.DeleteSession("u1", "s1");

        Assert.Equal(HttpMethod.Delete, transport.Request!.Method);
        Assert.Equal("/v1/users/u1/sessions/s1", transport.Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_list_decodes_into_the_generated_record_reusing_the_hand_written_AppUser()
    {
        var transport = new RecordingHandler($$"""{"total":2,"users":[{{UserJson()}},{{UserJson("u2")}}]}""");
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        var page = await client.Users.List();

        Assert.NotNull(page);
        Assert.Equal(2, page.Total);
        Assert.Equal("u2", page.Users[1].Id);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), page.Users[0].CreatedAt);
    }

    [Fact]
    public async Task An_error_envelope_becomes_the_matching_typed_exception()
    {
        var transport = new RecordingHandler(
            """{"message":"API key required.","code":401,"type":"unauthorized","version":"1","requestId":"r1"}""",
            HttpStatusCode.Unauthorized);
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        var error = await Assert.ThrowsAsync<PraxyAuthException>(() => client.Users.List());

        Assert.Equal("unauthorized", error.Type);
        Assert.Equal(401, error.Code);
        Assert.Equal("r1", error.RequestId);
    }

    [Fact]
    public async Task A_validation_envelope_carries_its_per_field_messages()
    {
        var transport = new RecordingHandler(
            """{"message":"Invalid.","code":400,"type":"argument_invalid","version":"1","requestId":"r2","fields":{"email":["Must be an email."]}}""",
            HttpStatusCode.BadRequest);
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        var error = await Assert.ThrowsAsync<PraxyValidationException>(
            () => client.Users.Create(email: "nope"));

        Assert.Equal(["Must be an email."], error.For("email"));
        Assert.Empty(error.For("name"));
    }

    [Fact]
    public async Task A_transport_failure_is_a_network_exception_not_an_api_one()
    {
        var transport = new ThrowingHandler(new HttpRequestException("connection refused"));
        using var client = new PraxyClient("https://example.test", "proj1", "key1.secret", new HttpClient(transport));

        var error = await Assert.ThrowsAsync<PraxyNetworkException>(() => client.Users.List());

        Assert.IsType<HttpRequestException>(error.Cause);
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            if (request.Content is not null)
                RequestBody = await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw error;
    }
}
