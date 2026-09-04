using Praxy.Storage;

namespace Praxy.Tests.Unit;

/// <summary>
/// The escalation order, in all four directions. This is the phase's security property: the same
/// branch sequence <c>QueryCompiler.PermissionPredicate</c> uses for row security, and the two
/// opposite ways to break it — defaulting to allow (a hole) or keeping the old unconditional throw
/// (a silently dead feature) — are both a single wrong branch away, which is why it is a pure
/// function with its own tests rather than an <c>if</c> buried in <c>FilesService</c>.
/// </summary>
public class FileAccessRulesTests
{
    private static FileAccessDecision Resolve(
        bool bypass = false, string[]? bucketGrants = null, string[]? caller = null, bool fileSecurity = false) =>
        FileAccessRules.Resolve(bypass, bucketGrants ?? [], caller ?? ["any", "users", "user:me"], fileSecurity);

    [Fact]
    public void A_bypassing_caller_is_allowed_before_anything_is_looked_at()
    {
        Assert.Equal(FileAccessDecision.Allow, Resolve(bypass: true));
        // Even with file_security on and no grants anywhere — the console and a bypassing key are
        // above this model entirely.
        Assert.Equal(FileAccessDecision.Allow, Resolve(bypass: true, fileSecurity: true));
    }

    [Fact]
    public void A_bucket_grant_allows_outright_and_per_file_grants_never_narrow_it()
    {
        // The additive property, stated as a test: with the bucket granting the action, the answer
        // is Allow whether or not file_security is on — so there is no code path in which a
        // per-file row is consulted to *remove* access. "Only your own files" is configured by
        // granting nothing at bucket level, never by restricting a grant that exists.
        Assert.Equal(FileAccessDecision.Allow, Resolve(bucketGrants: ["any"]));
        Assert.Equal(FileAccessDecision.Allow, Resolve(bucketGrants: ["any"], fileSecurity: true));
        Assert.Equal(FileAccessDecision.Allow, Resolve(bucketGrants: ["users"], fileSecurity: true));
    }

    [Fact]
    public void No_bucket_grant_and_no_file_security_is_a_flat_deny()
    {
        Assert.Equal(FileAccessDecision.Deny, Resolve());
        // A grant that doesn't match the caller is no grant at all.
        Assert.Equal(FileAccessDecision.Deny, Resolve(bucketGrants: ["user:someone-else"]));
    }

    [Fact]
    public void No_bucket_grant_with_file_security_defers_to_the_files_own_grants()
    {
        Assert.Equal(FileAccessDecision.PerFile, Resolve(fileSecurity: true));
        Assert.Equal(FileAccessDecision.PerFile, Resolve(bucketGrants: ["team:other"], fileSecurity: true));
    }

    /// <summary>Deny-by-default survives the new level: an empty bucket with no file grants reaches nothing.</summary>
    [Fact]
    public void A_caller_with_no_roles_at_all_is_never_allowed()
    {
        Assert.Equal(FileAccessDecision.Deny, Resolve(bucketGrants: ["any"], caller: []));
        Assert.Equal(FileAccessDecision.PerFile, Resolve(bucketGrants: ["any"], caller: [], fileSecurity: true));
    }
}
