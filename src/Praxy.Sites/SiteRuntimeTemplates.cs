using System.Formats.Tar;
using System.Text;
using System.Text.RegularExpressions;

namespace Praxy.Sites;

/// <summary>
/// Builds the Docker build context for a Next.js deployment: the uploaded user tar plus a generated
/// multi-stage Dockerfile requiring <c>output: "standalone"</c> (praxy-sites.md's Data model
/// section). Mirrors <c>Praxy.Functions.RuntimeTemplates</c>'s shape closely — same
/// <c>System.Formats.Tar</c> re-emit step for the macOS <c>bsdtar</c> PAX-attribute bug — but the
/// generated Dockerfile is Next.js-specific, not per-runtime.
/// </summary>
public static partial class SiteRuntimeTemplates
{
    public const int RuntimePort = 3000;

    public static async Task<MemoryStream> BuildContextAsync(
        string rootDirectory, string baseImage, IReadOnlyCollection<string> envVarKeys, Stream userTar,
        CancellationToken ct)
    {
        var output = new MemoryStream();
        await using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            await using (var reader = new TarReader(userTar, leaveOpen: true))
            {
                // Re-emit a fresh minimal entry per file rather than forwarding the original
                // TarEntry object — see RuntimeTemplates.BuildContextAsync's remarks: macOS's
                // bsdtar embeds a PAX extended attribute ("com.apple.provenance") that the Linux
                // side of the Docker daemon's context extraction rejects outright, turning a
                // perfectly valid upload into a build failure before the Dockerfile ever runs.
                while (await reader.GetNextEntryAsync(copyData: true, ct) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.Directory))
                        continue;
                    var clean = new PaxTarEntry(entry.EntryType, entry.Name) { Mode = entry.Mode };
                    if (entry.DataStream is not null)
                        clean.DataStream = entry.DataStream;
                    await writer.WriteEntryAsync(clean, ct);
                }
            }

            await WriteTextEntryAsync(writer, "Dockerfile", Dockerfile(rootDirectory, baseImage, envVarKeys), ct);
        }
        output.Position = 0;
        return output;
    }

    private static async Task WriteTextEntryAsync(TarWriter writer, string name, string content, CancellationToken ct)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        };
        await writer.WriteEntryAsync(entry, ct);
    }

    /// <summary>
    /// Multi-stage build: install + build in one stage, copy only the standalone output into a lean
    /// runner stage (praxy-sites.md's exact template, extended with the missing-standalone-output
    /// check and env-var build args). <paramref name="rootDirectory"/> support is deliberately
    /// simple — "treat this subdirectory as the app's own root" for a single-package layout, not a
    /// full npm/yarn/pnpm workspace monorepo, where Next's standalone output nests based on
    /// workspace-root detection in a way this template doesn't attempt to follow (see the Phase 1
    /// report's deviations section).
    /// </summary>
    private static string Dockerfile(string rootDirectory, string baseImage, IReadOnlyCollection<string> envVarKeys)
    {
        var appDir = string.IsNullOrEmpty(rootDirectory) ? "/app" : $"/app/{rootDirectory}";
        var buildArgs = new StringBuilder();
        foreach (var key in envVarKeys)
        {
            // Keys are validated at write time (SitesService.SetEnvVarAsync, same charset as
            // FunctionsService's) to [A-Za-z0-9_]+, so interpolating them directly into Dockerfile
            // text carries no injection risk — only the values travel through Docker's own
            // --build-arg channel (ImageBuildParameters.BuildArgs), never through this text.
            buildArgs.Append($"ARG {key}\nENV {key}=${key}\n");
        }

        return $"""
            FROM {baseImage} AS builder
            WORKDIR /app
            COPY . .
            WORKDIR {appDir}
            {buildArgs}RUN npm install
            RUN npm run build
            RUN mkdir -p public .next/static
            RUN test -d .next/standalone || (echo "ERROR: .next/standalone was not produced by 'npm run build'. Add output: 'standalone' to next.config.js (or .mjs/.ts) and redeploy." 1>&2 && exit 1)

            FROM {baseImage} AS runner
            WORKDIR /app
            ENV NODE_ENV=production
            ENV PORT={RuntimePort}
            ENV HOSTNAME=0.0.0.0
            COPY --from=builder {appDir}/.next/standalone ./
            COPY --from=builder {appDir}/.next/static ./.next/static
            COPY --from=builder {appDir}/public ./public
            EXPOSE {RuntimePort}
            CMD ["node", "server.js"]
            """;
    }

    /// <summary>Empty (tar root) or a relative path with no traversal — interpolated into the generated Dockerfile, so it must be inert as anything but a path.</summary>
    public static bool IsValidRootDirectory(string rootDirectory) =>
        rootDirectory.Length == 0 ||
        (rootDirectory.Length <= 256 && RootDirectoryRegex().IsMatch(rootDirectory) && !rootDirectory.Contains(".."));

    [GeneratedRegex(@"^[A-Za-z0-9_./-]+$")]
    private static partial Regex RootDirectoryRegex();
}
