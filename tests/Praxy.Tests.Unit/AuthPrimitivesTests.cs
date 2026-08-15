using System.Text.Json.Nodes;
using Praxy.Auth;

namespace Praxy.Tests.Unit;

public class SecretsTests
{
    [Fact]
    public void Generated_secret_verifies_against_its_hash()
    {
        var (secret, hash) = Secrets.Generate();
        Assert.True(Secrets.HashEquals(hash, Secrets.Hash(secret)));
    }

    [Fact]
    public void Different_secret_does_not_verify()
    {
        var (_, hash) = Secrets.Generate();
        var (other, _) = Secrets.Generate();
        Assert.False(Secrets.HashEquals(hash, Secrets.Hash(other)));
    }

    [Fact]
    public void Null_or_mismatched_length_hashes_are_rejected_not_thrown()
    {
        Assert.False(Secrets.HashEquals(null, Secrets.Hash("x")));
        Assert.False(Secrets.HashEquals("abcd", Secrets.Hash("x")));
    }
}

public class CompactJwtTests
{
    private static readonly byte[] Key = new InstanceKey("test-key").SigningKey;

    [Fact]
    public void Roundtrip_preserves_claims()
    {
        var token = CompactJwt.Encode(Key, new JsonObject { ["secret"] = "s3cret", ["provider"] = "google" },
            TimeSpan.FromMinutes(1));
        var claims = CompactJwt.Decode(Key, token);
        Assert.NotNull(claims);
        Assert.Equal("s3cret", claims["secret"]!.GetValue<string>());
        Assert.Equal("google", claims["provider"]!.GetValue<string>());
    }

    [Fact]
    public void Expired_token_decodes_to_null()
    {
        var token = CompactJwt.Encode(Key, [], TimeSpan.FromSeconds(-5));
        Assert.Null(CompactJwt.Decode(Key, token));
    }

    [Fact]
    public void Wrong_key_decodes_to_null()
    {
        var token = CompactJwt.Encode(Key, new JsonObject { ["a"] = 1 }, TimeSpan.FromMinutes(1));
        Assert.Null(CompactJwt.Decode(new InstanceKey("other-key").SigningKey, token));
    }

    [Fact]
    public void Tampered_payload_decodes_to_null()
    {
        var token = CompactJwt.Encode(Key, new JsonObject { ["a"] = 1 }, TimeSpan.FromMinutes(1));
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{Secrets.Base64Url("{\"a\":2,\"exp\":9999999999}"u8.ToArray())}.{parts[2]}";
        Assert.Null(CompactJwt.Decode(Key, tampered));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("!!!.###.$$$")]
    public void Garbage_decodes_to_null(string garbage)
    {
        Assert.Null(CompactJwt.Decode(Key, garbage));
    }
}

public class InstanceKeyTests
{
    [Fact]
    public void Encrypt_decrypt_roundtrips()
    {
        var key = new InstanceKey("configured-secret");
        Assert.False(key.Ephemeral);
        Assert.Equal("gho_token_value", key.Decrypt(key.Encrypt("gho_token_value")));
    }

    [Fact]
    public void Same_config_produces_interoperable_keys_across_restarts()
    {
        var first = new InstanceKey("configured-secret");
        var second = new InstanceKey("configured-secret");
        Assert.Equal("hello", second.Decrypt(first.Encrypt("hello")));
    }

    [Fact]
    public void Wrong_key_or_garbage_decrypts_to_null()
    {
        var key = new InstanceKey("a");
        var other = new InstanceKey("b");
        Assert.Null(other.Decrypt(key.Encrypt("hello")));
        Assert.Null(key.Decrypt("not-base64url!!"));
        Assert.Null(key.Decrypt(Secrets.Base64Url([1, 2, 3])));
    }

    [Fact]
    public void Unconfigured_key_is_ephemeral_and_random()
    {
        var key = new InstanceKey(null);
        Assert.True(key.Ephemeral);
        Assert.Null(new InstanceKey(null).Decrypt(key.Encrypt("x")));
    }
}
