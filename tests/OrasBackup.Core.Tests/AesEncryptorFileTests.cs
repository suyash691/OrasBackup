using OrasBackup.Core.Crypto;
using Xunit;

namespace OrasBackup.Core.Tests;

public class AesEncryptorFileTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enc-test-{Guid.NewGuid():N}");
    private readonly AesEncryptor _sut;
    private readonly byte[] _key;

    public AesEncryptorFileTests()
    {
        _sut = new AesEncryptor(pbkdf2Iterations: 1000, chunkSize: 64); // tiny chunks for testing
        var salt = _sut.GenerateSalt();
        _key = _sut.DeriveKey("test", salt);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public void EncryptFile_DecryptFile_RoundTrip()
    {
        var src = Path.Combine(_tempDir, "plain.bin");
        File.WriteAllText(src, "hello world");

        var encPath = _sut.EncryptFile(src, _key);
        Assert.True(File.Exists(encPath));
        Assert.NotEqual(File.ReadAllBytes(src), File.ReadAllBytes(encPath));

        var decPath = _sut.DecryptFile(encPath, _key);
        Assert.Equal("hello world", File.ReadAllText(decPath));

        File.Delete(encPath);
        File.Delete(decPath);
    }

    [Fact]
    public void EncryptFile_MultiChunk_RoundTrip()
    {
        // chunkSize=64, so 200 bytes = 4 chunks (64+64+64+8)
        var src = Path.Combine(_tempDir, "big.bin");
        var data = new byte[200];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(src, data);

        var encPath = _sut.EncryptFile(src, _key);
        var decPath = _sut.DecryptFile(encPath, _key);

        Assert.Equal(data, File.ReadAllBytes(decPath));

        File.Delete(encPath);
        File.Delete(decPath);
    }

    [Fact]
    public void EncryptFile_EmptyFile_RoundTrip()
    {
        var src = Path.Combine(_tempDir, "empty.bin");
        File.WriteAllBytes(src, []);

        var encPath = _sut.EncryptFile(src, _key);
        var decPath = _sut.DecryptFile(encPath, _key);

        Assert.Empty(File.ReadAllBytes(decPath));

        File.Delete(encPath);
        File.Delete(decPath);
    }

    [Fact]
    public void Encrypt_Decrypt_MultiChunk_InMemory()
    {
        var data = new byte[200];
        Random.Shared.NextBytes(data);

        var encrypted = _sut.Encrypt(data, _key);
        var decrypted = _sut.Decrypt(encrypted, _key);

        Assert.Equal(data, decrypted);
    }

    [Fact]
    public void EncryptFile_Cancellation_Throws()
    {
        var src = Path.Combine(_tempDir, "cancel.bin");
        File.WriteAllBytes(src, new byte[200]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _sut.EncryptFile(src, _key, cts.Token));
    }

    [Fact]
    public void EncryptFile_WrongKey_DecryptFails()
    {
        var src = Path.Combine(_tempDir, "secret.bin");
        File.WriteAllText(src, "secret");

        var encPath = _sut.EncryptFile(src, _key);
        var wrongKey = _sut.DeriveKey("wrong", _sut.GenerateSalt());

        Assert.ThrowsAny<Exception>(() => _sut.DecryptFile(encPath, wrongKey));

        File.Delete(encPath);
    }

    [Fact]
    public void EncryptFile_CrossCompatible_WithInMemory()
    {
        // File-encrypted data should be decryptable by in-memory Decrypt and vice versa
        var src = Path.Combine(_tempDir, "cross.bin");
        var data = new byte[100];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(src, data);

        var encPath = _sut.EncryptFile(src, _key);
        var encBytes = File.ReadAllBytes(encPath);
        var decrypted = _sut.Decrypt(encBytes, _key);
        Assert.Equal(data, decrypted);

        File.Delete(encPath);
    }
}
