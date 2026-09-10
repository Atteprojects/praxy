using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Gives every operation an <c>operationId</c>, derived from the handler method rather than written
/// by hand at 290 call sites.
///
/// <para>An SDK generator names its methods from <c>operationId</c>. Without one it has to invent a
/// name from the verb and path, which is both ugly and — the real problem — <em>unstable</em>: a route
/// rename silently renames a public SDK method. Deriving from the handler's own name ties the SDK
/// surface to the C# method instead, which changes far less often and changes deliberately when it
/// does.</para>
///
/// <para><b>Why a transformer rather than <c>.WithName()</c> on each endpoint.</b> 290 call sites is
/// 290 chances to typo, to forget on a new endpoint, and to drift from the method the endpoint
/// actually runs. One derivation cannot drift, applies to every endpoint added later for free, and
/// keeps the mapping code readable. Where a derived name reads badly, an explicit
/// <c>.WithName("…")</c> still wins — this only fills in what is missing.</para>
///
/// <para><b>Shape:</b> <c>resource.methodName</c>, e.g. <c>projects.getOverview</c>. The resource
/// comes from the declaring endpoint class with its <c>Endpoints</c> suffix dropped and a leading
/// <c>Console</c> kept, so a console operation is visibly console-scoped — which is also how an SDK
/// generator decides what a client SDK may see. Uniqueness is asserted by
/// <c>OpenApiDocumentTests</c>, because two endpoints resolving to one id would silently collapse
/// two SDK methods into one.</para>
/// </summary>
public sealed class OpenApiOperationIds : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        // An explicit .WithName() already set this; never overwrite a deliberate choice.
        if (!string.IsNullOrEmpty(operation.OperationId))
            return Task.CompletedTask;

        var method = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<MethodInfo>()
            .FirstOrDefault();
        if (method is null)
            return Task.CompletedTask;

        // A lambda mapped inline compiles to a name like `<Map>b__0_0`, which is meaningless as a
        // public SDK method and changes whenever the surrounding code is reordered. Leave the id
        // unset so OpenApiDocumentTests names the endpoint and asks for an explicit .WithName(),
        // rather than deriving something that looks deliberate and is not.
        if (method.Name.AsSpan().ContainsAny('<', '>') || method.Name.Contains("b__", StringComparison.Ordinal))
            return Task.CompletedTask;

        operation.OperationId = Compute(method.DeclaringType?.Name, method.Name);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The id for a handler, from its declaring type and method name. Public so
    /// <c>OpenApiDocumentTests</c> can go from a handler it found in the source back to the
    /// operation that handler produces — the two must agree, and the only way to guarantee that is
    /// for both to call this rather than for the test to reimplement the rules.
    /// </summary>
    public static string Compute(string? declaringTypeName, string methodName)
    {
        var resource = ResourceName(declaringTypeName);
        var action = CamelCase(TrimAsyncSuffix(methodName));
        return resource is null ? action : $"{resource}.{action}";
    }

    /// <summary>
    /// Class names that must not be pluralised. Naive pluralisation is right for the collections —
    /// <c>projects</c>, <c>sites</c>, <c>functions</c> — and wrong for every mass noun, which is how
    /// the first pass produced <c>realtimes</c>, <c>storages</c> and <c>messagings</c>. These become
    /// public SDK namespaces, so they are worth spelling correctly.
    /// </summary>
    private static readonly HashSet<string> Uncountable = new(StringComparer.Ordinal)
    {
        "Realtime", "Storage", "ConsoleStorage", "Messaging", "ConsoleAuth", "ConsoleAuthAdmin", "Audit", "Vcs",
    };

    /// <summary>
    /// A class whose name does not describe the resource an SDK caller thinks in.
    /// <c>UsersServerEndpoints</c> is the server-side users API; <c>usersServers</c> describes the
    /// C# file rather than the thing being called.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["UsersServer"] = "users",
    };

    /// <summary>
    /// <c>ProjectEndpoints</c> → <c>projects</c>, <c>ConsoleDatabaseEndpoints</c> →
    /// <c>consoleDatabases</c>, <c>RealtimeEndpoints</c> → <c>realtime</c>. Null for a handler that
    /// is not on an <c>*Endpoints</c> class (a lambda mapped inline in <c>Program.cs</c>), which
    /// then gets a bare action name.
    /// </summary>
    private static string? ResourceName(string? declaringTypeName)
    {
        if (string.IsNullOrEmpty(declaringTypeName) || !declaringTypeName.EndsWith("Endpoints", StringComparison.Ordinal))
            return null;

        var stem = declaringTypeName[..^"Endpoints".Length];
        if (stem.Length == 0)
            return null;

        if (Renamed.TryGetValue(stem, out var renamed))
            return renamed;

        // Pluralise the trailing word so the namespace reads like a collection — "projects", not
        // "project" — matching how every SDK in this space names its services.
        if (!stem.EndsWith('s') && !Uncountable.Contains(stem))
            stem += 's';
        return CamelCase(stem);
    }

    /// <summary>Handlers are named for what they do, not for being async; the suffix is noise in a public SDK name.</summary>
    private static string TrimAsyncSuffix(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length
            ? name[..^"Async".Length]
            : name;

    private static string CamelCase(string name)
    {
        if (name.Length == 0)
            return name;
        var sb = new StringBuilder(name);
        sb[0] = char.ToLowerInvariant(sb[0]);
        return sb.ToString();
    }
}
