using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Corrects a mismatch between what the OpenAPI schema says and what actually crosses the wire.
///
/// <c>Program.cs</c> sets <c>DefaultIgnoreCondition = WhenWritingNull</c> on every minimal-API
/// response: a property whose value is null is dropped from the JSON entirely, so it arrives
/// <c>undefined</c> to a client — never a present <c>null</c>. But .NET's schema generation
/// (<c>JsonSchemaExporter</c>) derives <c>type</c> and <c>required</c> from the C# type and
/// constructor shape alone, with no awareness of that serializer option. A nullable property —
/// <c>string? Foo</c> — is described as <c>{"type":["null","string"]}</c>, and for most of Praxy's
/// DTOs (positional records, whose parameters are all constructor arguments) it is *also* marked
/// <c>required</c>, since JsonSchemaExporter's notion of "required" means "the constructor needs a
/// value," which a missing key still satisfies for a nullable parameter (STJ passes <c>null</c>). The
/// document ends up saying "this key is always present and may be null" when the real contract is
/// "this key is present only when non-null, and never appears as null."
///
/// A generator that trusts the document as written reintroduces the exact bug PR #55 fixed —
/// <c>foo: T | null</c> instead of <c>foo?: T</c> — with machine-generated authority behind it. This
/// transformer makes the document tell the truth instead: for every property whose type includes
/// <c>null</c>, drop <c>null</c> from <c>type</c> and drop the property from <c>required</c>.
///
/// A nullable property whose type is itself a named/composed schema — <c>JsonNode?</c>, <c>JsonElement?</c>
/// — can't carry "null" as a sibling of a <c>$ref</c>, so the generator represents it instead as
/// <c>oneOf: [{"type":"null"}, {"$ref": ...}]</c>. Same bug, different encoding: this unwraps that
/// too, replacing the property with the referenced schema directly and dropping it from <c>required</c>.
///
/// Registered as a *document* transformer, not a schema transformer, deliberately: a schema
/// transformer fires on a property's own schema node before its `oneOf` branches (each themselves a
/// schema, one of them a `$ref` to a named component) are attached — verified against a running
/// instance, not assumed, after the schema-transformer version silently left every `oneOf` case
/// untouched. Document transformers run last, once the whole document (including every named
/// component) is fully built, so `oneOf` is populated by the time this walks it.
///
/// This is safe to apply uniformly (no per-schema opt-out) because the one case that must be
/// exempted — a `Row`'s dynamic column values, which bypass `WhenWritingNull` because they're
/// `JsonNode` content copied verbatim rather than a named CLR property — has no `properties`/
/// `required` for this to touch in the first place: `JsonObject`'s schema is a bare `{"type":"object"}`
/// with no declared members. (A `JsonNode`-*typed* property that is never actually null in practice,
/// like `AppUserResponse.Prefs`, is fixed at the source instead — declared non-nullable in C# —
/// rather than papered over here, the same principle applied one level up.)
/// </summary>
public sealed class OpenApiWireNullability : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        if (document.Components?.Schemas is null)
            return Task.CompletedTask;

        foreach (var schema in document.Components.Schemas.Values)
        {
            if (schema is OpenApiSchema { Properties: not null } concreteSchema)
                FixProperties(concreteSchema);
        }

        return Task.CompletedTask;
    }

    private static void FixProperties(OpenApiSchema schema)
    {
        foreach (var name in schema.Properties!.Keys.ToList())
        {
            if (schema.Properties[name] is not OpenApiSchema concrete)
                continue;

            if (concrete.Type is { } type && type.HasFlag(JsonSchemaType.Null))
            {
                concrete.Type = type & ~JsonSchemaType.Null;
                schema.Required?.Remove(name);
                continue;
            }

            if (concrete.OneOf is { Count: 2 } oneOf)
            {
                var nullBranch = oneOf.FirstOrDefault(o => o is OpenApiSchema { Type: JsonSchemaType.Null });
                var realBranch = oneOf.FirstOrDefault(o => !ReferenceEquals(o, nullBranch));
                if (nullBranch is not null && realBranch is not null)
                {
                    schema.Properties[name] = realBranch;
                    schema.Required?.Remove(name);
                }
            }
        }
    }
}
