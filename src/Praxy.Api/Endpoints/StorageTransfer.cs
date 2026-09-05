using Microsoft.AspNetCore.Http.Features;
using Praxy.Core.Errors;
using Praxy.Persistence.Entities;
using Praxy.Storage;
using Praxy.Tables.Quotas;

namespace Praxy.Api.Endpoints;

/// <summary>
/// The upload/download plumbing shared by the data-plane (<see cref="StorageEndpoints"/>) and
/// console (<see cref="ConsoleStorageEndpoints"/>) surfaces: raising the per-request body limit to
/// the resolved quota, translating Kestrel's own body-limit failure into Praxy's typed error, and
/// streaming a file back with correct headers.
/// </summary>
internal static class StorageTransfer
{
    /// <summary>The upload's file name, from <c>?name=</c>. Percent-decoded by the query parser, so non-ASCII names survive.</summary>
    public static string? FileName(HttpContext http) => http.Request.Query["name"].FirstOrDefault();

    /// <summary>
    /// The new file's own grants, from repeated <c>?permissions=read("user:abc")</c>. Query rather
    /// than body because the body *is* the file's bytes — there is no JSON envelope on an upload to
    /// put them in, which is the one place storage can't mirror a row's create payload exactly.
    /// </summary>
    public static string[]? Permissions(HttpContext http) =>
        http.Request.Query["permissions"] is { Count: > 0 } values ? [.. values!] : null;

    /// <summary>
    /// The download endpoint's <c>?width=/?height=/?format=/?quality=</c>. An absent parameter is
    /// <c>null</c> (not requested); a present-but-unparseable one is a clean 400 here rather than
    /// silently collapsing to "not requested" — a typo'd <c>?width=abc</c> should fail loudly, the
    /// same way an out-of-ladder value does in <see cref="ImageTransforms.Resolve"/>, not fall
    /// through to serving the plain, untransformed file.
    /// </summary>
    public static TransformRequest ParseTransform(HttpContext http)
    {
        var query = http.Request.Query;
        return new TransformRequest(
            ParseInt("width", query["width"]), ParseInt("height", query["height"]),
            query["format"].FirstOrDefault(), ParseInt("quality", query["quality"]));
    }

    private static int? ParseInt(string paramName, Microsoft.Extensions.Primitives.StringValues values)
    {
        var raw = values.FirstOrDefault();
        if (raw is null) return null;
        if (!int.TryParse(raw, out var value))
            throw new PraxyException(400, ErrorTypes.FileTransformInvalid, $"'{paramName}' must be a whole number.");
        return value;
    }

    /// <summary>
    /// Runs one upload with the request-body limit lifted to this project's resolved
    /// <c>MaxFileSizeBytes</c>.
    ///
    /// Kestrel's default 30 MB cap rejects a body before any Praxy check can run, so Program.cs
    /// raises the server-wide default to the same configured quota. This raises it again
    /// per-request to the value actually resolved for *this* project, which an organization's
    /// <c>limits</c> jsonb can set above the instance default — otherwise a raised org limit would
    /// be silently unreachable. Both numbers come from <c>Praxy:Quotas:MaxFileSizeBytes</c>, so
    /// they can never disagree at a size nobody configured.
    ///
    /// Kestrel is still the backstop for a client that lies about <c>Content-Length</c>, and its
    /// failure is translated here into the same <see cref="ErrorTypes.FileSizeExceeded"/> the
    /// service's own streaming check raises — one error for one condition, whichever guard sees it.
    /// </summary>
    public static async Task<StoredFile> UploadAsync(
        HttpContext http, QuotaService quotas, FilesService files, Bucket bucket,
        string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var budget = await quotas.GetStorageBudgetAsync(bucket.ProjectId, ct);
        var maxFileSize = Math.Min(bucket.MaxFileSizeBytes, budget.MaxFileSizeBytes);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = maxFileSize;

        try
        {
            return await files.UploadAsync(
                bucket, FileName(http), http.Request.ContentType, http.Request.ContentLength,
                http.Request.Body, Permissions(http), callerRoles, bypassPermissions, ct);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new PraxyException(400, ErrorTypes.FileSizeExceeded,
                $"This file exceeds the {maxFileSize} byte limit for this bucket.");
        }
    }

    /// <summary>
    /// Same request-body-limit dance as <see cref="UploadAsync"/>, for replacing an existing file's
    /// bytes in place instead of creating a new one.
    /// </summary>
    public static async Task<StoredFile> ReplaceAsync(
        HttpContext http, QuotaService quotas, FilesService files, Bucket bucket, string fileId,
        string[] callerRoles, bool bypassPermissions, CancellationToken ct)
    {
        var budget = await quotas.GetStorageBudgetAsync(bucket.ProjectId, ct);
        var maxFileSize = Math.Min(bucket.MaxFileSizeBytes, budget.MaxFileSizeBytes);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = maxFileSize;

        try
        {
            return await files.ReplaceBytesAsync(
                bucket, fileId, FileName(http), http.Request.ContentType, http.Request.ContentLength,
                http.Request.Body, callerRoles, bypassPermissions, ct);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new PraxyException(400, ErrorTypes.FileSizeExceeded,
                $"This file exceeds the {maxFileSize} byte limit for this bucket.");
        }
    }

    /// <summary>
    /// Streams the file's bytes to the response. Headers are set from the metadata row and the
    /// chunks are copied straight through — the whole file is never materialized, which is the
    /// only reason a multi-gigabyte download is affordable at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Downloads are always served as attachments, never rendered.</b> A file's
    /// <see cref="StoredFile.MimeType"/> comes from whatever <c>Content-Type</c> the uploader sent,
    /// and buckets accept any type by default — so echoing it back on a document the browser will
    /// render is stored XSS. The console is served from this same origin
    /// (<c>UseStaticFiles</c>/<c>MapFallbackToFile</c> in <c>Program.cs</c>), and the operator
    /// session cookie is <c>SameSite=Lax</c>, so a script in an uploaded <c>text/html</c> file would
    /// run same-origin with the console and could drive the console API as that operator —
    /// <c>HttpOnly</c> stops it reading the cookie, not from sending it.
    /// </para>
    /// <para>
    /// Two headers close that: <c>Content-Disposition: attachment</c> so the browser saves rather
    /// than renders, and <c>nosniff</c> so it cannot MIME-sniff its way back to rendering. The real
    /// <c>Content-Type</c> is still reported, because it is useful metadata and harmless once the
    /// response can't become an active document.
    /// </para>
    /// <para>
    /// A bucket can opt specific types into <c>inline</c> — but only types <see cref="InlineTypes"/>
    /// considers safe, never <c>text/html</c> or <c>image/svg+xml</c>, and <c>nosniff</c> stays on
    /// regardless. That is the only way the attachment default is ever lifted; dropping these
    /// headers for any other reason re-opens the hole above.
    /// </para>
    /// </remarks>
    public static async Task<IResult> DownloadAsync(HttpContext http, Bucket bucket, FileDownload download, CancellationToken ct)
    {
        // 416: the client asked for bytes this file doesn't have. Content-Range reports the real
        // size so it can retry correctly, and it survives the throw because ErrorHandlingMiddleware
        // writes the envelope onto this same response rather than resetting it. Never reached for a
        // derivative — FileDownload.ForDerivative always resolves to ByteRangeOutcome.Full.
        if (download.Range.Outcome == ByteRangeOutcome.Unsatisfiable)
        {
            http.Response.Headers.ContentRange = $"bytes */{download.SizeBytes}";
            throw new PraxyException(416, ErrorTypes.FileRangeNotSatisfiable,
                $"That range lies outside this file's {download.SizeBytes} bytes.");
        }

        // Non-null for every outcome but the 416 above, which has already returned.
        await using (var body = download.Content!)
        {
            http.Response.ContentType = download.MimeType;
            // Advertised only for the plain-file path — a media player probing for seek support on a
            // generated derivative would otherwise be told it can Range a resource that ignores it.
            if (download.SupportsRange)
                http.Response.Headers.AcceptRanges = "bytes";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            // Orthogonal to the range: a 206 is still an attachment unless this bucket opted this
            // exact type into inline serving. Adding partial content must not quietly drop that. A
            // derivative's type is the encoder's own choice, never the uploader's — safer than the
            // source, but still gated by the same two-part check (docs/research/storage.md).
            http.Response.Headers.ContentDisposition =
                InlineTypes.ServesInline(bucket.InlineTypes, download.MimeType)
                    ? ContentDisposition.Inline(download.File.Name)
                    : ContentDisposition.Attachment(download.File.Name);

            if (download.Range.Outcome == ByteRangeOutcome.Partial)
            {
                http.Response.StatusCode = StatusCodes.Status206PartialContent;
                http.Response.Headers.ContentRange = $"bytes {download.Range.Start}-{download.Range.End}/{download.SizeBytes}";
                // The length of the *part*, not of the file. Getting this wrong makes players hang
                // waiting for bytes that never come rather than failing loudly.
                http.Response.ContentLength = download.Range.Length;
            }
            else
            {
                http.Response.ContentLength = download.SizeBytes;
            }

            await body.CopyToAsync(http.Response.Body, ct);
        }
        return Results.Empty;
    }
}
