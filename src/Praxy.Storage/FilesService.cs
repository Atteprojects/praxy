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
/// <c>IRoleResolver</c> — escalating to the file's own grants exactly the way row security
/// escalates from the table matrix (<see cref="FileAccessRules"/>). Storage introduces no second
/// authorization concept (CLAUDE.md's cross-phase rule). Writes go through the outbox like every
/// other write since Phase 3.
/// </summary>
public sealed class FilesService(
    PraxyDb db, IFileStore store, BucketsService buckets, QuotaService quotas,
    StorageOptions options, IEventBus events, DerivativesService derivatives)
{
    /// <summary>Copy buffer between the request body and the chunk writer — unrelated to the chunk size, which is how much is buffered before a row is written.</summary>
    private const int CopyBufferBytes = 81_920;

    // ---- upload -----------------------------------------------------------------------------

    /// <summary>
    /// Streams <paramref name="body"/> straight into chunk rows inside one transaction: either the
    /// whole file and its metadata commit, or neither does, so a failed or over-quota upload can
    /// never leave an orphaned half-file. Nothing here materializes the file — peak memory is one
    /// chunk plus one copy buffer, whatever the file's size.
    ///
    /// <paramref name="permissions"/> are the new file's own grants, attached in the same
    /// transaction. There is no auto-grant to the uploader: rows don't do it either, and a magic
    /// grant that only some callers want is worse than one explicit line at the call site
    /// (docs/research/storage.md leaves this open — following rows is the answer this phase gives).
    /// </summary>
    public async Task<StoredFile> UploadAsync(
        Bucket bucket, string? name, string? contentType, long? contentLength, Stream body,
        string[]? permissions, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        // Create is bucket-level only, whatever file_security says: there is no file yet for a
        // per-file grant to hang off, which is the same reason a row can't grant its own creation.
        await RequireBucketAsync(bucket, PermissionStrings.Create, callerRoles, bypassPermissions, ct);
        RequireEnabled(bucket);
        var filePermissions = ParsePermissions(bucket, permissions);

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
            await StreamIntoStorageAsync(bucket, file, body, maxFileSize, budget, filePermissions, ct);
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
        Bucket bucket, StoredFile file, Stream body, long maxFileSize, StorageBudget budget,
        IReadOnlyList<(string Action, string Role)> permissions, CancellationToken ct)
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
            if (permissions.Count > 0)
                db.FilePermissions.AddRange(permissions.Select(p => new FilePermission
                {
                    FileId = file.Id,
                    Action = p.Action,
                    Role = p.Role,
                }));
            await db.SaveChangesAsync(ct);

            await WriteOutboxAsync(bucket, "create", file.Id, ct);
        }, ct);
    }

    // ---- read -------------------------------------------------------------------------------

    /// <summary>
    /// The permission filter is folded into the EF query rather than applied to the page after the
    /// fact — that is the whole difficulty of this method. Filtering after <c>Skip</c>/<c>Take</c>
    /// would report a <c>total</c> counting files the caller cannot see and hand back short (or
    /// empty) pages; this is the direct analogue of the <c>EXISTS</c> the query compiler folds into
    /// its <c>WHERE</c> for row security.
    /// </summary>
    public async Task<(int Total, List<StoredFile> Files)> ListAsync(
        Bucket bucket, int limit, int offset, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var decision = await ResolveAsync(bucket, PermissionStrings.Read, callerRoles, bypassPermissions, ct);
        if (decision == FileAccessDecision.Deny)
            throw Denied(PermissionStrings.Read);

        var query = db.Files.Where(f => f.BucketId == bucket.Id);
        if (decision == FileAccessDecision.PerFile)
            query = query.Where(f => db.FilePermissions.Any(p =>
                p.FileId == f.Id && p.Action == PermissionStrings.Read && callerRoles.Contains(p.Role)));

        var total = await query.CountAsync(ct);
        var files = await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip(offset)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);
        return (total, files);
    }

    public Task<StoredFile> GetAsync(
        Bucket bucket, string fileId, string[] callerRoles, bool bypassPermissions, CancellationToken ct) =>
        RequireFileAsync(bucket, fileId, PermissionStrings.Read, callerRoles, bypassPermissions, ct);

    /// <summary>
    /// A forward-only stream over the file's bytes — or, when <paramref name="transform"/> asks for
    /// one, over a generated derivative's bytes instead — already permission-checked either way. The
    /// caller copies it to the response body; neither path materializes more than one file's worth of
    /// memory.
    ///
    /// <para>
    /// <b>A derivative resolves through exactly this same permission check, never a second one.</b>
    /// <paramref name="transform"/> is only consulted *after* <see cref="GetAsync"/> has already
    /// decided the caller may read <paramref name="fileId"/> — a derivative is a representation of
    /// that file, not a resource with grants of its own (docs/research/storage.md), so there is no
    /// separate check to add and no way to reach one without first passing this one.
    /// </para>
    ///
    /// <para>
    /// <paramref name="rangeHeader"/> is resolved against the size this same lookup just read, so a
    /// range can be honoured without a second metadata round trip — but only on the plain-file path;
    /// a transform request ignores it entirely (Range is a full-file concern that doesn't apply to a
    /// generated derivative). The resulting offset/length go *through* the store seam rather than
    /// being skipped off the front of a full stream: reading-and-discarding works for the Postgres
    /// backend and would force a future S3-compatible one to fetch a whole object to serve a
    /// kilobyte (docs/research/storage.md).
    /// </para>
    ///
    /// <para>The content stream is null — and only null — for an unsatisfiable range, which the caller answers with a 416.</para>
    /// </summary>
    public async Task<FileDownload> OpenDownloadAsync(
        Bucket bucket, string fileId, string? rangeHeader, TransformRequest transform,
        string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var file = await GetAsync(bucket, fileId, callerRoles, bypassPermissions, ct);

        if (transform.IsRequested)
        {
            var (derivative, content) = await derivatives.ResolveAsync(bucket, file, transform, ct);
            return FileDownload.ForDerivative(file, derivative, content);
        }

        var range = ByteRanges.Parse(rangeHeader, file.SizeBytes);
        if (range.Outcome == ByteRangeOutcome.Unsatisfiable)
            return FileDownload.ForFile(file, range, null);

        var fileContent = range.Outcome == ByteRangeOutcome.Partial
            ? store.OpenRead(file.Id, file.ChunkSizeBytes, range.Start, range.Length)
            : store.OpenRead(file.Id, file.ChunkSizeBytes);
        return FileDownload.ForFile(file, range, fileContent);
    }

    /// <summary>
    /// Replaces an existing file's bytes in place, keeping its id — the capability Phase 1 explicitly
    /// left out ("the bytes of a stored file are immutable... replacing them means a new upload") and
    /// Phase 3 has to add: a derivative is keyed by file id, and "upload a new file instead" would
    /// leave the old file's now-stale derivatives sitting under an id nothing points at for cleanup.
    /// Gated on <c>update</c>, the same permission <see cref="RenameAsync"/> uses — replacing the
    /// bytes of a file you're allowed to rename is the same permission question.
    /// </summary>
    public async Task<StoredFile> ReplaceBytesAsync(
        Bucket bucket, string fileId, string? name, string? contentType, long? contentLength, Stream body,
        string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var file = await RequireFileAsync(
            bucket, fileId, PermissionStrings.Update, callerRoles, bypassPermissions, ct, requireEnabled: true);

        var mimeType = MimeTypes.Normalize(contentType);
        if (!MimeTypes.IsAllowed(bucket.AllowedMimeTypes, mimeType))
            throw new PraxyException(400, ErrorTypes.FileTypeNotAllowed,
                $"This bucket does not accept '{mimeType}'. Allowed: {string.Join(", ", bucket.AllowedMimeTypes!)}.");

        var budget = await quotas.GetStorageBudgetAsync(bucket.ProjectId, ct);
        var maxFileSize = Math.Min(bucket.MaxFileSizeBytes, budget.MaxFileSizeBytes);
        // The bytes being replaced stop counting toward "used" the moment the new ones land, so this
        // replace may spend the project's free headroom *plus* what this file already occupies —
        // otherwise replacing a file with one the same size or smaller could spuriously trip a quota
        // that is actually about to go down.
        var availableForThisFile = budget.Remaining + file.SizeBytes;

        if (contentLength is { } declared)
        {
            if (declared > maxFileSize) throw TooLarge(maxFileSize);
            if (declared > availableForThisFile) throw OverStorageQuota(budget);
        }

        try
        {
            await ReplaceBytesInStorageAsync(bucket, file, name, mimeType, body, maxFileSize, availableForThisFile, budget, ct);
        }
        catch
        {
            // The transaction rolled everything back, but this same tracked entity had its fields
            // mutated in memory before the throw — reload rather than leaving it disagreeing with
            // what's actually committed (the row still exists, unlike a failed create's placeholder).
            await db.Entry(file).ReloadAsync(ct);
            throw;
        }

        await PublishAsync(bucket, "update", file.Id, ct);
        return file;
    }

    private async Task ReplaceBytesInStorageAsync(
        Bucket bucket, StoredFile file, string? name, string mimeType, Stream body, long maxFileSize,
        long remaining, StorageBudget budget, CancellationToken ct)
    {
        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            // Old bytes and derivatives go first — purging derivatives here is the one invalidation
            // the schema's ON DELETE CASCADE can never reach on its own, since replacing bytes keeps
            // the file's id and therefore never deletes the row that cascade hangs off. Both this and
            // the new bytes below are inside the one transaction, so a failed replace never leaves
            // the file without its old bytes and without new ones either.
            await store.DeleteAsync(file.Id, ct);
            await derivatives.PurgeAsync(file.Id, ct);

            await using var writer = store.OpenWrite(file.Id, options.ChunkSizeBytes);
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), ct)) > 0)
                {
                    if (writer.BytesWritten + read > maxFileSize) throw TooLarge(maxFileSize);
                    if (writer.BytesWritten + read > remaining) throw OverStorageQuota(budget);
                    await writer.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await writer.CompleteAsync(ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (name is not null)
                file.Name = ValidateName(name);
            file.MimeType = mimeType;
            file.ChunkSizeBytes = options.ChunkSizeBytes;
            file.SizeBytes = writer.BytesWritten;
            file.ChunkCount = writer.ChunkCount;
            file.Checksum = writer.Checksum;
            // Phase 3's own lazily-cached probe (DerivativesService.SourceDimensionsAsync) — valid
            // only for the bytes it measured, which no longer exist.
            file.Width = null;
            file.Height = null;
            file.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await WriteOutboxAsync(bucket, "update", file.Id, ct);
        }, ct);
    }

    // ---- update / delete --------------------------------------------------------------------

    /// <summary>Metadata only — the bytes of a stored file are immutable in Phase 1; replacing them means a new upload.</summary>
    public async Task<StoredFile> RenameAsync(
        Bucket bucket, string fileId, string? name, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var file = await RequireFileAsync(
            bucket, fileId, PermissionStrings.Update, callerRoles, bypassPermissions, ct, requireEnabled: true);

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
        var file = await RequireFileAsync(
            bucket, fileId, PermissionStrings.Delete, callerRoles, bypassPermissions, ct, requireEnabled: true);

        // Captured before the row (and its permission rows, via ON DELETE CASCADE) disappear —
        // this is what makes the delete event authorizable to the same audience afterwards.
        var readRoles = await ReadRolesAsync(bucket, file.Id, ct);

        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            // The chunk rows go with it: file_chunks.file_id is ON DELETE CASCADE, so the bytes are
            // gone in the same statement rather than left for a sweeper to find. file_permissions
            // cascades from the same FK.
            db.Files.Remove(file);
            await db.SaveChangesAsync(ct);
            await WriteOutboxAsync(bucket, "delete", file.Id, ct, readRoles);
        }, ct);

        await PublishAsync(bucket, "delete", file.Id, ct, readRoles);
    }

    // ---- permissions ------------------------------------------------------------------------

    /// <summary>
    /// The grants attached to one file. Empty when the bucket has <c>file_security</c> off — the
    /// rows may exist (the flag can be turned off again) but nothing consults them, so reporting
    /// them as live grants would be a lie.
    /// </summary>
    public async Task<string[]> GetFilePermissionsAsync(Bucket bucket, Guid fileId, CancellationToken ct)
    {
        if (!bucket.FileSecurity)
            return [];
        var rows = await db.FilePermissions
            .Where(p => p.FileId == fileId)
            .OrderBy(p => p.Action).ThenBy(p => p.Role)
            .ToListAsync(ct);
        return [.. rows.Select(p => PermissionStrings.Format(p.Action, p.Role))];
    }

    /// <summary>The same lookup for a whole page, one query — the list screen's <c>$permissions</c>.</summary>
    public async Task<Dictionary<Guid, string[]>> GetFilePermissionsAsync(
        Bucket bucket, IReadOnlyList<Guid> fileIds, CancellationToken ct)
    {
        if (!bucket.FileSecurity || fileIds.Count == 0)
            return [];
        var rows = await db.FilePermissions
            .Where(p => fileIds.Contains(p.FileId))
            .OrderBy(p => p.Action).ThenBy(p => p.Role)
            .ToListAsync(ct);
        return rows.GroupBy(p => p.FileId)
            .ToDictionary(g => g.Key, g => g.Select(p => PermissionStrings.Format(p.Action, p.Role)).ToArray());
    }

    /// <summary>
    /// Full-replace semantics, like every other permission surface here: the given set becomes the
    /// file's entire grant. Gated on <c>update</c> for that file, so a caller who was granted
    /// <c>update("user:self")</c> on their own upload can re-share it without operator help.
    /// </summary>
    public async Task<string[]> ReplaceFilePermissionsAsync(
        Bucket bucket, string fileId, string[] permissions, string[] callerRoles, bool bypassPermissions,
        CancellationToken ct)
    {
        var file = await RequireFileAsync(
            bucket, fileId, PermissionStrings.Update, callerRoles, bypassPermissions, ct, requireEnabled: true);
        var parsed = ParsePermissions(bucket, permissions);

        file.UpdatedAt = DateTimeOffset.UtcNow;
        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            await db.FilePermissions.Where(p => p.FileId == file.Id).ExecuteDeleteAsync(ct);
            db.FilePermissions.AddRange(parsed.Select(p => new FilePermission
            {
                FileId = file.Id,
                Action = p.Action,
                Role = p.Role,
            }));
            await db.SaveChangesAsync(ct);
            await WriteOutboxAsync(bucket, "update", file.Id, ct);
        }, ct);

        await PublishAsync(bucket, "update", file.Id, ct);
        return await GetFilePermissionsAsync(bucket, file.Id, ct);
    }

    /// <summary>
    /// Reports what the bucket-level matrix already decided, instead of throwing on a miss.
    ///
    /// <para>
    /// This used to throw whenever the bucket didn't grant the action, which was right while
    /// bucket-level was the only level and became wrong the moment per-file grants existed: a
    /// caller with no bucket grant but a matching file grant must get their file, not a 403. The
    /// two ways to get this wrong are opposite and both bad — defaulting to allow is a security
    /// hole, keeping the throw silently kills the feature — so the branch order lives in
    /// <see cref="FileAccessRules"/> with its own tests, and every caller here handles all three
    /// outcomes explicitly.
    /// </para>
    /// </summary>
    private async Task<FileAccessDecision> ResolveAsync(
        Bucket bucket, string action, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var granted = await buckets.RolesAsync(bucket.Id, action, ct);
        return FileAccessRules.Resolve(bypassPermissions, granted, callerRoles, bucket.FileSecurity);
    }

    /// <summary>For actions no per-file grant can ever cover (create): bucket level or nothing.</summary>
    private async Task RequireBucketAsync(
        Bucket bucket, string action, string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        if (await ResolveAsync(bucket, action, callerRoles, bypassPermissions, ct) != FileAccessDecision.Allow)
            throw Denied(action);
    }

    /// <summary>
    /// Loads a file the caller may take <paramref name="action"/> on. A per-file miss is a 404
    /// rather than a 401 — the same answer a row the caller can't see gives, so the existence of
    /// someone else's file doesn't leak through the status code.
    ///
    /// <paramref name="requireEnabled"/> is checked *between* the bucket-level decision and the file
    /// lookup, which is the order Phase 1's writes already used: a caller the bucket denies outright
    /// gets the same 401 whatever state the bucket is in, and a permitted one gets the
    /// bucket-disabled error before a missing file turns it into a 404.
    /// </summary>
    private async Task<StoredFile> RequireFileAsync(
        Bucket bucket, string fileId, string action, string[] callerRoles, bool bypassPermissions,
        CancellationToken ct, bool requireEnabled = false)
    {
        var decision = await ResolveAsync(bucket, action, callerRoles, bypassPermissions, ct);
        if (decision == FileAccessDecision.Deny)
            throw Denied(action);
        if (requireEnabled)
            RequireEnabled(bucket);

        var file = await FindAsync(bucket, fileId, ct);
        if (decision == FileAccessDecision.PerFile && !await HasFileGrantAsync(file.Id, action, callerRoles, ct))
            throw PraxyException.NotFound(ErrorTypes.FileNotFound, "File not found.");
        return file;
    }

    private Task<bool> HasFileGrantAsync(Guid fileId, string action, string[] callerRoles, CancellationToken ct) =>
        db.FilePermissions.AnyAsync(
            p => p.FileId == fileId && p.Action == action && callerRoles.Contains(p.Role), ct);

    /// <summary>
    /// Per-file grants on a bucket that doesn't consult them would be dead configuration that reads
    /// as protection — the same trap <c>RowsService.RequirePermissionsAllowed</c> closes for rows.
    /// </summary>
    private static IReadOnlyList<(string Action, string Role)> ParsePermissions(Bucket bucket, string[]? permissions)
    {
        if (permissions is not { Length: > 0 })
            return [];
        if (!bucket.FileSecurity)
            throw PraxyException.ArgumentInvalid(
                "Per-file permissions require file_security to be enabled on this bucket.",
                new Dictionary<string, string[]>
                {
                    ["permissions"] = ["Enable file_security on this bucket to grant per-file permissions."],
                });
        return FilePermissions.Parse(permissions);
    }

    private static PraxyException Denied(string action) =>
        PraxyException.Unauthorized($"Not permitted to {action} files in this bucket.");

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
        // Path separators and control characters are rejected rather than sanitized: a name is a
        // label, never a filesystem path here, and silently rewriting it would make the stored name
        // differ from what the caller believes it uploaded. Control characters are excluded as a
        // class rather than just NUL — a name carrying CR/LF that reaches a response header is
        // header injection, and `StorageTransfer` puts the name in `Content-Disposition`. That
        // header builder defends itself too; this is the other half, so a bad name is never stored
        // in the first place and no future consumer has to remember.
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 255 ||
            trimmed.Contains('/') || trimmed.Contains('\\') ||
            trimmed.Any(char.IsControl))
        {
            throw PraxyException.ArgumentInvalid("Invalid file name.",
                new Dictionary<string, string[]>
                {
                    ["name"] = ["Must be 1-255 characters with no path separators or control characters."],
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
    /// <c>readRoles</c> are computed pre-commit — a delete has no file left to re-query afterward,
    /// which is also why they can be passed in.
    /// </summary>
    private async Task WriteOutboxAsync(
        Bucket bucket, string action, Guid fileId, CancellationToken ct, string[]? readRoles = null)
    {
        var (type, payload) = await BuildEventAsync(bucket, action, fileId, ct, readRoles);
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
    private async Task PublishAsync(
        Bucket bucket, string action, Guid fileId, CancellationToken ct, string[]? readRoles = null)
    {
        var (type, payload) = await BuildEventAsync(bucket, action, fileId, ct, readRoles);
        var roles = readRoles ?? await ReadRolesAsync(bucket, fileId, ct);
        await events.PublishAsync(new PraxyEvent(
            Ids.Wire(Ids.NewUuid()), DateTimeOffset.UtcNow, bucket.ProjectId, type, roles, payload), ct);
    }

    private async Task<(string Type, JsonObject Payload)> BuildEventAsync(
        Bucket bucket, string action, Guid fileId, CancellationToken ct, string[]? readRoles)
    {
        var roles = readRoles ?? await ReadRolesAsync(bucket, fileId, ct);
        var type = $"buckets.{Ids.Wire(bucket.Id)}.files.{Ids.Wire(fileId)}.{action}";
        var payload = new JsonObject
        {
            ["bucketId"] = Ids.Wire(bucket.Id),
            ["fileId"] = Ids.Wire(fileId),
            ["roles"] = new JsonArray([.. roles.Select(r => (JsonNode?)JsonValue.Create(r))]),
        };
        return (type, payload);
    }

    /// <summary>
    /// Who may see this file's events: the bucket's <c>read</c> grants plus, when the bucket opts
    /// into per-file security, the file's own <c>read</c> grants. Additive in the fan-out exactly as
    /// it is in the query — a subscriber who can read the file through either level gets the event,
    /// and one who can read it through neither gets nothing.
    /// </summary>
    private async Task<string[]> ReadRolesAsync(Bucket bucket, Guid fileId, CancellationToken ct)
    {
        var roles = new List<string>(await buckets.RolesAsync(bucket.Id, PermissionStrings.Read, ct));
        if (bucket.FileSecurity)
        {
            roles.AddRange(await db.FilePermissions
                .Where(p => p.FileId == fileId && p.Action == PermissionStrings.Read)
                .Select(p => p.Role)
                .ToArrayAsync(ct));
        }
        return [.. roles.Distinct()];
    }
}
