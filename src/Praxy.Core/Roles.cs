using System.Text.RegularExpressions;

namespace Praxy.Core;

/// <summary>
/// The role vocabulary, per architecture.md §4.3. One grammar, three consumers: <c>RoleResolver</c>
/// produces these strings, table/row permissions validate against them, and function
/// <c>execute</c> lists do too. It lives in Core rather than in Tables because Functions needs the
/// same check and must not fork it — the same "one implementation" discipline the role resolver
/// itself is held to (roadmap rule 2).
/// </summary>
public static partial class Roles
{
    public const string Any = "any";
    public const string Guests = "guests";
    public const string Users = "users";
    public const string UsersVerified = "users/verified";

    /// <summary>
    /// Validates a role string's shape. Lenient on the free-form parts (label names, team custom
    /// roles) — matching Phase 1's membership-roles precedent of validating shape, not a closed
    /// vocabulary.
    /// </summary>
    public static bool IsValid(string role) =>
        role is Any or Guests or Users or UsersVerified ||
        UserRoleRegex().IsMatch(role) ||
        TeamRoleRegex().IsMatch(role) ||
        MemberRoleRegex().IsMatch(role) ||
        LabelRoleRegex().IsMatch(role);

    [GeneratedRegex("^user:[0-9a-f]{32}(/verified)?$")]
    private static partial Regex UserRoleRegex();

    [GeneratedRegex("^team:[0-9a-f]{32}(/[a-zA-Z0-9_-]{1,64})?$")]
    private static partial Regex TeamRoleRegex();

    [GeneratedRegex("^member:[0-9a-f]{32}$")]
    private static partial Regex MemberRoleRegex();

    [GeneratedRegex("^label:[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex LabelRoleRegex();
}
