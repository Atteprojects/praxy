using Praxy.Core.Errors;
using Praxy.Tables;

namespace Praxy.Storage;

/// <summary>
/// Per-file permission grants: the same <c>action("role")</c> grammar as
/// <see cref="PermissionStrings"/> — no new vocabulary — restricted to the same three actions
/// <c>RowPermissions</c> allows on a row. A file can't grant its own creation (there is no file to
/// attach a grant to before the upload exists, so the bucket-level <c>create</c> matrix is the only
/// thing that can gate one), and <c>write</c> is refused rather than expanded, since its expansion
/// would smuggle exactly that meaningless <c>create</c> grant in.
/// </summary>
public static class FilePermissions
{
    private static readonly string[] AllowedActions =
        [PermissionStrings.Read, PermissionStrings.Update, PermissionStrings.Delete];

    public static IReadOnlyList<(string Action, string Role)> Parse(IEnumerable<string> permissions)
    {
        var parsed = new List<(string Action, string Role)>();
        foreach (var permission in permissions)
        {
            (string Action, string Role) entry;
            try
            {
                entry = PermissionStrings.Parse(permission);
            }
            catch (FormatException ex)
            {
                throw PraxyException.ArgumentInvalid(ex.Message,
                    new Dictionary<string, string[]> { ["permissions"] = [ex.Message] });
            }
            if (!AllowedActions.Contains(entry.Action))
                throw PraxyException.ArgumentInvalid(
                    $"'{permission}' — file permissions may only grant read/update/delete; create/write are bucket-level only.",
                    new Dictionary<string, string[]>
                    {
                        ["permissions"] = [$"'{permission}': create/write can't be granted on a single file."],
                    });
            parsed.Add(entry);
        }
        return [.. parsed.Distinct()];
    }
}
