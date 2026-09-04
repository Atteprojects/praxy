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
    /// <c>attachment</c> unless the bucket has opted this exact type into inline serving *and*
    /// <see cref="InlineTypes.Safe"/> agrees: a file's stored MIME type is whatever the uploader
    /// sent, so rendering an arbitrary one would be stored XSS on the console's own origin.
    /// </summary>
    public static string Attachment(string fileName) => Build("attachment", fileName);

    /// <summary>
    /// The opt-in form. Identical escaping — the disposition type is the only difference, and the
    /// decision about whether it is allowed belongs to <see cref="InlineTypes.ServesInline"/>, not
    /// here. A caller reaching for this without asking that question first is the bug.
    /// </summary>
    public static string Inline(string fileName) => Build("inline", fileName);

    private static string Build(string disposition, string fileName)
    {
        var ascii = new string(fileName.Where(c => c is >= ' ' and <= '~').ToArray())
            .Replace("\\", "\\\\").Replace("\"", "\\\"");
        if (ascii.Length == 0)
            ascii = "download";
        return $"{disposition}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}
