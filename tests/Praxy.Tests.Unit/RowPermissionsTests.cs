using Praxy.Core.Errors;
using Praxy.Tables;

namespace Praxy.Tests.Unit;

public class RowPermissionsTests
{
    [Fact]
    public void Read_update_delete_are_accepted()
    {
        var parsed = RowPermissions.Parse(["read(\"any\")", "update(\"user:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4\")", "delete(\"users\")"]);
        Assert.Equal(3, parsed.Count);
    }

    [Fact]
    public void Create_is_rejected_because_rows_cant_grant_their_own_creation()
    {
        var ex = Assert.Throws<PraxyException>(() => RowPermissions.Parse(["create(\"users\")"]));
        Assert.Equal(ErrorTypes.GeneralArgumentInvalid, ex.Type);
    }

    [Fact]
    public void Write_is_rejected_even_though_it_would_expand_to_include_create()
    {
        Assert.Throws<PraxyException>(() => RowPermissions.Parse(["write(\"users\")"]));
    }

    [Fact]
    public void Malformed_permission_strings_are_rejected()
    {
        Assert.Throws<PraxyException>(() => RowPermissions.Parse(["not-a-permission"]));
    }

    [Fact]
    public void Duplicates_collapse_to_one_entry()
    {
        var parsed = RowPermissions.Parse(["read(\"any\")", "read(\"any\")"]);
        Assert.Single(parsed);
    }
}
