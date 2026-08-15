using System.Security.Cryptography;
using System.Text;

namespace Praxy.Auth;

/// <summary>
/// The instance-wide secret key: signs the short-lived OAuth JWTs and encrypts provider access
/// tokens at rest (AES-256-GCM). Sourced from <c>PRAXY_SECRET_KEY</c>; when unset, an ephemeral
/// key is generated so single-node dev still works — with a loud warning, because encrypted
/// values and in-flight OAuth logins then die with the process.
/// </summary>
public sealed class InstanceKey
{
    private readonly byte[] _key;

    public InstanceKey(string? configuredSecret)
    {
        Ephemeral = string.IsNullOrWhiteSpace(configuredSecret);
        // Any string works as configuration; SHA-256 normalizes it to exactly 32 key bytes.
        _key = Ephemeral
            ? RandomNumberGenerator.GetBytes(32)
            : SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret!));
    }

    public bool Ephemeral { get; }

    public byte[] SigningKey => _key;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>AES-256-GCM; output is base64url(nonce || ciphertext || tag).</summary>
    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var packed = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(packed, 0);
        cipher.CopyTo(packed, NonceSize);
        tag.CopyTo(packed, NonceSize + cipher.Length);
        return Secrets.Base64Url(packed);
    }

    /// <summary>Null when the payload is malformed or was encrypted under a different key.</summary>
    public string? Decrypt(string encrypted)
    {
        byte[] packed;
        try
        {
            packed = Secrets.FromBase64Url(encrypted);
        }
        catch (FormatException)
        {
            return null;
        }
        if (packed.Length < NonceSize + TagSize)
            return null;

        var nonce = packed.AsSpan(0, NonceSize);
        var cipher = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
        var tag = packed.AsSpan(packed.Length - TagSize);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        return Encoding.UTF8.GetString(plain);
    }
}
