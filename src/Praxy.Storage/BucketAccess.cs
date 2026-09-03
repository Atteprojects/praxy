namespace Praxy.Storage;

/// <summary>
/// The one bucket authorization rule, named so there is exactly one place it can be got wrong:
/// the roles a bucket grants for an action, intersected with the caller's roles as resolved by
/// <c>IRoleResolver</c> — the same implementation the query compiler and realtime fan-out use.
/// Identical in shape to <c>CatalogEntry.TableRoles(action).Intersect(callerRoles)</c>, which is
/// the point: Storage introduces no second authorization concept (CLAUDE.md).
///
/// Deny-by-default falls out of it rather than being a separate rule — a bucket with no grants has
/// an empty left-hand side, so nothing intersects it.
/// </summary>
public static class BucketAccess
{
    public static bool IsPermitted(IEnumerable<string> grantedRoles, IEnumerable<string> callerRoles) =>
        grantedRoles.Intersect(callerRoles).Any();
}
