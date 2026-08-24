using System.Formats.Tar;

namespace Praxy.Sites;

/// <summary>
/// A minimal, always-buildable Next.js app bundled with Praxy itself (<c>Templates/nextjs-starter/</c>,
/// copied next to the running assembly by <c>Praxy.Sites.csproj</c>'s <c>Content</c> item) — lets a
/// brand-new user deploy something real with one click instead of needing their own Next.js app ready
/// first, closer to Appwrite's template picker than the plain console-upload flow alone offers. Built
/// into a tar of the same shape a real upload would arrive in and handed to
/// <c>SitesService.CreateDeploymentAsync</c> unchanged — <c>SiteRuntimeTemplates.BuildContextAsync</c>
/// already re-emits every entry fresh regardless of source, so nothing here needs to worry about tar
/// format edge cases the way an upload from an arbitrary OS's tar tool might.
/// </summary>
public static class SiteStarterTemplate
{
    private static string RootPath => Path.Combine(AppContext.BaseDirectory, "Templates", "nextjs-starter");

    public static async Task<byte[]> BuildTarAsync(CancellationToken ct)
    {
        var root = RootPath;
        if (!Directory.Exists(root))
            throw new InvalidOperationException(
                $"Starter template not found at '{root}' — check Praxy.Sites.csproj's Templates Content item.");

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

    /// <summary>Skips anything that shouldn't ship in the deploy tar even if it ends up on disk next to the template (a stray local `node_modules`/`.next` from someone testing the template directly, editor dotfiles, etc.) — the template itself never checks any of these in.</summary>
    private static IEnumerable<string> EnumerateTemplateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "node_modules" or ".next" or ".git" || segment.StartsWith('.')));
}
