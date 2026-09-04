using Praxy.Storage;

namespace Praxy.Tests.Unit;

/// <summary>
/// A stored file's name is caller-supplied and ends up in a response header, so this builder is a
/// header-injection boundary. Found in review of Storage Phase 1: downloads echoed the uploader's
/// own Content-Type with no Content-Disposition and no nosniff, which is stored XSS on the console's
/// own origin — see StorageTransfer.DownloadAsync's remarks.
/// </summary>
public class ContentDispositionTests
{
    [Fact]
    public void A_plain_name_is_quoted_and_also_carries_the_utf8_form()
    {
        var value = ContentDisposition.Attachment("report.pdf");
        Assert.Equal("attachment; filename=\"report.pdf\"; filename*=UTF-8''report.pdf", value);
    }

    /// <summary>CR/LF in a name must never reach the header — that is the injection.</summary>
    [Theory]
    [InlineData("a\r\nX-Injected: yes.txt")]
    [InlineData("a\nSet-Cookie: x=1.txt")]
    [InlineData("a\rb.txt")]
    [InlineData("tab\there.txt")]
    public void Control_characters_never_survive_into_the_header(string name)
    {
        var value = ContentDisposition.Attachment(name);
        Assert.DoesNotContain("\r", value);
        Assert.DoesNotContain("\n", value);
        Assert.DoesNotContain("\t", value);
    }

    [Fact]
    public void Quotes_and_backslashes_are_escaped_not_dropped()
    {
        var value = ContentDisposition.Attachment("we\"ird\\name.txt");
        Assert.Contains("filename=\"we\\\"ird\\\\name.txt\"", value);
    }

    /// <summary>Non-ASCII is dropped from the quoted form but preserved percent-encoded in filename*.</summary>
    [Fact]
    public void Non_ascii_names_are_carried_by_the_utf8_form()
    {
        var value = ContentDisposition.Attachment("résumé-日本.pdf");
        Assert.Contains("filename*=UTF-8''", value);
        Assert.Contains(Uri.EscapeDataString("résumé-日本.pdf"), value);
    }

    /// <summary>A name that is entirely non-ASCII would otherwise emit an empty filename="".</summary>
    [Fact]
    public void A_name_with_no_ascii_at_all_still_gets_a_usable_quoted_fallback()
    {
        var value = ContentDisposition.Attachment("日本語");
        Assert.Contains("filename=\"download\"", value);
        Assert.Contains(Uri.EscapeDataString("日本語"), value);
    }

    /// <summary>
    /// The inline form is the same builder with a different disposition — same escaping, same
    /// injection defense. Whether it is *allowed* is InlineTypes' question, never this type's.
    /// </summary>
    [Fact]
    public void The_inline_form_differs_only_in_the_disposition()
    {
        Assert.Equal("inline; filename=\"cat.png\"; filename*=UTF-8''cat.png", ContentDisposition.Inline("cat.png"));
        Assert.StartsWith("attachment;", ContentDisposition.Attachment("cat.png"));
    }

    [Theory]
    [InlineData("a\r\nX-Injected: yes.png")]
    [InlineData("a\nSet-Cookie: x=1.png")]
    public void Inline_defends_the_header_boundary_identically(string name)
    {
        var value = ContentDisposition.Inline(name);
        Assert.DoesNotContain("\r", value);
        Assert.DoesNotContain("\n", value);
    }
}
