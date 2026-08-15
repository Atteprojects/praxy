using Praxy.Auth;

namespace Praxy.Tests.Unit;

public class Argon2PasswordHasherTests
{
    // Small parameters: these tests exercise correctness, not KDF strength.
    private static readonly Argon2PasswordHasher Hasher = new(new Argon2Options
    {
        MemoryKib = 1024,
        Iterations = 1,
        Parallelism = 1,
    });

    [Fact]
    public void Hash_then_verify_round_trips()
    {
        var phc = Hasher.Hash("correct horse battery staple");
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", phc);
        Assert.True(Hasher.Verify("correct horse battery staple", phc));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var phc = Hasher.Hash("password-one");
        Assert.False(Hasher.Verify("password-two", phc));
    }

    [Fact]
    public void Same_password_hashes_differently_each_time()
    {
        Assert.NotEqual(Hasher.Hash("pw123456"), Hasher.Hash("pw123456"));
    }

    [Fact]
    public void Verify_honors_parameters_embedded_in_the_phc_string()
    {
        // A hash produced under different parameters still verifies — required for
        // re-hash-on-upgrade when the configured parameters change.
        var other = new Argon2PasswordHasher(new Argon2Options { MemoryKib = 2048, Iterations = 2, Parallelism = 1 });
        var phc = other.Hash("migrating-password");
        Assert.True(Hasher.Verify("migrating-password", phc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-phc-string")]
    [InlineData("$argon2id$v=19$m=abc,t=1,p=1$AAAA$BBBB")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$AAAA$BBBB")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$!notb64!$BBBB")]
    [InlineData("$argon2id$v=19$m=0,t=0,p=0$AAAA$BBBB")]
    public void Malformed_phc_strings_fail_closed(string phc)
    {
        Assert.False(Hasher.Verify("whatever", phc));
    }
}
