using System.Security.Cryptography;

namespace OrasBackup.Core.Crypto;

/// <summary>
/// AES-256-GCM encryption. Wire format: [12-byte nonce][ciphertext][16-byte tag].
/// </summary>
public sealed class AesEncryptor : IEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // 256 bits
    private readonly int _pbkdf2Iterations;

    public AesEncryptor(int pbkdf2Iterations = 600_000) => _pbkdf2Iterations = pbkdf2Iterations;

    public byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        ValidateKey(key);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // [nonce][ciphertext][tag]
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);
        return result;
    }

    public byte[] Decrypt(byte[] data, byte[] key)
    {
        ValidateKey(key);
        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short");

        var nonce = data.AsSpan(0, NonceSize);
        var ciphertext = data.AsSpan(NonceSize, data.Length - NonceSize - TagSize);
        var tag = data.AsSpan(data.Length - TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, _pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

    public byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(16);

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes, got {key.Length}");
    }
}
