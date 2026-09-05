using Praxy.Persistence.Entities;

namespace Praxy.Storage;

/// <summary>
/// What <c>StorageTransfer.DownloadAsync</c> needs to write a response, for either the file itself
/// or one of its derivatives. The two cases share every header-writing rule (attachment default,
/// nosniff, the inline allowlist) — only the reported type/size and whether Range applies differ,
/// so this is one type with two factories rather than a branch duplicated at the call site.
/// </summary>
public sealed record FileDownload(
    StoredFile File, string MimeType, long SizeBytes, bool SupportsRange, ByteRangeRequest Range, Stream? Content)
{
    /// <summary>The plain file: Range applies, and the reported type is whatever the uploader sent — unchanged from Phases 1-2.</summary>
    public static FileDownload ForFile(StoredFile file, ByteRangeRequest range, Stream? content) =>
        new(file, file.MimeType, file.SizeBytes, SupportsRange: true, range, content);

    /// <summary>
    /// A generated derivative: always the whole thing (Range is a full-file concern that never
    /// reaches a derivative, per docs/research/storage.md), and the reported type is the encoder's
    /// choice — server-chosen, and therefore never carrying the uploader's own claimed
    /// <see cref="StoredFile.MimeType"/> forward into the inline-serving decision.
    /// </summary>
    public static FileDownload ForDerivative(StoredFile file, FileDerivative derivative, Stream content) =>
        new(file, derivative.MimeType, derivative.SizeBytes, SupportsRange: false,
            new ByteRangeRequest(ByteRangeOutcome.Full, 0, Math.Max(0, derivative.SizeBytes - 1)), content);
}
