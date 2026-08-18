using System.Formats.Tar;
using System.Text;
using Praxy.Functions;

namespace Praxy.Tests.Unit;

public class RuntimeTemplatesTests
{
    private static async Task<MemoryStream> MakeUserTarAsync(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        await using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                };
                await writer.WriteEntryAsync(entry);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static async Task<Dictionary<string, string>> ReadEntriesAsync(Stream tar)
    {
        var result = new Dictionary<string, string>();
        await using var reader = new TarReader(tar, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: true) is { } entry)
        {
            if (entry.DataStream is null)
                continue;
            using var ms = new MemoryStream();
            await entry.DataStream.CopyToAsync(ms);
            result[entry.Name] = Encoding.UTF8.GetString(ms.ToArray());
        }
        return result;
    }

    [Fact]
    public async Task Dart_context_carries_user_files_plus_generated_dockerfile_and_wrapper()
    {
        await using var userTar = await MakeUserTarAsync(("main.dart", "Future<Map<String,dynamic>> handler(Map<String,dynamic> c) async => {};"));
        await using var context = await RuntimeTemplates.BuildContextAsync(
            FunctionRuntimes.Dart, "main.dart", "dart:stable", userTar, CancellationToken.None);

        var entries = await ReadEntriesAsync(context);

        Assert.Contains("main.dart", entries.Keys);
        Assert.Contains("Dockerfile", entries.Keys);
        Assert.Contains("_praxy_server.dart", entries.Keys);
        Assert.Contains("FROM dart:stable", entries["Dockerfile"]);
        Assert.Contains("CMD [\"dart\", \"run\", \"_praxy_server.dart\"]", entries["Dockerfile"]);
        Assert.Contains("import './main.dart' as user_fn;", entries["_praxy_server.dart"]);
        Assert.Contains(RuntimeTemplates.SecretHeader, entries["_praxy_server.dart"]);
    }

    [Fact]
    public async Task Node_context_wrapper_reads_the_entrypoint_from_an_env_var_not_generated_source()
    {
        // A distinctive entrypoint name proves the wrapper source is a fixed template (the
        // entrypoint only ever appears in the Dockerfile's ENV line), unlike Dart's per-build codegen.
        await using var userTar = await MakeUserTarAsync(("custom_handler.js", "module.exports = async (c) => ({ statusCode: 200 });"));
        await using var context = await RuntimeTemplates.BuildContextAsync(
            FunctionRuntimes.Node, "custom_handler.js", "node:22-alpine", userTar, CancellationToken.None);

        var entries = await ReadEntriesAsync(context);

        Assert.Contains("custom_handler.js", entries.Keys);
        Assert.Contains("Dockerfile", entries.Keys);
        Assert.Contains("_praxy_server.js", entries.Keys);
        Assert.Contains("FROM node:22-alpine", entries["Dockerfile"]);
        Assert.Contains("ENV PRAXY_ENTRYPOINT=custom_handler.js", entries["Dockerfile"]);
        Assert.Contains("process.env.PRAXY_ENTRYPOINT", entries["_praxy_server.js"]);
        Assert.DoesNotContain("custom_handler.js", entries["_praxy_server.js"]);
    }

    [Fact]
    public async Task Health_and_runtime_port_constants_agree_with_the_generated_wrapper()
    {
        await using var userTar = await MakeUserTarAsync(("index.js", "module.exports = async () => ({});"));
        await using var context = await RuntimeTemplates.BuildContextAsync(
            FunctionRuntimes.Node, "index.js", "node:22-alpine", userTar, CancellationToken.None);
        var entries = await ReadEntriesAsync(context);

        Assert.Contains(RuntimeTemplates.HealthPath, entries["_praxy_server.js"]);
        Assert.Contains($"EXPOSE {RuntimeTemplates.RuntimePort}", entries["Dockerfile"]);
        Assert.Contains($"server.listen({RuntimeTemplates.RuntimePort}", entries["_praxy_server.js"]);
    }
}
