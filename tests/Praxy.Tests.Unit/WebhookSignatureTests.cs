using Praxy.Webhooks;

namespace Praxy.Tests.Unit;

public class WebhookSignatureTests
{
    [Fact]
    public void Sign_is_deterministic_and_verifies_against_the_same_inputs()
    {
        var signature = WebhookSignature.Sign("a-secret", 1_700_000_000, """{"hello":"world"}""");
        Assert.StartsWith("v1=", signature);
        Assert.True(WebhookSignature.Verify("a-secret", 1_700_000_000, """{"hello":"world"}""", signature));
    }

    [Fact]
    public void Verify_fails_when_the_secret_differs()
    {
        var signature = WebhookSignature.Sign("secret-a", 1, "body");
        Assert.False(WebhookSignature.Verify("secret-b", 1, "body", signature));
    }

    [Fact]
    public void Verify_fails_when_the_timestamp_differs()
    {
        var signature = WebhookSignature.Sign("secret", 1, "body");
        Assert.False(WebhookSignature.Verify("secret", 2, "body", signature));
    }

    [Fact]
    public void Verify_fails_when_the_body_differs()
    {
        var signature = WebhookSignature.Sign("secret", 1, "body-a");
        Assert.False(WebhookSignature.Verify("secret", 1, "body-b", signature));
    }

    [Fact]
    public void Verify_fails_on_a_malformed_signature_without_throwing()
    {
        Assert.False(WebhookSignature.Verify("secret", 1, "body", "not-a-real-signature"));
        Assert.False(WebhookSignature.Verify("secret", 1, "body", ""));
    }

    [Fact]
    public void Sign_matches_the_documented_hex_HMACSHA256_scheme()
    {
        // Cross-checked against System.Security.Cryptography.HMACSHA256 directly, not just
        // against itself — a regression that breaks both the same way would slip past
        // Sign_is_deterministic_and_verifies_against_the_same_inputs alone.
        var expectedHash = Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("secret"), System.Text.Encoding.UTF8.GetBytes("42.body")));
        Assert.Equal($"v1={expectedHash}", WebhookSignature.Sign("secret", 42, "body"));
    }
}
