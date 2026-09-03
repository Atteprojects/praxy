using System.Buffers;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Tables;
using Praxy.Tables.Quotas;

namespace Praxy.Storage;

/// <summary>
/// File CRUD over a bucket. Access is the same intersect-the-roles check tables already perform —
/// <c>bucket.Roles(action) ∩ callerRoles</c>, against roles from the one
/// <c>IRoleResolver</c> — so Storage introduces no second authorization concept (CLAUDE.md's
/// cross-phase rule). Writes go through the outbox like every other write since Phase 3.
/// </summary>
public sealed class FilesService(
    PraxyDb db, IFileStore store, BucketsService buckets, QuotaService quotas,
    StorageOptions options, IEventBus events)
{
    /// <summary>Copy buffer between the request body and the chunk writer — unrelated to the chunk size, which is how much is buffered before a row is written.</summary>
    private const int CopyBufferBytes = 81_920;

    // ---- upload -----------------------------------------------------------------------------

    /// <summary>
    /// Streams <paramref name="body"/> straight into chunk rows inside one transaction: either the
    /// whole file and its metadata commit, or neither does, so a failed or over-quota upload can
    /// never leave an orphaned half-file. Nothing here materializes the file — peak memory is one
    /// chunk plus one copy buffer, whatever the file's size.
    /// </summary>
    public async Task<StoredFile> UploadAsync(
        Bucket bucket, string? name, string? contentType, long? contentLength, Stream body,
        string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        await RequireAsync(bucket, PermissionStrings.Create, callerRoles, bypassPermissions, ct);
        RequireEnabled(bucket);

        var fileName = ValidateName(name);
        var mimeType = MimeTypes.Normalize(contentType);
        if (!MimeTypes.IsAllowed(bucket.AllowedMimeTypes, mimeType))
            throw new PraxyException(400, ErrorTypes.FileTypeNotAllowed,
                $"This bucket does not accept '{mimeType}'. Allowed: {string.Join(", ", bucket.AllowedMimeTypes!)}.");

        var budget = await quotas.GetStorageBudgetAsync(bucket.ProjectId, ct);
        // A bucket may narrow the resolved quota, never widen it (BucketsService clamps on write —
        // this re-derives it because an org's limit can be lowered after the bucket was created).
        var maxFileSize = Math.Min(bucket.MaxFileSizeBytes, budget.MaxFileSizeBytes);

        // Cheap up-front rejection when the client declared a length. The streaming checks below
        // are the real enforcement — a chunked upload declares nothing.
        if (contentLength is { } declared)
        {
            if (declared > maxFileSize) throw TooLarge(maxFileSize);
            if (declared > budget.Remaining) throw OverStorageQuota(budget);
        }

        var file = new StoredFile
        {
            Id = Ids.NewUuid(),
            BucketId = bucket.Id,
            Name = fileName,
            MimeType = mimeType,
            SizeBytes = 0,
            ChunkSizeBytes = options.ChunkSizeBytes,
            ChunkCount = 0,
            Checksum = "",
        };

        try
        {
            await StreamIntoStorageAsync(bucket, file, body, maxFileSize, budget, ct);
        }
        catch
        {
            // The transaction rolled the row back, but EF still tracks it as saved — unlike every
            // other write in this engine, this one can fail *after* a successful SaveChanges (a
            // quota tripped mid-stream). Detaching keeps a later SaveChanges on this same scoped
            // context from resurrecting a file that does not exist.
            db.Entry(file).State = EntityState.Detached;
            throw;
        }

        await PublishAsync(bucket, "create", file.Id, ct);
        return file;
    }

    private async Task StreamIntoStorageAsync(
        Bucket bucket, StoredFile file, Stream body, long maxFileSize, StorageBudget budget, CancellationToken ct)
    {
        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            // The chunk rows FK to this row, so the metadata goes in first with placeholder
            // size/checksum and is corrected once the bytes are counted. Both writes are inside the
            // one transaction, so the placeholder state is never observable.
            db.Files.Add(file);
            await db.SaveChangesAsync(ct);

            await using var writer = store.OpenWrite(file.Id, file.ChunkSizeBytes);
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), ct)) > 0)
                {
                    // Checked *before* the bytes are written, on every read: a streaming upload can
                    // sail past either limit halfway through, and the answer is a clean rejection
                    // plus the rollback this transaction gives for free — never a truncated file.
                    if (writer.BytesWritten + read > maxFileSize) throw TooLarge(maxFileSize);
                    if (writer.BytesWritten + read > budget.Remaining) throw OverStorageQuota(budget);
                    await writer.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await writer.CompleteAsync(ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            file.SizeBytes = writer.BytesWritten;
            file.ChunkCount = writer.ChunkCount;
            file.Checksum = writer.Checksum;
            file.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await WriteOutboxAsync(bucket, "create", file.Id, ct);
        }, ct);
    }

    // ---- read -------------------------------------------------------------------------------

    public async Task<(int Total, List<StoredFile> Files)> ListAsync(
        Bucket bucket, int limit, int offset, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        await RequireAsync(bucket, PermissionStrings.Read, callerRoles, bypassPermissions, ct);
        var query = db.Files.Where(f => f.BucketId == bucket.Id);
        var total = await query.CountAsync(ct);
        var files = await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip(offset)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);
        return (total, files);
    }

    public async Task<StoredFile> GetAsync(
        Bucket bucket, string fileId, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        await RequireAsync(bucket, PermissionStrings.Read, callerRoles, bypassPermissions, ct);
        return await FindAsync(bucket, fileId, ct);
    }

    /// <summary>
    /// A forward-only stream over the file's bytes, already permission-checked. The caller copies it
    /// to the response body — it is never materialized, so a 2 GB download costs one chunk of memory.
    /// </summary>
    public async Task<(StoredFile File, Stream Content)> OpenDownloadAsync(
        Bucket bucket, string fileId, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var file = await GetAsync(bucket, fileId, callerRoles, bypassPermissions, ct);
        return (file, store.OpenRead(file.Id));
    }

    // ---- update / delete --------------------------------------------------------------------

    /// <summary>Metadata only — the bytes of a stored file are immutable in Phase 1; replacing them means a new upload.</summary>
    public async Task<StoredFile> RenameAsync(
        Bucket bucket, string fileId, string? name, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        await RequireAsync(bucket, PermissionStrings.Update, callerRoles, bypassPermissions, ct);
        RequireEnabled(bucket);
        var file = await FindAsync(bucket, fileId, ct);

        if (name is not null)
            file.Name = ValidateName(name);
        file.UpdatedAt = DateTimeOffset.UtcNow;

        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            await db.SaveChangesAsync(ct);
            await WriteOutboxAsync(bucket, "update", file.Id, ct);
        }, ct);

        await PublishAsync(bucket, "update", file.Id, ct);
        return file;
    }

    public async Task DeleteAsync(
        Bucket bucket, string fileId, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        await RequireAsync(bucket, PermissionStrings.Delete, callerRoles, bypassPermissions, ct);
        RequireEnabled(bucket);
        var file = await FindAsync(bucket, fileId, ct);

        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            // The chunk rows go with it: file_chunks.file_id is ON DELETE CASCADE, so the bytes are
            // gone in the same statement rather than left for a sweeper to find.
            db.Files.Remove(file);
            await db.SaveChangesAsync(ct);
            await WriteOutboxAsync(bucket, "delete", file.Id, ct);
        }, ct);

        await PublishAsync(bucket, "delete", file.Id, ct);
    }

    // ---- permissions ------------------------------------------------------------------------

    /// <summary>Delegates to <see cref="BucketAccess.IsPermitted"/> — see its remarks for why the rule lives in one named place.</summary>
    private async Task RequireAsync(
        Bucket bucket, string action, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        if (bypassPermissions) return;
        var granted = await buckets.RolesAsync(bucket.Id, action, ct);
        if (!BucketAccess.IsPermitted(granted, callerRoles))
            throw PraxyException.Unauthorized($"Not permitted to {action} files in this bucket.");
    }

    private static void RequireEnabled(Bucket bucket)
    {
        if (!bucket.Enabled)
            throw new PraxyException(403, ErrorTypes.BucketDisabled, "This bucket is disabled.");
    }

    private async Task<StoredFile> FindAsync(Bucket bucket, string fileId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(fileId, out var id))
            throw PraxyException.NotFound(ErrorTypes.FileNotFound, "File not found.");
        return await db.Files.FirstOrDefaultAsync(f => f.Id == id && f.BucketId == bucket.Id, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.FileNotFound, "File not found.");
    }

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        // Path separators and NUL are rejected rather than sanitized: a name is a label, never a
        // filesystem path here, and silently rewriting it would make the stored name differ from
        // what the caller believes it uploaded.
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 255 ||
            trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains('\0'))
        {
            throw PraxyException.ArgumentInvalid("Invalid file name.",
                new Dictionary<string, string[]>
                {
                    ["name"] = ["Must be 1-255 characters and contain no path separators."],
                });
        }
        return trimmed;
    }

    private static PraxyException TooLarge(long maxFileSize) =>
        new(400, ErrorTypes.FileSizeExceeded,
            $"This file exceeds the {maxFileSize} byte limit for this bucket.");

    private static PraxyException OverStorageQuota(StorageBudget budget) =>
        new(400, ErrorTypes.GeneralResourceLimitExceeded,
            $"This project's storage quota of {budget.MaxTotalBytes} bytes would be exceeded " +
            $"({budget.UsedBytes} bytes already stored).");

    // ---- outbox / realtime ------------------------------------------------------------------

    /// <summary>
    /// Durable copy in <c>praxy.events</c>, written inside the same transaction as the file change.
    /// <c>readRoles</c> are the bucket's <c>read</c> grants, computed pre-commit — a delete has no
    /// file left to re-query afterward.
    /// </summary>
    private async Task WriteOutboxAsync(Bucket bucket, string action, Guid fileId, CancellationToken ct)
    {
        var (type, payload) = await BuildEventAsync(bucket, action, fileId, ct);
        db.Events.Add(new OutboxEvent
        {
            Id = Ids.NewUuid(),
            ProjectId = bucket.ProjectId,
            Type = type,
            Payload = payload.ToJsonString(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Best-effort in-process fan-out, published after commit (architecture.md §7).</summary>
    private async Task PublishAsync(Bucket bucket, string action, Guid fileId, CancellationToken ct)
    {
        var (type, payload) = await BuildEventAsync(bucket, action, fileId, ct);
        var readRoles = await buckets.RolesAsync(bucket.Id, PermissionStrings.Read, ct);
        await events.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, bucket.ProjectId, type, readRoles, payload), ct);
    }

    private async Task<(string Type, JsonObject Payload)> BuildEventAsync(
        Bucket bucket, string action, Guid fileId, CancellationToken ct)
    {
        var readRoles = await buckets.RolesAsync(bucket.Id, PermissionStrings.Read, ct);
        var type = $"buckets.{Ids.Wire(bucket.Id)}.files.{Ids.Wire(fileId)}.{action}";
        var payload = new JsonObject
        {
            ["bucketId"] = Ids.Wire(bucket.Id),
            ["fileId"] = Ids.Wire(fileId),
            ["roles"] = new JsonArray([.. readRoles.Select(r => (JsonNode?)JsonValue.Create(r))]),
        };
        return (type, payload);
    }
}
