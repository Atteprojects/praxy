namespace Praxy.Auth;

/// <summary>
/// Seam over the password KDF so the implementation (Konscious Argon2id today) can be
/// swapped in one file. Hashes are PHC strings, self-describing enough to verify and
/// re-hash-on-upgrade after parameter changes.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string phcHash);
}
