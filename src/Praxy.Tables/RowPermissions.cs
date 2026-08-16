using Praxy.Core.Errors;

namespace Praxy.Tables;

/// <summary>
/// Row-level permission grants — the same <c>action("role")</c> grammar as
/// <see cref="PermissionStrings"/>, but restricted to <c>read</c>/<c>update</c>/<c>delete</c>. A row
/// can't grant its own creation (console-design.md: "no Create column at row level" — there's no
/// row to attach a grant to before it exists), and <c>write</c> is never accepted here since it
/// would silently smuggle a create grant in through its expansion.
/// </summary>
public static class RowPermissions
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
                    $"'{permission}' — row permissions may only grant read/update/delete; create/write are table-level only.",
                    new Dictionary<string, string[]>
                    {
                        ["permissions"] = [$"'{permission}': create/write can't be granted at the row level."],
                    });
            parsed.Add(entry);
        }
        return [.. parsed.Distinct()];
    }
}
