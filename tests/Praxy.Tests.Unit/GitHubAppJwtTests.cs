using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Praxy.Core.Errors;
using Praxy.Vcs;

namespace Praxy.Tests.Unit;

public class GitHubAppJwtTests
{
    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly string PrivateKeyPem = Rsa.ExportRSAPrivateKeyPem();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static GitHubAppOptions Options(string privateKey) =>
        new("app-123", "client-id", "client-secret", privateKey, "webhook-secret");

    [Theory]
    [InlineData("", "app-123")]
    [InlineData("   ", "app-123")]
    [InlineData("some-pem", "")]
    [InlineData("some-pem", "   ")]
    public void Create_throws_a_typed_error_when_the_app_isnt_configured_rather_than_a_raw_pem_exception(
        string privateKey, string appId)
    {
        var options = new GitHubAppOptions(appId, "client-id", "client-secret", privateKey, "webhook-secret");
        var ex = Assert.Throws<PraxyException>(() => GitHubAppJwt.Create(options));
        Assert.Equal(ErrorTypes.VcsGithubNotConfigured, ex.Type);
    }

    [Fact]
    public void Create_produces_a_three_part_RS256_jwt_with_the_app_id_as_issuer_and_a_ten_minute_window()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var jwt = GitHubAppJwt.Create(Options(PrivateKeyPem), new FixedTimeProvider(now));

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());

        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        Assert.Equal("app-123", payload.RootElement.GetProperty("iss").GetString());

        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        // GitHub's documented recommendation: iat backdated ~1 minute to tolerate clock drift, exp
        // no more than 10 minutes out — this hits both edges of that window exactly.
        Assert.Equal(now.AddSeconds(-60).ToUnixTimeSeconds(), iat);
        Assert.Equal(now.AddMinutes(9).ToUnixTimeSeconds(), exp);
        Assert.Equal(600, exp - iat);
    }

    [Fact]
    public void Create_signs_with_a_signature_the_matching_public_key_verifies()
    {
        var jwt = GitHubAppJwt.Create(Options(PrivateKeyPem));
        var parts = jwt.Split('.');
        var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecodeBytes(parts[2]);

        Assert.True(Rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Create_rejects_tampering_with_either_half()
    {
        var jwt = GitHubAppJwt.Create(Options(PrivateKeyPem));
        var parts = jwt.Split('.');
        var signature = Base64UrlDecodeBytes(parts[2]);

        var tamperedInput = Encoding.UTF8.GetBytes($"{parts[0]}.eyJ0YW1wZXJlZCI6dHJ1ZX0");
        Assert.False(Rsa.VerifyData(tamperedInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Create_accepts_a_base64_encoded_private_key_too()
    {
        // The self-host-recommended path (docs/self-host.md): a single-line .env value can't carry
        // the PEM's real newlines cleanly, so VcsOptions.DecodePrivateKey tries base64 first.
        var base64Pem = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrivateKeyPem));
        var jwt = GitHubAppJwt.Create(Options(base64Pem));
        Assert.Equal(3, jwt.Split('.').Length);

        var parts = jwt.Split('.');
        var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecodeBytes(parts[2]);
        Assert.True(Rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static byte[] Base64UrlDecodeBytes(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlDecode(string value) => Encoding.UTF8.GetString(Base64UrlDecodeBytes(value));
}
