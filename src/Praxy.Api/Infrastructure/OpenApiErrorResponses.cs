using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Documents the error contract on every operation. Praxy's error <c>type</c> strings are public API
/// (CLAUDE.md), but the generated document described only request bodies — an SDK author reading
/// <c>docs/openapi/v1.json</c> could see exactly what to send and nothing about what comes back when
/// it fails.
///
/// Every non-2xx response in Praxy is the same <see cref="ErrorEnvelope"/> — that is what
/// <see cref="ErrorHandlingMiddleware"/> guarantees — so this is expressed as OpenAPI's <c>default</c>
/// response rather than by guessing which status codes each operation can reach. `default` means
/// precisely "any status not otherwise listed", which is true here and stays true as endpoints
/// change; an enumerated list of 400/401/404/… would be a guess that silently rots.
///
/// 429 is listed explicitly on rate-limited operations because it is the one error carrying extra
/// headers a client is expected to act on.
///
/// Registered twice — as an operation transformer (which references the schema) and as a document
/// transformer (which defines it once under <c>components</c>). Emitting the schema inline instead
/// would repeat the whole envelope on all ~180 operations.
/// </summary>
public sealed class OpenApiErrorResponses : IOpenApiOperationTransformer, IOpenApiDocumentTransformer
{
    private const string Json = "application/json";
    private const string SchemaId = "ErrorEnvelope";

    /// <summary>The one endpoint that never returns the envelope: liveness, polled by load balancers.</summary>
    private const string HealthPath = "v1/health";

    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        if (context.Description.RelativePath is HealthPath)
            return Task.CompletedTask;

        operation.Responses ??= [];
        operation.Responses["default"] = new OpenApiResponse
        {
            Description =
                "Error. Every non-2xx response uses this envelope. `type` is a stable, machine-readable "
                + "string SDKs may switch on; `code` repeats the HTTP status; `requestId` matches the "
                + "X-Praxy-Request-Id response header; `fields` is present only on validation failures.",
            Content = ErrorContent(),
        };

        if (IsRateLimited(context))
        {
            operation.Responses["429"] = new OpenApiResponse
            {
                Description =
                    "Rate limit exceeded (`general_rate_limit_exceeded`). Retry after the number of "
                    + "seconds in Retry-After; RateLimit-Limit/-Remaining/-Reset describe the bucket. "
                    + "Buckets partition on project plus caller identity, falling back to source address.",
                Content = ErrorContent(),
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["Retry-After"] = Header("Seconds to wait before retrying."),
                    ["RateLimit-Limit"] = Header("Requests permitted per window."),
                    ["RateLimit-Remaining"] = Header("Requests left in the current window."),
                    ["RateLimit-Reset"] = Header("Seconds until the window resets."),
                },
            };
        }

        return Task.CompletedTask;
    }

    /// <summary>Defines the schema the operation transformer's references point at.</summary>
    public async Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas[SchemaId] =
            await context.GetOrCreateSchemaAsync(typeof(ErrorEnvelope), null, ct);
    }

    private static Dictionary<string, OpenApiMediaType> ErrorContent() =>
        new() { [Json] = new OpenApiMediaType { Schema = new OpenApiSchemaReference(SchemaId, null, null) } };

    private static OpenApiHeader Header(string description) => new()
    {
        Description = description,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    /// <summary>
    /// Read from the endpoint's own metadata rather than a hardcoded route list, so an endpoint that
    /// gains or loses <c>RequireRateLimiting</c> re-documents itself without anyone remembering to.
    /// </summary>
    private static bool IsRateLimited(OpenApiOperationTransformerContext context) =>
        context.Description.ActionDescriptor.EndpointMetadata
            ?.OfType<EnableRateLimitingAttribute>().Any() ?? false;
}
