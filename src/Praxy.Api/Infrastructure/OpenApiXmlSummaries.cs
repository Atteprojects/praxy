using System.Collections.Frozen;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Gives every operation a <c>summary</c> (and a <c>description</c>) from the XML doc comment on
/// its handler method.
///
/// <para><b>Why this exists at all.</b> .NET 10's own XML-comment support populates schema
/// descriptions — enabling <c>GenerateDocumentationFile</c> in the SDK-generation Phase 1 gave 30
/// of 191 schemas theirs — but it never populated a single operation summary: 1 of 290, and that
/// one came from an explicit <c>.WithSummary()</c>. The difference is accessibility. The DTOs it
/// does document are <c>public</c> records; every endpoint handler here is a <c>private static</c>
/// method, and a private member is not part of the surface that support reads.</para>
///
/// <para><b>Why not the two obvious alternatives.</b> Making 290 handlers public to satisfy a
/// documentation feature trades real encapsulation for prose. Writing <c>.WithSummary("…")</c> at
/// 290 mapping sites puts the description of a method somewhere other than the method, where it
/// drifts — the same argument that made <see cref="OpenApiOperationIds"/> a transformer rather
/// than 290 <c>.WithName()</c> calls. A normal <c>/// &lt;summary&gt;</c> serves the C# reader and
/// the SDK consumer from one place.</para>
///
/// <para><b>Keyed by declaring type and method name, not by the XML member id.</b> A real member
/// id encodes every parameter type (<c>M:…UpdateEmail(System.String,…UpdateUserEmailRequest,…)</c>),
/// which means reconstructing .NET's exact type-name mangling from
/// <see cref="MethodInfo"/> — generics and all — to look anything up. Handler names are unique
/// within an endpoint class, so the short key is enough; an overloaded name is detected and
/// skipped rather than resolved to whichever entry happened to parse first.</para>
/// </summary>
public sealed class OpenApiXmlSummaries : IOpenApiOperationTransformer
{
    private static readonly Lazy<FrozenDictionary<string, XmlDoc>> Docs = new(Load);

    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var method = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<MethodInfo>()
            .FirstOrDefault();
        if (method?.DeclaringType?.FullName is not { } declaringType)
            return Task.CompletedTask;

        if (!Docs.Value.TryGetValue($"{declaringType}.{method.Name}", out var doc))
            return Task.CompletedTask;

        // Never overwrite an explicit .WithSummary() — an endpoint that says something deliberate
        // at the mapping site outranks whatever its handler's doc comment happens to say.
        operation.Summary ??= doc.Summary;
        operation.Description ??= doc.Remarks;
        return Task.CompletedTask;
    }

    private readonly record struct XmlDoc(string? Summary, string? Remarks);

    /// <summary>
    /// Reads the XML documentation file next to the assembly. Missing is not an error: the document
    /// is only served in Development, and a deployment that trims the file should lose doc prose,
    /// not fail to start.
    /// </summary>
    private static FrozenDictionary<string, XmlDoc> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Praxy.Api.xml");
        if (!File.Exists(path))
            return FrozenDictionary<string, XmlDoc>.Empty;

        var members = new Dictionary<string, XmlDoc>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            if (name is null || !name.StartsWith("M:", StringComparison.Ordinal))
                continue;

            // "M:Namespace.Type.Method(args)" → "Namespace.Type.Method".
            var signature = name[2..];
            var parenthesis = signature.IndexOf('(');
            if (parenthesis >= 0)
                signature = signature[..parenthesis];

            if (!members.TryAdd(signature, ToDoc(member)))
                ambiguous.Add(signature);
        }

        foreach (var key in ambiguous)
            members.Remove(key);

        return members.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static XmlDoc ToDoc(XElement member) =>
        new(Normalize(member.Element("summary")), Normalize(member.Element("remarks")));

    /// <summary>
    /// XML doc text arrives wrapped and indented exactly as it was typed. A summary is a single
    /// line in every OpenAPI viewer and in a generated SDK's doc comment, so the source's line
    /// breaks are noise rather than meaning.
    /// </summary>
    private static string? Normalize(XElement? element)
    {
        if (element is null)
            return null;
        var text = string.Join(' ', element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text;
    }
}
