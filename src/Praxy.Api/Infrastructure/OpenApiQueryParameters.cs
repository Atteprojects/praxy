using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// One query-string parameter an endpoint reads but the framework cannot see.
/// </summary>
/// <param name="Name">The query key, exactly as the handler reads it.</param>
/// <param name="Type">Its JSON type.</param>
/// <param name="Description">What it does, and the bounds the handler enforces.</param>
public sealed record QueryParameterDoc(string Name, JsonSchemaType Type, string Description);

/// <summary>
/// Declares query parameters that a handler reads out of <see cref="HttpContext"/>.
///
/// <para><b>The gap this closes.</b> .NET's OpenAPI generation documents only parameters the
/// framework itself binds. Praxy's list endpoints read <c>limit</c>/<c>offset</c>/<c>search</c>
/// directly from <c>HttpContext.Request.Query</c> — 37 such reads across 16 endpoint files — so
/// the document described them as taking no query parameters at all. Only path parameters
/// survived. That is invisible to a human reading <c>docs/api-reference.md</c> and actively
/// harmful to a generated SDK: <c>praxy_sdk_gen</c> emitted a <c>users.list()</c> that could never
/// page past the first 25 users, faithfully, because the document said there was nothing to pass.</para>
///
/// <para><b>Why declare rather than bind.</b> Rewriting the handlers to take
/// <c>[FromQuery] int? limit</c> would let the framework document them for free, but it changes
/// behaviour at the edges: today a non-numeric <c>?limit=abc</c> falls back to the default through
/// <c>int.TryParse</c>, where a bound parameter would fail binding and return a 400. These are
/// shipped endpoints, so the parameters are described where they are read, and the runtime keeps
/// doing exactly what it did. Binding them properly is a fine future change — it is just not a
/// documentation change.</para>
///
/// <para>The obvious risk is that a declaration drifts from what the handler actually reads.
/// <c>OpenApiDocumentTests.Every_query_parameter_a_handler_reads_is_documented</c> is the guard: it
/// reads the endpoint sources, finds every <c>Request.Query["…"]</c>, and fails if the operation
/// does not document it.</para>
/// </summary>
public sealed class OpenApiQueryParameters : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var declared = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<QueryParameterDoc>()
            .ToList();
        if (declared.Count == 0)
            return Task.CompletedTask;

        operation.Parameters ??= [];
        foreach (var parameter in declared)
        {
            // A parameter the framework already documented wins: it is bound for real, so its
            // schema came from the actual signature rather than from this description of it.
            if (operation.Parameters.Any(p => p.Name == parameter.Name))
                continue;

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.Name,
                In = ParameterLocation.Query,
                Required = false,
                Description = parameter.Description,
                Schema = new OpenApiSchema { Type = parameter.Type },
            });
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Attaches <see cref="QueryParameterDoc"/>s to an endpoint. Kept as extension methods so a call
/// site reads as one line next to the mapping it documents.
/// </summary>
public static class QueryParameterExtensions
{
    /// <summary>
    /// The pagination pair every list endpoint in this API reads through its own
    /// <c>ListParams(HttpContext)</c> helper. The bounds are stated because they are enforced
    /// silently: an out-of-range <c>limit</c> is not an error, it becomes the default.
    /// </summary>
    public static TBuilder WithPagination<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithQueryParameters(
            new QueryParameterDoc(
                "limit", JsonSchemaType.Integer,
                "Maximum items to return, 1-100. Outside that range, or unparseable, falls back to 25."),
            new QueryParameterDoc(
                "offset", JsonSchemaType.Integer,
                "Items to skip, 0-100000. Outside that range it is clamped, never rejected."));

    /// <summary>Free-text filter, alongside <see cref="WithPagination{TBuilder}"/>.</summary>
    public static TBuilder WithSearch<TBuilder>(this TBuilder builder, string description)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithQueryParameters(new QueryParameterDoc("search", JsonSchemaType.String, description));

    /// <summary>
    /// The row-listing family: the query DSL plus its two modifiers. <c>queries[]</c> and
    /// <c>queries</c> are both read — bracketed first, falling back to the bare name — because
    /// different HTTP clients serialise repeated values differently, and both are documented since
    /// both work.
    /// </summary>
    public static TBuilder WithRowQueries<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithQueryParameters(
            new QueryParameterDoc(
                "queries[]", JsonSchemaType.Array,
                "Query DSL clauses, repeated once per clause, e.g. `queries[]=equal(\"status\",\"done\")`."),
            new QueryParameterDoc(
                "queries", JsonSchemaType.Array,
                "Same as `queries[]`, for clients that repeat a bare parameter name. Only consulted when `queries[]` is absent."),
            new QueryParameterDoc(
                "total", JsonSchemaType.Boolean,
                "Whether to compute the total row count. Defaults to true; pass `false` to skip the count on a large table."),
            new QueryParameterDoc(
                "expand", JsonSchemaType.String,
                "Comma-separated relationship columns to embed in each row."))
        .WithQueryParameters();

    /// <summary>Reading a single row takes only the embed modifier.</summary>
    public static TBuilder WithRowExpand<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithQueryParameters(
            new QueryParameterDoc(
                "expand", JsonSchemaType.String,
                "Comma-separated relationship columns to embed in the row."));

    /// <summary>
    /// The audit-log filter family. Both audit endpoints read the same set through one
    /// <c>ListParams</c>, so they declare it from one place — two copies of this list would be two
    /// things to keep in step with a single helper.
    /// </summary>
    public static TBuilder WithAuditFilters<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithPagination().WithQueryParameters(
            new QueryParameterDoc("action", JsonSchemaType.String, "Exact audit action to filter by, e.g. `users.create`."),
            new QueryParameterDoc("actor", JsonSchemaType.String, "Exact operator id to filter by."),
            new QueryParameterDoc("resource", JsonSchemaType.String, "Exact resource identifier to filter by, e.g. `user/<id>`."),
            new QueryParameterDoc("from", JsonSchemaType.String, "ISO-8601 lower bound on `createdAt`, inclusive. Unparseable values are ignored, not rejected."),
            new QueryParameterDoc("to", JsonSchemaType.String, "ISO-8601 upper bound on `createdAt`, inclusive. Unparseable values are ignored, not rejected."));

    /// <summary><c>?force=true</c> on a destructive endpoint. What it overrides differs per resource, so the caller supplies it.</summary>
    public static TBuilder WithForce<TBuilder>(this TBuilder builder, string description)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithQueryParameters(new QueryParameterDoc("force", JsonSchemaType.Boolean, description));

    public static TBuilder WithQueryParameters<TBuilder>(
        this TBuilder builder, params QueryParameterDoc[] parameters)
        where TBuilder : IEndpointConventionBuilder
    {
        foreach (var parameter in parameters)
            builder.WithMetadata(parameter);
        return builder;
    }
}
