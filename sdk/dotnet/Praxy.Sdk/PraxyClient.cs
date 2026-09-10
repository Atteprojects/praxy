using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Praxy.Sdk.Services.Generated;

namespace Praxy.Sdk;

/// <summary>
/// The Praxy server client: one instance per project, authenticated with a project API key.
///
/// <para><b>This is a server SDK and only a server SDK.</b> A project API key is not scoped to one
/// end user the way a session is, and a key shipped inside a desktop or mobile application is
/// extractable from it. There is deliberately no session-based constructor here: unlike
/// <c>praxy_core</c>, which is one dual-mode package because it is also the base of the Flutter
/// client SDK, this package has no client half to serve.</para>
///
/// <para>Safe to keep for the lifetime of the process and to share across threads, like the
/// <see cref="HttpClient"/> it wraps. Pass your own <see cref="HttpClient"/> to control handlers,
/// proxies or timeouts; this type then does not dispose it, on the usual principle that whoever
/// created it owns it.</para>
/// </summary>
public sealed class PraxyClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Mirrors the server's own WhenWritingNull: an unset property is omitted rather than sent
        // as an explicit null. On several endpoints those are not the same request — omitting a
        // field means "leave it alone", where an explicit null would mean "clear it".
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _projectId;
    private readonly string _apiKey;

    /// <param name="endpoint">Instance base URL, e.g. <c>https://api.example.com</c>.</param>
    /// <param name="projectId">The project this client acts on.</param>
    /// <param name="apiKey">A project API key, <c>&lt;keyId&gt;.&lt;secret&gt;</c>.</param>
    /// <param name="httpClient">Optional; supply one to control handlers or timeouts.</param>
    public PraxyClient(string endpoint, string projectId, string apiKey, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _projectId = projectId;
        _apiKey = apiKey;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress ??= new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");

        Users = new UsersService(this);
    }

    /// <summary>Server-side app-user administration. Generated — see <c>sdk/generator</c>.</summary>
    public UsersService Users { get; }

    /// <summary>
    /// Sends one API call and deserializes its body. Internal to the SDK: every service method
    /// funnels through here so header and error-mapping rules exist once.
    /// </summary>
    /// <returns>The deserialized body, or <c>default</c> for a 204 or an empty body.</returns>
    public async Task<T?> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body = null,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, BuildUri(path, query));
        request.Headers.TryAddWithoutValidation("X-Praxy-Project", _projectId);
        request.Headers.TryAddWithoutValidation("X-Praxy-Key", _apiKey);
        if (body is not null)
            request.Content = JsonContent.Create(StripNulls(body), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A caller's own cancellation is theirs to see; anything else never reached the server.
            throw new PraxyNetworkException($"{method} {path} did not reach the server: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                return default;

            try
            {
                return await response.Content
                    .ReadFromJsonAsync<T>(Json, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new PraxyDecodeException($"Could not read the response to {method} {path} as {typeof(T).Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Drops null entries from a dictionary body.
    ///
    /// <para>Generated services pass their request body as a <c>Dictionary&lt;string, object?&gt;</c>
    /// with one entry per parameter, and an unset optional parameter is null. That must be
    /// <em>absent</em> from the JSON, not present-and-null: the API serializes with
    /// <c>WhenWritingNull</c> and reads the difference — omitting a field means "leave it alone"
    /// where an explicit null can mean "clear it".</para>
    ///
    /// <para><see cref="JsonIgnoreCondition.WhenWritingNull"/> does not cover this, because it
    /// applies to a type's <em>properties</em> and these are dictionary <em>entries</em>. Caught by
    /// a test asserting the absence rather than the value, which is the only way this shows up.
    /// <c>praxy_core</c> gets the same result from Dart's <c>?name</c> spread at the call site; C#
    /// has no equivalent, so it happens once here instead of in every generated method.</para>
    /// </summary>
    private static object StripNulls(object body) =>
        body is IReadOnlyDictionary<string, object?> map
            ? map.Where(entry => entry.Value is not null).ToDictionary(entry => entry.Key, entry => entry.Value)
            : body;

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string?>? query)
    {
        // Relative to BaseAddress, so the leading slash has to go or Uri discards the base path.
        var relative = path.TrimStart('/');
        if (query is null || query.Count == 0)
            return new Uri(relative, UriKind.Relative);

        var pairs = query
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        var queryString = string.Join('&', pairs);
        return new Uri(queryString.Length == 0 ? relative : $"{relative}?{queryString}", UriKind.Relative);
    }

    /// <summary>
    /// Maps the API's error envelope onto the typed hierarchy. A non-2xx that is somehow not the
    /// envelope still produces a <see cref="PraxyApiException"/> carrying the status, rather than a
    /// decode error: the caller's request did fail, and saying so badly beats reporting the wrong
    /// kind of failure.
    /// </summary>
    private static async Task<PraxyException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var requestId = response.Headers.TryGetValues("X-Praxy-Request-Id", out var ids) ? ids.FirstOrDefault() : null;

        ErrorEnvelope? envelope = null;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Not the envelope — a proxy's HTML error page, most likely.
        }

        var message = envelope?.Message ?? $"{status} {response.ReasonPhrase}";
        var type = envelope?.Type ?? "unknown";
        requestId ??= envelope?.RequestId;

        if (envelope?.Fields is { Count: > 0 } fields)
            return new PraxyValidationException(message, type, status, requestId, fields);

        return status switch
        {
            401 or 403 => new PraxyAuthException(message, type, status, requestId),
            404 => new PraxyNotFoundException(message, type, status, requestId),
            409 => new PraxyConflictException(message, type, status, requestId),
            429 => new PraxyRateLimitException(message, type, status, requestId, response.Headers.RetryAfter?.Delta),
            400 => new PraxyValidationException(message, type, status, requestId, new Dictionary<string, string[]>()),
            _ => new PraxyApiException(message, type, status, requestId),
        };
    }

    private sealed record ErrorEnvelope(
        string? Message, int Code, string? Type, string? Version, string? RequestId,
        Dictionary<string, string[]>? Fields);

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
