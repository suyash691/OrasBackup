namespace OrasBackup.Core.Crypto;

public interface IEncryptor
{
    byte[] Encrypt(byte[] plaintext, byte[] key);
    byte[] Decrypt(byte[] ciphertext, byte[] key);
    byte[] DeriveKey(string password, byte[] salt);
    byte[] GenerateSalt();

    /// <summary>Encrypt a file to a temp file, streaming in chunks. Returns encrypted file path.</summary>
    string EncryptFile(string inputPath, byte[] key, CancellationToken ct = default);

    /// <summary>Decrypt a file to a temp file, streaming in chunks. Returns decrypted file path.</summary>
    string DecryptFile(string inputPath, byte[] key, CancellationToken ct = default);
}
