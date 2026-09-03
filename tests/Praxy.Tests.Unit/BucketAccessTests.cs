using Microsoft.EntityFrameworkCore;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Storage;

namespace Praxy.Tests.Unit;

/// <summary>
/// Bucket permission resolution against the *shared* role resolver — the same
/// <see cref="RoleResolver"/> the query compiler and realtime fan-out consume, not a storage-local
/// copy. Guest and API-key principals resolve without touching the database, so they are exercised
/// here directly; the user/team cases go through a real Postgres in
/// <c>StorageEngineTests</c>.
/// </summary>
public class BucketAccessTests
{
    /// <summary>The resolver only reads the context for user/team roles, which these cases never take.</summary>
    private static RoleResolver Resolver() =>
        new(new PraxyDb(new DbContextOptionsBuilder<PraxyDb>().Options));

    private static async Task<string[]> RolesFor(RequestPrincipal principal) =>
        await Resolver().ResolveAsync(principal);

    [Fact]
    public async Task A_bucket_with_no_grants_denies_everyone()
    {
        var guest = await RolesFor(new RequestPrincipal.Guest());
        Assert.False(BucketAccess.IsPermitted([], guest));
    }

    [Fact]
    public async Task A_guest_is_permitted_by_a_grant_to_any_or_guests()
    {
        var guest = await RolesFor(new RequestPrincipal.Guest());

        Assert.True(BucketAccess.IsPermitted(["any"], guest));
        Assert.True(BucketAccess.IsPermitted(["guests"], guest));
        Assert.False(BucketAccess.IsPermitted(["users"], guest));
    }

    [Fact]
    public async Task An_api_key_satisfies_any_but_not_a_user_scoped_grant()
    {
        var key = await RolesFor(new RequestPrincipal.Key(new ApiKey
        {
            Id = Ids.NewUuid(),
            ProjectId = "p1",
            Name = "k",
            SecretHash = "",
            Scopes = [],
        }));

        Assert.True(BucketAccess.IsPermitted(["any"], key));
        Assert.False(BucketAccess.IsPermitted(["users"], key));
        Assert.False(BucketAccess.IsPermitted(["guests"], key));
    }

    [Fact]
    public void A_grant_for_one_action_says_nothing_about_another()
    {
        // The caller passes only the roles granted for the action it is attempting, so a bucket
        // that grants read("any") and nothing else denies a create with an empty granted list.
        Assert.True(BucketAccess.IsPermitted(["any"], ["any", "guests"]));
        Assert.False(BucketAccess.IsPermitted([], ["any", "guests"]));
    }

    [Fact]
    public void Specific_user_and_team_roles_match_by_exact_string()
    {
        string[] caller = ["any", "users", "user:abc", "team:t1", "team:t1/admin"];

        Assert.True(BucketAccess.IsPermitted(["user:abc"], caller));
        Assert.True(BucketAccess.IsPermitted(["team:t1/admin"], caller));
        Assert.False(BucketAccess.IsPermitted(["user:def"], caller));
        Assert.False(BucketAccess.IsPermitted(["team:t1/member"], caller));
    }
}
