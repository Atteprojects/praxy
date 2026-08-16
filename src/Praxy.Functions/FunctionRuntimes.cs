using System.Text.RegularExpressions;

namespace Praxy.Functions;

/// <summary>The two runtimes this phase ships (roadmap.md: "Dart runtime first — dogfoods the SDK — Node second").</summary>
public static partial class FunctionRuntimes
{
    public const string Dart = "dart";
    public const string Node = "node";
    public static readonly string[] All = [Dart, Node];

    public static bool IsValidEntrypoint(string runtime, string entrypoint)
    {
        if (string.IsNullOrWhiteSpace(entrypoint) || entrypoint.Length > 256)
            return false;
        // Relative, forward-slash, no traversal — this string is interpolated into generated
        // Dockerfile/wrapper source (RuntimeTemplates), so it must be inert as anything but a path.
        if (!EntrypointRegex().IsMatch(entrypoint) || entrypoint.Contains(".."))
            return false;
        return runtime switch
        {
            Dart => entrypoint.EndsWith(".dart", StringComparison.Ordinal),
            Node => entrypoint.EndsWith(".js", StringComparison.Ordinal) || entrypoint.EndsWith(".cjs", StringComparison.Ordinal),
            _ => false,
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_./-]+$")]
    private static partial Regex EntrypointRegex();
}
