using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Tables;
using Praxy.Tables.Quotas;

namespace Praxy.Storage;

/// <summary>
/// Bucket CRUD and its permission matrix. Deliberately <see cref="TablesService"/>'s shape — same
/// key/name validation, same full-replace permission semantics over the same
/// <see cref="PermissionStrings.StorableActions"/>, same <c>force=true</c> convention on a
/// destructive delete — because a developer who has learned tables should not have to learn a
/// second model for storage (docs/research/storage.md).
/// </summary>
public sealed class BucketsService(PraxyDb db, QuotaService quotas, StorageOptions options)
{
    public async Task<Bucket> CreateAsync(
        string projectId, string key, string name, long? maxFileSizeBytes, string[]? allowedMimeTypes,
        bool? fileSecurity, string[]? inlineTypes, CancellationToken ct)
    {
        var fields = new Dictionary<string, string[]>();
        if (!Keys.IsValid(key))
            fields["key"] = ["Must start with a letter and contain only letters, digits and underscores (max 64 chars)."];
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];
        var normalizedMimeTypes = NormalizeMimeTypes(allowedMimeTypes, fields);
        var normalizedInlineTypes = NormalizeInlineTypes(inlineTypes, fields);
        if (maxFileSizeBytes is < 1)
            fields["maxFileSizeBytes"] = ["Must be at least 1 byte."];
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid bucket payload.", fields);

        await quotas.EnsureBucketQuotaAsync(projectId, ct);
        var budget = await quotas.GetStorageBudgetAsync(projectId, ct);

        var bucket = new Bucket
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Key = key,
            Name = name.Trim(),
            MaxFileSizeBytes = ClampFileSize(maxFileSizeBytes ?? options.DefaultBucketMaxFileSizeBytes, budget),
            AllowedMimeTypes = normalizedMimeTypes,
            FileSecurity = fileSecurity ?? false,
            InlineTypes = normalizedInlineTypes,
        };

        db.Buckets.Add(bucket);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new PraxyException(409, ErrorTypes.BucketAlreadyExists,
                $"A bucket with key '{key}' already exists in this project.");
        }
        return bucket;
    }

    public Task<List<Bucket>> ListAsync(string projectId, CancellationToken ct) =>
        db.Buckets.Where(b => b.ProjectId == projectId).OrderBy(b => b.CreatedAt).ToListAsync(ct);

    public async Task<Bucket> GetAsync(string projectId, Guid bucketId, CancellationToken ct) =>
        await db.Buckets.FirstOrDefaultAsync(b => b.Id == bucketId && b.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.BucketNotFound, "Bucket not found.");

    /// <summary>Wire-id entry point: a malformed id is a 404, not a 400 — an unparseable id names nothing.</summary>
    public Task<Bucket> GetAsync(string projectId, string bucketId, CancellationToken ct) =>
        Ids.TryParseWire(bucketId, out var id)
            ? GetAsync(projectId, id, ct)
            : throw PraxyException.NotFound(ErrorTypes.BucketNotFound, "Bucket not found.");

    public async Task<Bucket> UpdateAsync(
        Bucket bucket, string? name, bool? enabled, long? maxFileSizeBytes, string[]? allowedMimeTypes,
        bool clearAllowedMimeTypes, bool? fileSecurity, string[]? inlineTypes, CancellationToken ct)
    {
        // Validate everything before touching the entity: a rejected update must leave the tracked
        // bucket exactly as it was, not half-applied and relying on nobody calling SaveChanges after.
        var fields = new Dictionary<string, string[]>();
        if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128))
            fields["name"] = ["Must be between 1 and 128 characters."];
        var normalizedMimeTypes = allowedMimeTypes is not null
            ? NormalizeMimeTypes(allowedMimeTypes, fields)
            : null;
        // Unlike the mime-type allow-list, [] here is meaningful on its own (serve nothing inline,
        // the default) rather than a "clear it" signal, so it needs no companion flag.
        var normalizedInlineTypes = inlineTypes is not null
            ? NormalizeInlineTypes(inlineTypes, fields)
            : null;
        if (maxFileSizeBytes is < 1)
            fields["maxFileSizeBytes"] = ["Must be at least 1 byte."];
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid bucket payload.", fields);

        if (name is not null)
            bucket.Name = name.Trim();
        if (allowedMimeTypes is not null)
            bucket.AllowedMimeTypes = normalizedMimeTypes;
        else if (clearAllowedMimeTypes)
            bucket.AllowedMimeTypes = null;
        if (enabled is not null)
            bucket.Enabled = enabled.Value;
        if (fileSecurity is not null)
            bucket.FileSecurity = fileSecurity.Value;
        if (inlineTypes is not null)
            bucket.InlineTypes = normalizedInlineTypes;
        if (maxFileSizeBytes is { } max)
        {
            var budget = await quotas.GetStorageBudgetAsync(bucket.ProjectId, ct);
            bucket.MaxFileSizeBytes = ClampFileSize(max, budget);
        }

        bucket.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return bucket;
    }

    /// <summary>
    /// Always destructive — the FK cascade takes every file and, through those, every chunk row.
    /// Same <c>force=true</c> gate every other destructive delete in this engine uses, so the
    /// console can put a typed-name confirm in front of it.
    /// </summary>
    public async Task DeleteAsync(Bucket bucket, bool force, CancellationToken ct)
    {
        if (!force)
            throw new PraxyException(400, ErrorTypes.GeneralForceRequired,
                "Deleting a bucket is destructive — every file in it is deleted too. Pass force=true to confirm.");

        db.Buckets.Remove(bucket);
        await db.SaveChangesAsync(ct);
    }

    // ---- permissions ----------------------------------------------------------------------------

    public async Task<string[]> GetPermissionsAsync(Guid bucketId, CancellationToken ct)
    {
        var rows = await db.BucketPermissions
            .Where(p => p.BucketId == bucketId)
            .OrderBy(p => p.Action).ThenBy(p => p.Role)
            .ToListAsync(ct);
        return [.. rows.Select(p => PermissionStrings.Format(p.Action, p.Role))];
    }

    /// <summary>Full-replace semantics: the given set becomes the bucket's entire permission grant.</summary>
    public async Task<string[]> ReplacePermissionsAsync(Guid bucketId, string[] permissions, CancellationToken ct)
    {
        IReadOnlyList<(string Action, string Role)> expanded;
        try
        {
            expanded = PermissionStrings.ParseAndExpand(permissions);
        }
        catch (FormatException ex)
        {
            throw PraxyException.ArgumentInvalid(ex.Message,
                new Dictionary<string, string[]> { ["permissions"] = [ex.Message] });
        }

        await db.BucketPermissions.Where(p => p.BucketId == bucketId).ExecuteDeleteAsync(ct);
        db.BucketPermissions.AddRange(expanded.Select(e => new BucketPermission
        {
            BucketId = bucketId,
            Action = e.Action,
            Role = e.Role,
        }));
        await db.SaveChangesAsync(ct);
        return await GetPermissionsAsync(bucketId, ct);
    }

    /// <summary>Roles granted <paramref name="action"/> on this bucket (<c>write</c> already expanded at storage time).</summary>
    public async Task<string[]> RolesAsync(Guid bucketId, string action, CancellationToken ct) =>
        await db.BucketPermissions
            .Where(p => p.BucketId == bucketId && p.Action == action)
            .Select(p => p.Role)
            .ToArrayAsync(ct);

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// A bucket may narrow the instance/org ceiling but never widen it — otherwise a per-bucket
    /// setting would silently outrank the quota, and Kestrel's request-body limit (derived from
    /// the same quota) would reject the upload anyway at a size the bucket claimed to accept.
    /// Callers validate the lower bound alongside their other fields, so this only clamps.
    /// </summary>
    private static long ClampFileSize(long requested, StorageBudget budget) =>
        Math.Min(requested, budget.MaxFileSizeBytes);

    /// <summary>
    /// Inline types are validated against <see cref="InlineTypes.Safe"/> at write time so a bucket
    /// can never carry a grant that reads as protection-shaped configuration but is silently
    /// ignored when serving. The serve path intersects with the same set again — this check is the
    /// loud one, that one is the security control.
    /// </summary>
    private static string[]? NormalizeInlineTypes(string[]? types, Dictionary<string, string[]> fields)
    {
        if (types is null || types.Length == 0)
            return null;
        var normalized = types.Select(t => (t ?? "").Trim().ToLowerInvariant()).Distinct().ToArray();
        var invalid = normalized.Where(t => !InlineTypes.IsSafe(t)).ToArray();
        if (invalid.Length > 0)
        {
            fields["inlineTypes"] =
            [
                $"Cannot be served inline: {string.Join(", ", invalid)}. " +
                $"Allowed: {string.Join(", ", InlineTypes.Safe)}.",
            ];
            return null;
        }
        return normalized;
    }

    /// <summary>Empty means "any type", which is stored as null so there is one representation of "no restriction".</summary>
    private static string[]? NormalizeMimeTypes(string[]? patterns, Dictionary<string, string[]> fields)
    {
        if (patterns is null || patterns.Length == 0)
            return null;
        var invalid = patterns.Where(p => !MimeTypes.IsValidPattern(p)).ToArray();
        if (invalid.Length > 0)
        {
            fields["allowedMimeTypes"] =
                [$"Not a mime type or wildcard: {string.Join(", ", invalid.Select(i => i ?? "null"))}."];
            return null;
        }
        return [.. patterns.Select(p => p.Trim().ToLowerInvariant()).Distinct()];
    }
}
