using System.Formats.Tar;
using Praxy.Core.Errors;

namespace Praxy.Functions;

/// <summary>One bundled starter's catalog entry — key, display metadata, and the runtime/entrypoint/schedule its tar is built for.</summary>
public sealed record FunctionTemplateInfo(
    string Key, string Name, string Description, string Runtime, string Entrypoint, string? DefaultSchedule);

/// <summary>
/// Bundled, always-buildable function starters (<c>Templates/&lt;key&gt;/</c>, copied next to the
/// running assembly by <c>Praxy.Functions.csproj</c>'s <c>Content</c> item) — the Functions
/// equivalent of <c>Praxy.Sites.SiteStarterTemplate</c>, restructured for several templates instead
/// of one. Each demonstrates a real Praxy primitive (a standing API-key credential, a cron schedule,
/// a shared-secret receiver writing to Tables) rather than a bare "hello world". Tar-building mirrors
/// <c>SiteStarterTemplate.BuildTarAsync</c> exactly: re-emit every file fresh into a tar of the same
/// shape a real upload would arrive in, then hand it to <c>FunctionsService.CreateDeploymentAsync</c>
/// unchanged — nothing here needs to worry about tar format edge cases a real upload tool might hit.
/// </summary>
public static class FunctionTemplates
{
    public static readonly IReadOnlyList<FunctionTemplateInfo> All =
    [
        new(
            "http-echo", "HTTP echo",
            "Echoes back the request method, path and body — the true minimal starter, nothing to configure before deploying.",
            FunctionRuntimes.Dart, "main.dart", null),
        new(
            "scheduled-cleanup", "Scheduled cleanup",
            "Runs on a daily cron schedule and deletes rows older than a configurable age from a Table you choose. Needs a standing API key — see the template's own comments.",
            FunctionRuntimes.Node, "index.js", "0 3 * * *"),
        new(
            "webhook-receiver", "Webhook receiver",
            "Validates a shared secret carried in the request body and writes each event to a Table — a starting point for wiring up an external integration.",
            FunctionRuntimes.Node, "index.js", null),
    ];

    public static FunctionTemplateInfo Find(string key) =>
        All.FirstOrDefault(t => t.Key == key)
        ?? throw PraxyException.NotFound(ErrorTypes.FunctionTemplateNotFound, $"No function template '{key}'.");

    public static async Task<byte[]> BuildTarAsync(string key, CancellationToken ct)
    {
        var template = Find(key);
        var root = RootPath(template.Key);
        if (!Directory.Exists(root))
            throw new InvalidOperationException(
                $"Function template not found at '{root}' — check Praxy.Functions.csproj's Templates Content item.");

        using var output = new MemoryStream();
        await using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var path in EnumerateTemplateFiles(root))
            {
                var relativeName = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                var entry = new PaxTarEntry(TarEntryType.RegularFile, relativeName)
                {
                    DataStream = new MemoryStream(await File.ReadAllBytesAsync(path, ct)),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                await writer.WriteEntryAsync(entry, ct);
            }
        }
        return output.ToArray();
    }

    private static string RootPath(string key) => Path.Combine(AppContext.BaseDirectory, "Templates", key);

    /// <summary>Skips anything that shouldn't ship in the deploy tar even if it ends up on disk next to a template (editor dotfiles, a stray local `.git`) — mirrors <c>SiteStarterTemplate</c>'s own filter.</summary>
    private static IEnumerable<string> EnumerateTemplateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "node_modules" or ".git" || segment.StartsWith('.')));
}
