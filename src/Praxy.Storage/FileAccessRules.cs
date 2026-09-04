namespace Praxy.Storage;

/// <summary>What the bucket-level matrix alone has already decided about one action.</summary>
public enum FileAccessDecision
{
    /// <summary>The bucket (or a bypass) already grants it — no per-file lookup, and no per-file grant can take it back.</summary>
    Allow,

    /// <summary>Denied outright: the bucket doesn't grant it and per-file grants are switched off for this bucket.</summary>
    Deny,

    /// <summary>Undecided at bucket level — the caller reaches a file only through that file's own grants.</summary>
    PerFile,
}

/// <summary>
/// The escalation order for a file, copied from <c>QueryCompiler.PermissionPredicate</c> rather
/// than improvised, so storage and tables answer "who may do this" the same way:
///
/// <code>
/// bypassPermissions        -> allow
/// bucket grants the action -> allow
/// !bucket.file_security    -> deny
/// otherwise                -> the file's own grants decide
/// </code>
///
/// <para>
/// <b>Per-file grants are additive, never restrictive.</b> A bucket-level <c>read("any")</c> means
/// everyone reads every file, and nothing attached to a file claws that back — exactly how a
/// table-level grant overrides row security today. "Users can only read their own uploads" is
/// therefore configured by granting <i>no</i> bucket-level read at all, turning
/// <c>file_security</c> on, and attaching <c>read("user:&lt;id&gt;")</c> to each file. A design
/// where a bucket grant coexists with per-file restriction would be a second authorization model,
/// and this codebase has one on purpose (CLAUDE.md, docs/research/storage.md).
/// </para>
///
/// <para>
/// Split out as a pure function for one reason beyond tidiness: the branch order is the security
/// property, and this is the shape that can be unit-tested in all four directions without a
/// database.
/// </para>
/// </summary>
public static class FileAccessRules
{
    public static FileAccessDecision Resolve(
        bool bypassPermissions, IEnumerable<string> bucketGrantedRoles, IEnumerable<string> callerRoles,
        bool fileSecurity)
    {
        if (bypassPermissions)
            return FileAccessDecision.Allow;
        if (BucketAccess.IsPermitted(bucketGrantedRoles, callerRoles))
            return FileAccessDecision.Allow;
        return fileSecurity ? FileAccessDecision.PerFile : FileAccessDecision.Deny;
    }
}
