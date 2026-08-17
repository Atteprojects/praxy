using Praxy.Tables;

namespace Praxy.Tests.Unit;

public class KeysTests
{
    [Theory]
    [InlineData("title")]
    [InlineData("a")]
    [InlineData("snake_case_key")]
    [InlineData("Mixed_Case1")]
    public void Valid_keys_pass(string key) => Assert.True(Keys.IsValid(key));

    [Theory]
    [InlineData("")]
    [InlineData("1starts-with-digit")]
    [InlineData("has space")]
    [InlineData("has-hyphen")]
    public void Invalid_keys_fail(string key) => Assert.False(Keys.IsValid(key));

    /// <summary>
    /// A request body missing a "required" JSON string property binds it to null (System.Text.Json
    /// does not enforce C#'s non-nullable reference annotations at runtime) — found by Phase 9's
    /// security pass throwing <see cref="NullReferenceException"/>, an unhandled 500, for an
    /// ordinary incomplete database/table/column/index create request.
    /// </summary>
    [Fact]
    public void Null_key_is_invalid_not_a_crash() => Assert.False(Keys.IsValid(null));
}
