using Praxy.Core;

namespace Praxy.Tests.Unit;

public class IdsTests
{
    [Theory]
    [InlineData("my-project")]
    [InlineData("a")]
    [InlineData("0abc")]
    [InlineData("proj-123-x")]
    public void Valid_custom_ids_pass(string id) => Assert.True(Ids.IsValidCustomId(id));

    [Theory]
    [InlineData("")]
    [InlineData("-starts-with-hyphen")]
    [InlineData("Has-Uppercase")]
    [InlineData("under_score")]
    [InlineData("spaces here")]
    [InlineData("emoji-🙂")]
    [InlineData("this-id-is-thirty-seven-characters-xx")]
    public void Invalid_custom_ids_fail(string id) => Assert.False(Ids.IsValidCustomId(id));

    [Theory]
    [InlineData("console")]
    [InlineData("Console")]
    [InlineData("CONSOLE")]
    public void Console_is_reserved_case_insensitively(string id) => Assert.True(Ids.IsReservedProjectId(id));

    [Fact]
    public void Generated_resource_ids_are_valid_custom_ids()
    {
        for (var i = 0; i < 100; i++)
            Assert.True(Ids.IsValidCustomId(Ids.NewResourceId()));
    }

    [Fact]
    public void Generated_resource_ids_are_time_ordered()
    {
        var a = Ids.NewResourceId();
        Thread.Sleep(2);
        var b = Ids.NewResourceId();
        Assert.True(string.CompareOrdinal(a, b) < 0);
    }

    /// <summary>
    /// A request body missing a "required" JSON string property binds it to null (System.Text.Json
    /// does not enforce C#'s non-nullable reference annotations at runtime) — found by Phase 9's
    /// security pass throwing <see cref="ArgumentNullException"/>, an unhandled 500, for an ordinary
    /// incomplete <c>POST /v1/console/projects</c> body.
    /// </summary>
    [Fact]
    public void Null_id_is_invalid_not_a_crash() => Assert.False(Ids.IsValidCustomId(null));
}
