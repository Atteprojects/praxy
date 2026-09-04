namespace Praxy.Storage;

/// <summary>
/// The hard-coded set of types a download may be served <c>inline</c> as, and the check that
/// decides it for one response.
///
/// <para>
/// Read <c>StorageTransfer.DownloadAsync</c>'s remarks first: a file's stored MIME type is whatever
/// the uploader sent, and the console is served from the API's own origin with a <c>SameSite=Lax</c>
/// operator cookie, so anything renderable-and-scriptable served inline is stored XSS against the
/// console. Inline is therefore two gates, not one — the bucket has to opt the type in *and* the
/// type has to be in <see cref="Safe"/> — and <c>X-Content-Type-Options: nosniff</c> stays on every
/// response either way, so a browser can't sniff its way from one of these to a document.
/// </para>
///
/// <para>
/// <b><c>text/html</c> and <c>image/svg+xml</c> are permanently excluded and not configurable.</b>
/// SVG carries script, which is the whole vulnerability again with an extra step. Nothing in this
/// list can execute in the page's origin: bitmap images and media are decoded, never parsed as
/// documents, and <c>text/plain</c> with <c>nosniff</c> cannot be re-interpreted as markup. PDF is
/// rendered by the browser's own sandboxed viewer, which has no access to the embedding origin.
/// </para>
///
/// <para>
/// The stronger answer — serving user content from a separate origin, the way Sites already does —
/// is an owner decision recorded in docs/research/storage.md rather than something this phase
/// assumes. Same-origin inline content is risk management; a different origin makes it structural.
/// </para>
/// </summary>
public static class InlineTypes
{
    public static readonly IReadOnlyList<string> Safe =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "image/avif",
        "video/mp4",
        "video/webm",
        "audio/mpeg",
        "audio/mp4",
        "application/pdf",
        "text/plain",
    ];

    public static bool IsSafe(string mimeType) =>
        Safe.Contains(mimeType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Both gates, in one place so neither can be applied without the other. The bucket's list is
    /// validated against <see cref="Safe"/> when it is written, and intersected with it again here
    /// — belt and braces, because a stored value outlives the set that was current when it was
    /// stored, and shrinking <see cref="Safe"/> must take effect immediately.
    /// </summary>
    public static bool ServesInline(IReadOnlyList<string>? bucketInlineTypes, string mimeType) =>
        bucketInlineTypes is { Count: > 0 } &&
        bucketInlineTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase) &&
        IsSafe(mimeType);
}
