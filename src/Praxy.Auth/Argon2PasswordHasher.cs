using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Praxy.Auth;

public sealed class Argon2Options
{
    /// <summary>OWASP baseline: m=19456 KiB (19 MiB), t=2, p=1. Memory cost is per concurrent hash.</summary>
    public int MemoryKib { get; set; } = 19456;

    public int Iterations { get; set; } = 2;
    public int Parallelism { get; set; } = 1;
    public int SaltBytes { get; set; } = 16;
    public int HashBytes { get; set; } = 32;
}

/// <summary>
/// Argon2id via Konscious (pure managed — no native dependency, which matters for self-host).
/// Konscious produces raw bytes only, so the PHC string
/// <c>$argon2id$v=19$m=..,t=..,p=..$salt$hash</c> is assembled and parsed here.
/// </summary>
public sealed class Argon2PasswordHasher(Argon2Options options) : IPasswordHasher
{
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(options.SaltBytes);
        var hash = Derive(password, salt, options.MemoryKib, options.Iterations, options.Parallelism, options.HashBytes);
        return $"$argon2id$v=19$m={options.MemoryKib},t={options.Iterations},p={options.Parallelism}" +
               $"${B64(salt)}${B64(hash)}";
    }

    public bool Verify(string password, string phcHash)
    {
        // PHC: $argon2id$v=19$m=<m>,t=<t>,p=<p>$<salt>$<hash>
        var parts = phcHash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19")
            return false;

        int m = 0, t = 0, p = 0;
        foreach (var kv in parts[2].Split(','))
        {
            var eq = kv.IndexOf('=');
            if (eq < 1 || !int.TryParse(kv[(eq + 1)..], out var val))
                return false;
            switch (kv[..eq])
            {
                case "m": m = val; break;
                case "t": t = val; break;
                case "p": p = val; break;
                default: return false;
            }
        }
        if (m <= 0 || t <= 0 || p <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = UnB64(parts[3]);
            expected = UnB64(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int hashBytes)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(hashBytes);
    }

    // PHC uses unpadded standard base64.
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] UnB64(string s) =>
        Convert.FromBase64String(s.Length % 4 == 0 ? s : s + new string('=', 4 - s.Length % 4));
}
