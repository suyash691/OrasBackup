using System.Security.Cryptography;
using OrasBackup.Core.Crypto;

namespace OrasBackup.Core.Tests;

public class AesEncryptorTests
{
    private readonly AesEncryptor _sut = new(iterations: 10_000); // low iterations for test speed
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Encrypt_Decrypt_RoundTrip()
    {
        var plaintext = "hello world"u8.ToArray();
        var encrypted = _sut.Encrypt(plaintext, _key);
        var decrypted = _sut.Decrypt(encrypted, _key);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var plaintext = "same input"u8.ToArray();
        var a = _sut.Encrypt(plaintext, _key);
        var b = _sut.Encrypt(plaintext, _key);
        Assert.NotEqual(a, b); // unique nonce each time
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        var plaintext = "secret"u8.ToArray();
        var encrypted = _sut.Encrypt(plaintext, _key);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        Assert.ThrowsAny<CryptographicException>(() => _sut.Decrypt(encrypted, wrongKey));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var plaintext = "tamper test"u8.ToArray();
        var encrypted = _sut.Encrypt(plaintext, _key);
        encrypted[20] ^= 0xFF; // flip a byte
        Assert.ThrowsAny<CryptographicException>(() => _sut.Decrypt(encrypted, _key));
    }

    [Fact]
    public void Decrypt_TooShort_Throws()
    {
        Assert.ThrowsAny<CryptographicException>(() => _sut.Decrypt(new byte[10], _key));
    }

    [Fact]
    public void Encrypt_InvalidKeyLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => _sut.Encrypt([1, 2, 3], new byte[16]));
    }

    [Fact]
    public void DeriveKey_ProducesConsistentOutput()
    {
        var salt = _sut.GenerateSalt();
        var key1 = _sut.DeriveKey("password", salt);
        var key2 = _sut.DeriveKey("password", salt);
        Assert.Equal(key1, key2);
        Assert.Equal(32, key1.Length);
    }

    [Fact]
    public void DeriveKey_DifferentPasswords_DifferentKeys()
    {
        var salt = _sut.GenerateSalt();
        var key1 = _sut.DeriveKey("password1", salt);
        var key2 = _sut.DeriveKey("password2", salt);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Encrypt_EmptyPlaintext_RoundTrips()
    {
        var plaintext = Array.Empty<byte>();
        var encrypted = _sut.Encrypt(plaintext, _key);
        var decrypted = _sut.Decrypt(encrypted, _key);
        Assert.Equal(plaintext, decrypted);
    }
}
