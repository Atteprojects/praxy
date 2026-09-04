using Praxy.Core.Errors;
using Praxy.Storage;

namespace Praxy.Tests.Unit;

/// <summary>
/// The file-level grant vocabulary — deliberately <c>RowPermissions</c>'s, one level down, so a
/// developer who has learned rows doesn't meet a second grammar for storage.
/// </summary>
public class FilePermissionsTests
{
    [Fact]
    public void Read_update_delete_are_accepted()
    {
        var parsed = FilePermissions.Parse(
            ["""read("any")""", """update("user:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4")""", """delete("users")"""]);
        Assert.Equal(3, parsed.Count);
    }

    /// <summary>There is no file to attach a grant to before the upload exists, so only the bucket matrix can gate one.</summary>
    [Fact]
    public void Create_is_rejected_because_a_file_cant_grant_its_own_creation()
    {
        var ex = Assert.Throws<PraxyException>(() => FilePermissions.Parse(["""create("users")"""]));
        Assert.Equal(ErrorTypes.GeneralArgumentInvalid, ex.Type);
    }

    /// <summary>Refused rather than expanded — its expansion would smuggle in exactly that dead create grant.</summary>
    [Fact]
    public void Write_is_rejected_even_though_it_would_expand_to_include_create()
    {
        Assert.Throws<PraxyException>(() => FilePermissions.Parse(["""write("users")"""]));
    }

    [Fact]
    public void Malformed_entries_are_a_clean_400_rather_than_an_unhandled_throw()
    {
        Assert.Throws<PraxyException>(() => FilePermissions.Parse(["not-a-permission"]));
        // A JSON null in the array reaches here as a null string; PermissionStrings.Parse turns that
        // into a FormatException rather than an ArgumentNullException, and this maps it to a 400.
        Assert.Throws<PraxyException>(() => FilePermissions.Parse([null!]));
    }

    /// <summary>Duplicates would otherwise violate the primary key on (file_id, action, role).</summary>
    [Fact]
    public void Duplicates_collapse_to_one_entry()
    {
        Assert.Single(FilePermissions.Parse(["""read("any")""", """read("any")"""]));
    }
}
