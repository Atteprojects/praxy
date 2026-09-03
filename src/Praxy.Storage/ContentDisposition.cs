namespace Praxy.Storage;

/// <summary>
/// Builds the <c>Content-Disposition</c> header for a file download. Lives here beside
/// <see cref="MimeTypes"/> — both are the HTTP-shaped edge of storage, and both need unit tests.
///
/// This is a header-injection boundary: a file's name is caller-supplied, and
/// <c>FilesService.ValidateName</c> rejecting control characters is only half the defense (names
/// stored before that rule existed, or reached by any future path, still flow through here).
/// </summary>
public static class ContentDisposition
{
    /// <summary>
    /// <c>attachment; filename="…"; filename*=UTF-8''…</c> per RFC 6266. The quoted form is ASCII
    /// with <c>"</c> and <c>\</c> escaped for old clients; <c>filename*</c> carries the real name
    /// percent-encoded. Anything outside printable ASCII is dropped from the quoted form rather than
    /// escaped, so a raw CR/LF can never reach the header.
    ///
    /// Always <c>attachment</c>, never <c>inline</c>: a file's stored MIME type is whatever the
    /// uploader sent, so rendering it would be stored XSS on the console's own origin. Opt-in inline
    /// serving is Phase 2's job and needs a safe-type allowlist.
    /// </summary>
    public static string Attachment(string fileName)
    {
        var ascii = new string(fileName.Where(c => c is >= ' ' and <= '~').ToArray())
            .Replace("\\", "\\\\").Replace("\"", "\\\"");
        if (ascii.Length == 0)
            ascii = "download";
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}
