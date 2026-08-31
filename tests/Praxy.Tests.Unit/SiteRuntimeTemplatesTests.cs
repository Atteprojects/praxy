using System.Formats.Tar;
using System.Text;
using Praxy.Sites;

namespace Praxy.Tests.Unit;

public class SiteRuntimeTemplatesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("app")]
    [InlineData("apps/web")]
    [InlineData("apps/web-2")]
    public void Valid_root_directories_are_accepted(string rootDirectory) =>
        Assert.True(SiteRuntimeTemplates.IsValidRootDirectory(rootDirectory));

    [Theory]
    [InlineData("../app")]
    [InlineData("apps/../../etc")]
    [InlineData("app'; DROP TABLE x;--")]
    [InlineData("   ")]
    public void Traversal_and_garbage_root_directories_are_rejected(string rootDirectory) =>
        Assert.False(SiteRuntimeTemplates.IsValidRootDirectory(rootDirectory));

    private static byte[] BuildUserTar(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                // Simulates macOS bsdtar's real-world quirk: a PAX extended attribute
                // ("com.apple.provenance") on an otherwise ordinary file entry, the exact shape
                // dotnet-stack.md documents as breaking a raw tar pass-through on Linux.
                var entry = new PaxTarEntry(
                    TarEntryType.RegularFile, name, [new KeyValuePair<string, string>("com.apple.provenance", "0")])
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                };
                writer.WriteEntry(entry);
            }
        }
        return stream.ToArray();
    }

    private static async Task<Dictionary<string, string>> ReadTarAsync(Stream tar)
    {
        var files = new Dictionary<string, string>();
        await using var reader = new TarReader(tar);
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                continue;
            using var ms = new MemoryStream();
            await entry.DataStream.CopyToAsync(ms);
            files[entry.Name] = Encoding.UTF8.GetString(ms.ToArray());
        }
        return files;
    }

    [Fact]
    public async Task Dockerfile_requires_standalone_output_and_fails_fast_with_an_actionable_message()
    {
        await using var userTar = new MemoryStream(BuildUserTar(("package.json", "{}")));
        await using var context = await SiteRuntimeTemplates.BuildContextAsync("", "node:22-alpine", [], userTar, CancellationToken.None);

        var files = await ReadTarAsync(context);
        Assert.True(files.TryGetValue("Dockerfile", out var dockerfile));

        Assert.Contains("FROM node:22-alpine AS builder", dockerfile);
        Assert.Contains("FROM node:22-alpine AS runner", dockerfile);
        Assert.Contains("RUN npm install", dockerfile);
        Assert.Contains("RUN npm run build", dockerfile);
        // The missing-standalone-output check must run as its own RUN step, before the runner
        // stage's COPY --from=builder ever executes — an opaque Docker COPY error is exactly what
        // this is meant to preempt.
        Assert.Contains(".next/standalone", dockerfile);
        Assert.Contains("output: 'standalone'", dockerfile);
        var standaloneCheckIndex = dockerfile.IndexOf("test -d .next/standalone", StringComparison.Ordinal);
        var runnerStageIndex = dockerfile.IndexOf("AS runner", StringComparison.Ordinal);
        Assert.True(standaloneCheckIndex > 0 && standaloneCheckIndex < runnerStageIndex,
            "The missing-standalone check must appear in the builder stage, before the runner stage.");
        Assert.Contains("COPY --from=builder /app/.next/standalone ./", dockerfile);
        Assert.Contains("EXPOSE " + SiteRuntimeTemplates.RuntimePort, dockerfile);
        Assert.Contains("""CMD ["node", "server.js"]""", dockerfile);
    }

    /// <summary>
    /// The caching fix: <c>package.json</c>/<c>package-lock.json</c> must be copied and installed
    /// *before* the full source copy, so Docker's local layer cache can skip <c>npm install</c> when
    /// only app code (not dependencies) changed between two builds of the same site.
    /// </summary>
    [Fact]
    public async Task Dependency_install_layer_is_ordered_before_the_full_source_copy_for_docker_layer_caching()
    {
        await using var userTar = new MemoryStream(BuildUserTar(("package.json", "{}"), ("package-lock.json", "{}")));
        await using var context = await SiteRuntimeTemplates.BuildContextAsync("", "node:22-alpine", [], userTar, CancellationToken.None);
        var files = await ReadTarAsync(context);
        var dockerfile = files["Dockerfile"];

        Assert.Contains("COPY package.json package-lock.json* ./", dockerfile);

        var pkgCopyIndex = dockerfile.IndexOf("COPY package.json package-lock.json* ./", StringComparison.Ordinal);
        var installIndex = dockerfile.IndexOf("RUN npm install", StringComparison.Ordinal);
        var fullCopyIndex = dockerfile.IndexOf("COPY . .", StringComparison.Ordinal);
        var buildIndex = dockerfile.IndexOf("RUN npm run build", StringComparison.Ordinal);

        Assert.True(pkgCopyIndex >= 0 && installIndex > pkgCopyIndex && fullCopyIndex > installIndex && buildIndex > fullCopyIndex,
            "Expected order: COPY package.json/lock -> RUN npm install -> COPY . . -> RUN npm run build.");
    }

    [Fact]
    public async Task Root_directory_changes_the_builder_workdir_and_copy_source_paths()
    {
        await using var userTar = new MemoryStream(BuildUserTar(("apps/web/package.json", "{}")));
        await using var context = await SiteRuntimeTemplates.BuildContextAsync(
            "apps/web", "node:22-alpine", [], userTar, CancellationToken.None);
        var files = await ReadTarAsync(context);
        var dockerfile = files["Dockerfile"];

        Assert.Contains("WORKDIR /app/apps/web", dockerfile);
        Assert.Contains("COPY apps/web/package.json apps/web/package-lock.json* ./", dockerfile);
        Assert.Contains("COPY --from=builder /app/apps/web/.next/standalone ./", dockerfile);
        Assert.Contains("COPY --from=builder /app/apps/web/public ./public", dockerfile);
    }

    [Fact]
    public async Task Env_var_keys_become_build_args_promoted_to_env_before_the_build_runs()
    {
        await using var userTar = new MemoryStream(BuildUserTar(("package.json", "{}")));
        await using var context = await SiteRuntimeTemplates.BuildContextAsync(
            "", "node:22-alpine", ["NEXT_PUBLIC_API_URL", "DATABASE_URL"], userTar, CancellationToken.None);
        var files = await ReadTarAsync(context);
        var dockerfile = files["Dockerfile"];

        Assert.Contains("ARG NEXT_PUBLIC_API_URL", dockerfile);
        Assert.Contains("ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL", dockerfile);
        Assert.Contains("ARG DATABASE_URL", dockerfile);
        Assert.Contains("ENV DATABASE_URL=$DATABASE_URL", dockerfile);
        // Build args must be declared (and thus available to `RUN npm run build`) before that step,
        // not after — Next.js inlines NEXT_PUBLIC_* only if the value is present at build time.
        Assert.True(dockerfile.IndexOf("ARG NEXT_PUBLIC_API_URL", StringComparison.Ordinal)
            < dockerfile.IndexOf("RUN npm run build", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_tar_entries_are_re_emitted_without_the_macOS_bsdtar_PAX_attribute()
    {
        await using var userTar = new MemoryStream(BuildUserTar(("index.js", "console.log('hi')"), ("package.json", "{}")));
        await using var context = await SiteRuntimeTemplates.BuildContextAsync("", "node:22-alpine", [], userTar, CancellationToken.None);

        await using var reader = new TarReader(context);
        var seenUserFiles = 0;
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            if (entry.Name is "index.js" or "package.json")
            {
                seenUserFiles++;
                var pax = Assert.IsType<PaxTarEntry>(entry);
                Assert.False(pax.ExtendedAttributes.ContainsKey("com.apple.provenance"),
                    $"{entry.Name} still carries the macOS bsdtar PAX attribute that breaks the Docker build on Linux.");
            }
        }
        Assert.Equal(2, seenUserFiles);
    }
}
