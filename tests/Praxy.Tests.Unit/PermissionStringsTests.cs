using Praxy.Tables;

namespace Praxy.Tests.Unit;

public class PermissionStringsTests
{
    [Fact]
    public void Format_and_parse_round_trip()
    {
        var formatted = PermissionStrings.Format("read", "any");
        Assert.Equal("read(\"any\")", formatted);
        var (action, role) = PermissionStrings.Parse(formatted);
        Assert.Equal("read", action);
        Assert.Equal("any", role);
    }

    [Theory]
    [InlineData("not-a-permission")]
    [InlineData("read(any)")]
    [InlineData("readany")]
    [InlineData("delete(\"any\"")]
    [InlineData("bogus(\"any\")")]
    public void Malformed_permission_strings_throw(string value) =>
        Assert.Throws<FormatException>(() => PermissionStrings.Parse(value));

    [Fact]
    public void Parse_rejects_an_invalid_role_shape()
    {
        Assert.Throws<FormatException>(() => PermissionStrings.Parse("read(\"not a real role!!\")"));
    }

    [Theory]
    [InlineData("any")]
    [InlineData("guests")]
    [InlineData("users")]
    [InlineData("users/verified")]
    [InlineData("user:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4")]
    [InlineData("user:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4/verified")]
    [InlineData("team:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4")]
    [InlineData("team:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4/owner")]
    [InlineData("member:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4")]
    [InlineData("label:vip")]
    public void Valid_roles_pass(string role) => Assert.True(PermissionStrings.IsValidRole(role));

    [Theory]
    [InlineData("")]
    [InlineData("everyone")]
    [InlineData("user:not-a-guid")]
    [InlineData("team:")]
    [InlineData("label:has spaces")]
    public void Invalid_roles_fail(string role) => Assert.False(PermissionStrings.IsValidRole(role));

    [Fact]
    public void Write_expands_to_create_update_delete_and_is_never_returned()
    {
        var expanded = PermissionStrings.ParseAndExpand(["write(\"users\")"]);
        Assert.Equal(3, expanded.Count);
        Assert.Contains(("create", "users"), expanded);
        Assert.Contains(("update", "users"), expanded);
        Assert.Contains(("delete", "users"), expanded);
        Assert.DoesNotContain(expanded, e => e.Action == "write");
    }

    [Fact]
    public void Expansion_deduplicates_overlapping_grants()
    {
        var expanded = PermissionStrings.ParseAndExpand(["write(\"users\")", "create(\"users\")"]);
        Assert.Equal(3, expanded.Count); // create(users) from both collapses to one
    }
}
