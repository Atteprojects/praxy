using Praxy.Functions;

namespace Praxy.Tests.Unit;

public class FunctionRuntimesTests
{
    [Theory]
    [InlineData(FunctionRuntimes.Dart, "main.dart", true)]
    [InlineData(FunctionRuntimes.Dart, "src/main.dart", true)]
    [InlineData(FunctionRuntimes.Dart, "main.js", false)]
    [InlineData(FunctionRuntimes.Node, "index.js", true)]
    [InlineData(FunctionRuntimes.Node, "src/index.cjs", true)]
    [InlineData(FunctionRuntimes.Node, "index.dart", false)]
    public void Entrypoint_extension_must_match_the_runtime(string runtime, string entrypoint, bool expected) =>
        Assert.Equal(expected, FunctionRuntimes.IsValidEntrypoint(runtime, entrypoint));

    [Theory]
    [InlineData("../main.dart")]
    [InlineData("a/../../main.dart")]
    [InlineData("main.dart'; DROP TABLE x;--")]
    [InlineData("")]
    [InlineData("   ")]
    public void Traversal_and_garbage_entrypoints_are_rejected(string entrypoint) =>
        Assert.False(FunctionRuntimes.IsValidEntrypoint(FunctionRuntimes.Dart, entrypoint));

    [Fact]
    public void Unknown_runtime_is_never_valid() =>
        Assert.False(FunctionRuntimes.IsValidEntrypoint("python", "main.py"));
}
