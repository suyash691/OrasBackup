namespace OrasBackup.Core.Crypto;

public interface IEncryptor
{
    byte[] Encrypt(byte[] plaintext, byte[] key);
    byte[] Decrypt(byte[] ciphertext, byte[] key);
    byte[] DeriveKey(string password, byte[] salt);
    byte[] GenerateSalt();
}
