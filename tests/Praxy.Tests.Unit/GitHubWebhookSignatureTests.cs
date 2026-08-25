using System.Security.Cryptography;
using System.Text;
using Praxy.Vcs;

namespace Praxy.Tests.Unit;

public class GitHubWebhookSignatureTests
{
    private static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void Verify_succeeds_for_a_correctly_signed_body()
    {
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var header = Sign("a-secret", """{"hello":"world"}""");
        Assert.True(GitHubWebhookSignature.Verify("a-secret", body, header));
    }

    [Fact]
    public void Verify_fails_when_the_secret_differs()
    {
        var body = Encoding.UTF8.GetBytes("body");
        var header = Sign("secret-a", "body");
        Assert.False(GitHubWebhookSignature.Verify("secret-b", body, header));
    }

    [Fact]
    public void Verify_fails_when_the_body_is_tampered_with()
    {
        var header = Sign("secret", "body-a");
        Assert.False(GitHubWebhookSignature.Verify("secret", Encoding.UTF8.GetBytes("body-b"), header));
    }

    [Fact]
    public void Verify_fails_on_a_missing_or_malformed_header_without_throwing()
    {
        var body = Encoding.UTF8.GetBytes("body");
        Assert.False(GitHubWebhookSignature.Verify("secret", body, null));
        Assert.False(GitHubWebhookSignature.Verify("secret", body, ""));
        Assert.False(GitHubWebhookSignature.Verify("secret", body, "not-a-real-signature"));
        // Right hash, wrong scheme prefix — GitHub's own header always carries "sha256=".
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), body));
        Assert.False(GitHubWebhookSignature.Verify("secret", body, $"sha1={hash}"));
    }

    [Fact]
    public void Verify_matches_the_documented_sha256_prefixed_hex_hmac_scheme()
    {
        // Cross-checked against System.Security.Cryptography.HMACSHA256 directly, not just against
        // itself — mirrors WebhookSignatureTests' own "don't only test the mirror image" discipline.
        var body = Encoding.UTF8.GetBytes("hello");
        var expectedHash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), body));
        Assert.True(GitHubWebhookSignature.Verify("secret", body, $"sha256={expectedHash}"));
    }
}
