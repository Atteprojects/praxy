using Praxy.Auth;
using Praxy.Core;

namespace Praxy.Tests.Unit;

public class SessionTokensTests
{
    [Fact]
    public void Create_then_parse_round_trips()
    {
        var sessionId = Ids.NewUuid();
        var (token, storedHash) = SessionTokens.Create(sessionId);

        Assert.True(SessionTokens.TryParse(token, out var parsedId, out var presentedHash));
        Assert.Equal(sessionId, parsedId);
        Assert.True(SessionTokens.HashEquals(storedHash, presentedHash));
    }

    [Fact]
    public void Tampered_secret_produces_a_different_hash()
    {
        var (token, storedHash) = SessionTokens.Create(Ids.NewUuid());
        var tampered = token[..^4] + (token.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");

        Assert.True(SessionTokens.TryParse(tampered, out _, out var presentedHash));
        Assert.False(SessionTokens.HashEquals(storedHash, presentedHash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-dot-here")]
    [InlineData(".secretonly")]
    [InlineData("notauuid.c2VjcmV0")]
    [InlineData("0198c5cd8e7c7d5c8f8e000000000000.")]
    [InlineData("0198c5cd8e7c7d5c8f8e000000000000.!!!not-base64url@@@")]
    public void Malformed_tokens_fail_closed(string token)
    {
        Assert.False(SessionTokens.TryParse(token, out _, out _));
    }

    [Fact]
    public void Token_is_id_dot_base64url_with_no_padding()
    {
        var (token, _) = SessionTokens.Create(Ids.NewUuid());
        var parts = token.Split('.');
        Assert.Equal(2, parts.Length);
        Assert.Equal(32, parts[0].Length);
        Assert.DoesNotContain('=', parts[1]);
        Assert.DoesNotContain('+', parts[1]);
        Assert.DoesNotContain('/', parts[1]);
    }
}
