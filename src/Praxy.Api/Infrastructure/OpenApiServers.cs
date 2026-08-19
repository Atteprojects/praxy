using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Pins the document's <c>servers</c> to a relative URL.
///
/// By default .NET stamps in whichever address the generating instance happened to be reached on, so
/// the committed snapshot (docs/openapi/v1.json — the published reference for anyone not running an
/// instance) advertised a developer's localhost port: it has shipped as
/// <c>http://localhost:5090/</c> since Phase 0 and briefly as <c>http://127.0.0.1:5099/</c>. Importing
/// that into Postman or Scalar points every request at a machine that is not the reader's.
///
/// A relative <c>/</c> is correct for every deployment — Praxy is self-hosted, so the host is
/// whatever the operator runs it on — and it makes regeneration deterministic, which is what lets
/// the snapshot be diffed and asserted on rather than churning on the generator's port.
/// </summary>
public sealed class OpenApiServers : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Servers =
        [
            new OpenApiServer
            {
                Url = "/",
                Description = "This Praxy instance. Paths are relative to wherever the API is hosted.",
            },
        ];
        return Task.CompletedTask;
    }
}
