using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Tables;
using Praxy.Tables.Quotas;
using SkiaSharp;

namespace Praxy.Storage;

/// <summary>
/// Resolves a <see cref="TransformRequest"/> against one file's cached derivatives, generating and
/// storing a new one on a miss. Deliberately takes no <c>callerRoles</c>/permission parameters of its
/// own — the caller (<see cref="FilesService"/>) has already run the source file through
/// <c>FileAccessRules</c> before this type is ever reached, and a derivative has no second
/// authorization path to run (docs/research/storage.md).
/// </summary>
public sealed class DerivativesService(
    PraxyDb db, IFileStore fileStore, PostgresDerivativeChunkFileStore derivativeStore,
    StorageOptions options, QuotaService quotas, ImageTransformer transformer)
{
    public async Task<(FileDerivative Derivative, Stream Content)> ResolveAsync(
        Bucket bucket, StoredFile file, TransformRequest request, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(file, request, ct);

        if (await FindAsync(file.Id, key, ct) is { } cached)
            return (cached, derivativeStore.OpenRead(cached.Id, cached.ChunkSizeBytes));

        var sourceBytes = await ReadSourceBytesAsync(file, ct);
        var encoded = transformer.Transform(sourceBytes, key);

        var budget = await quotas.GetStorageBudgetAsync(bucket.ProjectId, ct);
        if (encoded.Length > budget.Remaining)
        {
            throw new PraxyException(400, ErrorTypes.GeneralResourceLimitExceeded,
                $"This project's storage quota of {budget.MaxTotalBytes} bytes would be exceeded " +
                $"({budget.UsedBytes} bytes already stored).");
        }

        var derivative = new FileDerivative
        {
            Id = Ids.NewUuid(),
            FileId = file.Id,
            Width = key.Width,
            Height = key.Height,
            Format = key.Format,
            Quality = key.Quality,
            Gravity = key.Gravity,
            MimeType = key.MimeType,
            SizeBytes = 0,
            ChunkSizeBytes = options.ChunkSizeBytes,
            ChunkCount = 0,
            Checksum = "",
        };

        try
        {
            await StoreAsync(derivative, encoded, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Lost the race: a concurrent request for the same missing derivative finished first.
            // The unique index on (file_id, width, height, format, quality) is what makes this safe
            // to detect — re-read the winner's row rather than serializing every miss behind a lock
            // (docs/research/storage.md's own call on this).
            db.Entry(derivative).State = EntityState.Detached;
            var winner = await FindAsync(file.Id, key, ct) ??
                throw new InvalidOperationException("Lost a derivative race but the winner's row is missing.");
            return (winner, derivativeStore.OpenRead(winner.Id, winner.ChunkSizeBytes));
        }

        return (derivative, derivativeStore.OpenRead(derivative.Id, derivative.ChunkSizeBytes));
    }

    /// <summary>Every existing derivative for one file — the console's "which sizes exist" list.</summary>
    public Task<List<FileDerivative>> ListAsync(Guid fileId, CancellationToken ct) =>
        db.FileDerivatives.Where(d => d.FileId == fileId).OrderBy(d => d.CreatedAt).ToListAsync(ct);

    /// <summary>
    /// Drops every cached derivative for a file. The chunk rows go with them via
    /// <c>file_derivative_chunks</c>' own <c>ON DELETE CASCADE</c> off <c>file_derivatives.id</c> — a
    /// bulk delete on the metadata table is enough, no second statement needed. Used by the console's
    /// explicit purge action and by <see cref="FilesService.ReplaceBytesAsync"/>, which is the one
    /// invalidation the schema's cascade-on-delete-file can never reach: replacing bytes keeps the
    /// same file id, so nothing about the file row itself is deleted.
    /// </summary>
    public Task<int> PurgeAsync(Guid fileId, CancellationToken ct) =>
        db.FileDerivatives.Where(d => d.FileId == fileId).ExecuteDeleteAsync(ct);

    private Task<FileDerivative?> FindAsync(Guid fileId, DerivativeKey key, CancellationToken ct) =>
        db.FileDerivatives.FirstOrDefaultAsync(d =>
            d.FileId == fileId && d.Width == key.Width && d.Height == key.Height &&
            d.Format == key.Format && d.Quality == key.Quality && d.Gravity == key.Gravity, ct);

    private async Task StoreAsync(FileDerivative derivative, byte[] encoded, CancellationToken ct)
    {
        await SchemaDdl.InTransactionAsync(db, async () =>
        {
            // The metadata row goes in first, same order FilesService.UploadAsync uses for a real
            // file: the unique-key violation this method is built to survive happens right here, on
            // this insert, before any chunk bytes exist to roll back.
            db.FileDerivatives.Add(derivative);
            await db.SaveChangesAsync(ct);

            await using var writer = derivativeStore.OpenWrite(derivative.Id, derivative.ChunkSizeBytes);
            await writer.WriteAsync(encoded, ct);
            await writer.CompleteAsync(ct);

            derivative.SizeBytes = writer.BytesWritten;
            derivative.ChunkCount = writer.ChunkCount;
            derivative.Checksum = writer.Checksum;
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    /// <summary>
    /// The source's real pixel dimensions — from the file's own cached probe when one already ran,
    /// or a header-only parse (cheap: <see cref="SKCodec.Create(SKData)"/> reads metadata, not
    /// pixels) cached onto the row for every request after this one. Skipped entirely when the
    /// request already names both axes explicitly: that shape needs no source dimensions at all, so
    /// a cache *hit* for it never touches the source file a second time.
    /// </summary>
    private async Task<DerivativeKey> ResolveKeyAsync(StoredFile file, TransformRequest request, CancellationToken ct)
    {
        if (request.Width is not null && request.Height is not null)
            // Unused by ImageTransforms.Resolve's own crop branch when both axes are explicit.
            return ImageTransforms.Resolve(request, file.MimeType, sourceWidth: 0, sourceHeight: 0);

        var (width, height) = await SourceDimensionsAsync(file, ct);
        return ImageTransforms.Resolve(request, file.MimeType, width, height);
    }

    private async Task<(int Width, int Height)> SourceDimensionsAsync(StoredFile file, CancellationToken ct)
    {
        if (file.Width is { } w && file.Height is { } h)
            return (w, h);

        // Checked before the source is read at all, not just before the decode: an unsupported type
        // should never cost a full-file read just to find that out.
        ImageTransforms.EnsureSupportedSourceType(file.MimeType);

        var sourceBytes = await ReadSourceBytesAsync(file, ct);
        using var data = SKData.CreateCopy(sourceBytes);
        using var codec = SKCodec.Create(data) ??
            throw new PraxyException(400, ErrorTypes.FileTransformInvalid, "This file could not be decoded as an image.");

        // Cached as the EXIF-corrected, visually-upright size, not the raw encoded one — a
        // single-axis request (?width= alone) derives the other axis from this aspect ratio, and a
        // rotated phone photo's encoded width/height are swapped relative to how it actually displays.
        // ImageTransformer.Transform applies the same correction to the real pixels; this only needs
        // to agree with it, not redo it, since a header probe never decodes.
        var swapped = ImageTransformer.SwapsDimensions(codec.EncodedOrigin);
        file.Width = swapped ? codec.Info.Height : codec.Info.Width;
        file.Height = swapped ? codec.Info.Width : codec.Info.Height;
        await db.SaveChangesAsync(ct);
        return (file.Width.Value, file.Height.Value);
    }

    /// <summary>
    /// Buffers the whole source into memory — bounded by the bucket's own upload-size ceiling, and
    /// unavoidable either way: decoding an image at all needs its full encoded bytes, and this same
    /// buffer is reused by the header probe above when that's all a given request needs.
    /// </summary>
    private async Task<byte[]> ReadSourceBytesAsync(StoredFile file, CancellationToken ct)
    {
        var buffer = new byte[file.SizeBytes];
        await using var source = fileStore.OpenRead(file.Id, file.ChunkSizeBytes);
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return buffer;
    }
}
