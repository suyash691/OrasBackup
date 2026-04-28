using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OrasBackup.Core.Crypto;

/// <summary>
/// AES-256-GCM streaming encryption. Processes data in fixed-size chunks,
/// each with its own nonce and auth tag. Memory = one chunk at a time.
///
/// Wire format:
///   [4-byte LE max chunk size]
///   Per chunk:
///     [4-byte LE ciphertext length]
///     [12-byte nonce]
///     [ciphertext]
///     [16-byte GCM tag]
///
/// Each chunk's length is explicit — no ambiguity at boundaries.
/// </summary>
public sealed class AesEncryptor : IEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int LenSize = 4; // per-chunk length prefix
    private readonly int _pbkdf2Iterations;
    private readonly int _chunkSize;

    public AesEncryptor(int pbkdf2Iterations = 600_000, int chunkSize = 64 * 1024 * 1024)
    {
        _pbkdf2Iterations = pbkdf2Iterations;
        _chunkSize = chunkSize;
    }

    public byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        ValidateKey(key);
        using var output = new MemoryStream();
        WriteHeader(output, _chunkSize);

        var offset = 0;
        while (offset < plaintext.Length)
        {
            var size = Math.Min(plaintext.Length - offset, _chunkSize);
            EncryptChunk(output, plaintext.AsSpan(offset, size), key);
            offset += size;
        }

        if (plaintext.Length == 0)
            EncryptChunk(output, ReadOnlySpan<byte>.Empty, key);

        return output.ToArray();
    }

    public byte[] Decrypt(byte[] data, byte[] key)
    {
        ValidateKey(key);
        if (data.Length < 4)
            throw new CryptographicException("Ciphertext too short");

        var maxChunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        if (maxChunkSize <= 0 || maxChunkSize > 256 * 1024 * 1024)
            throw new CryptographicException($"Invalid chunk size: {maxChunkSize}");

        using var output = new MemoryStream();
        var offset = 4;

        while (offset < data.Length)
        {
            if (data.Length - offset < LenSize)
                throw new CryptographicException("Truncated chunk length");

            var ciphertextLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, LenSize));
            offset += LenSize;

            if (ciphertextLen < 0 || ciphertextLen > maxChunkSize)
                throw new CryptographicException($"Invalid chunk ciphertext length: {ciphertextLen}");
            if (data.Length - offset < NonceSize + ciphertextLen + TagSize)
                throw new CryptographicException("Truncated chunk data");

            var nonce = data.AsSpan(offset, NonceSize); offset += NonceSize;
            var ciphertext = data.AsSpan(offset, ciphertextLen); offset += ciphertextLen;
            var tag = data.AsSpan(offset, TagSize); offset += TagSize;

            var plaintext = new byte[ciphertextLen];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            output.Write(plaintext);
        }

        return output.ToArray();
    }

    public string EncryptFile(string inputPath, byte[] key, CancellationToken ct = default)
    {
        ValidateKey(key);
        var outputPath = inputPath + ".enc";
        using var input = File.OpenRead(inputPath);
        using var output = File.Create(outputPath);
        WriteHeader(output, _chunkSize);

        var buffer = new byte[_chunkSize];
        int bytesRead;
        while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            EncryptChunk(output, buffer.AsSpan(0, bytesRead), key);
        }

        if (input.Length == 0)
            EncryptChunk(output, ReadOnlySpan<byte>.Empty, key);

        return outputPath;
    }

    public string DecryptFile(string inputPath, byte[] key, CancellationToken ct = default)
    {
        ValidateKey(key);
        var outputPath = inputPath + ".dec";
        using var input = File.OpenRead(inputPath);
        using var output = File.Create(outputPath);

        Span<byte> header = stackalloc byte[4];
        input.ReadExactly(header);
        var maxChunkSize = BinaryPrimitives.ReadInt32LittleEndian(header);

        Span<byte> lenBuf = stackalloc byte[LenSize];
        var nonceBuf = new byte[NonceSize];
        var tagBuf = new byte[TagSize];

        while (input.Position < input.Length)
        {
            ct.ThrowIfCancellationRequested();

            // Read explicit chunk length — no ambiguity
            input.ReadExactly(lenBuf);
            var ciphertextLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (ciphertextLen < 0 || ciphertextLen > maxChunkSize)
                throw new CryptographicException($"Invalid chunk ciphertext length: {ciphertextLen}");

            input.ReadExactly(nonceBuf);
            var ciphertext = new byte[ciphertextLen];
            if (ciphertextLen > 0) input.ReadExactly(ciphertext);
            input.ReadExactly(tagBuf);

            var plaintext = new byte[ciphertextLen];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonceBuf, ciphertext, tagBuf, plaintext);
            output.Write(plaintext);
        }

        return outputPath;
    }

    public byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, _pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

    public byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(16);

    private static void EncryptChunk(Stream output, ReadOnlySpan<byte> plaintext, byte[] key)
    {
        // Write length prefix
        Span<byte> lenBuf = stackalloc byte[LenSize];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, plaintext.Length);
        output.Write(lenBuf);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        output.Write(nonce);
        output.Write(ciphertext);
        output.Write(tag);
    }

    private static void WriteHeader(Stream output, int chunkSize)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, chunkSize);
        output.Write(header);
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes, got {key.Length}");
    }
}
