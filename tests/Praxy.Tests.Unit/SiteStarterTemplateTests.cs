using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Praxy.Sites;

namespace Praxy.Tests.Unit;

public class SiteStarterTemplateTests
{
    [Fact]
    public async Task Builds_a_tar_containing_exactly_the_expected_files()
    {
        var tar = await SiteStarterTemplate.BuildTarAsync(CancellationToken.None);

        var names = await ReadEntryNamesAsync(tar);

        Assert.Contains("package.json", names);
        Assert.Contains("next.config.js", names);
        Assert.Contains("app/layout.js", names);
        Assert.Contains("app/page.js", names);
        Assert.Equal(4, names.Count);
    }

    [Fact]
    public async Task Excludes_node_modules_next_and_dotfiles_even_if_present_on_disk()
    {
        var tar = await SiteStarterTemplate.BuildTarAsync(CancellationToken.None);
        var names = await ReadEntryNamesAsync(tar);

        Assert.DoesNotContain(names, n => n.Split('/').Any(segment =>
            segment is "node_modules" or ".next" or ".git" || segment.StartsWith('.')));
    }

    [Fact]
    public async Task Next_config_sets_the_standalone_output_required_by_the_build_pipeline()
    {
        var content = await ReadEntryTextAsync(await SiteStarterTemplate.BuildTarAsync(CancellationToken.None), "next.config.js");
        Assert.Contains("output", content);
        Assert.Contains("standalone", content);
    }

    [Fact]
    public async Task Package_json_is_valid_and_declares_next_react_and_a_build_script()
    {
        var content = await ReadEntryTextAsync(await SiteStarterTemplate.BuildTarAsync(CancellationToken.None), "package.json");
        using var doc = JsonDocument.Parse(content);
        var deps = doc.RootElement.GetProperty("dependencies");
        Assert.True(deps.TryGetProperty("next", out _));
        Assert.True(deps.TryGetProperty("react", out _));
        Assert.True(deps.TryGetProperty("react-dom", out _));
        Assert.True(doc.RootElement.GetProperty("scripts").TryGetProperty("build", out _));
    }

    private static async Task<List<string>> ReadEntryNamesAsync(byte[] tar)
    {
        var names = new List<string>();
        using var stream = new MemoryStream(tar);
        using var reader = new TarReader(stream);
        while (await reader.GetNextEntryAsync() is { } entry)
            names.Add(entry.Name);
        return names;
    }

    private static async Task<string> ReadEntryTextAsync(byte[] tar, string name)
    {
        using var stream = new MemoryStream(tar);
        using var reader = new TarReader(stream);
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            if (entry.Name != name || entry.DataStream is null)
                continue;
            using var buffer = new MemoryStream();
            await entry.DataStream.CopyToAsync(buffer);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        throw new InvalidOperationException($"Entry '{name}' not found in tar.");
    }
}
