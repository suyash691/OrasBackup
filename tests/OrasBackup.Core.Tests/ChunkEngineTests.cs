using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Backup;
using Xunit;

namespace OrasBackup.Core.Tests;

public class ChunkEngineTests : IDisposable
{
    private readonly string _srcDir = Path.Combine(Path.GetTempPath(), $"chunk-test-{Guid.NewGuid():N}");
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly ChunkEngine _sut;

    public ChunkEngineTests()
    {
        Directory.CreateDirectory(_srcDir);
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _oras.UploadBlobFromFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrasLayerDescriptor(ci.ArgAt<string>(2), "sha256:blob", 100));
        _sut = new ChunkEngine(_oras, _encryptor, NullLogger<ChunkEngine>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_srcDir, true); } catch { } }

    [Fact]
    public async Task PushChunk_UploadsFilesAndPushesManifest()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_srcDir, "b.txt"), "world");

        var files = new List<FileSnapshot>
        {
            new("a.txt", "hash-a", 5, DateTime.UtcNow),
            new("b.txt", "hash-b", 5, DateTime.UtcNow)
        };
        var chunk = new FileChunk("docs", files);

        var result = await _sut.PushChunkAsync("reg/repo", chunk, [_srcDir], new byte[32], false);

        Assert.Equal("docs", result.Path);
        Assert.Equal(2, result.FileCount);
        Assert.StartsWith("chunk-", result.Tag);

        // 2 file blobs uploaded from file
        await _oras.Received(2).UploadBlobFromFileAsync("reg/repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // 1 PushManifestAsync with 1 in-memory layer (manifest) + 2 blob descriptors
        await _oras.Received(1).PushManifestAsync(Arg.Any<string>(),
            Arg.Is<IReadOnlyList<OrasLayer>>(l => l.Count == 1),
            Arg.Is<IReadOnlyList<OrasLayerDescriptor>>(l => l.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushChunk_EncryptsWhenEnabled()
    {
        File.WriteAllText(Path.Combine(_srcDir, "secret.txt"), "classified");

        _encryptor.EncryptFile(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var encPath = ci.ArgAt<string>(0) + ".enc";
            File.Copy(ci.ArgAt<string>(0), encPath, true);
            return encPath;
        });

        var files = new List<FileSnapshot> { new("secret.txt", "hash", 10, DateTime.UtcNow) };
        var chunk = new FileChunk("secrets", files);

        await _sut.PushChunkAsync("reg/repo", chunk, [_srcDir], new byte[32], encrypt: true);

        _encryptor.Received(1).EncryptFile(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        _encryptor.Received(1).Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()); // manifest
    }

    [Fact]
    public async Task PushChunk_DeterministicTag()
    {
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");

        var files = new List<FileSnapshot> { new("f.txt", "same-hash", 4, DateTime.UtcNow) };
        var chunk = new FileChunk("test", files);

        var r1 = await _sut.PushChunkAsync("reg/repo", chunk, [_srcDir], new byte[32], false);
        var r2 = await _sut.PushChunkAsync("reg/repo", chunk, [_srcDir], new byte[32], false);

        Assert.Equal(r1.Tag, r2.Tag);
        Assert.Equal(r1.ContentHash, r2.ContentHash);
    }

    [Fact]
    public async Task PushChunk_FileNotFound_Throws()
    {
        var files = new List<FileSnapshot> { new("nonexistent.txt", "hash", 10, DateTime.UtcNow) };
        var chunk = new FileChunk("test", files);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _sut.PushChunkAsync("reg/repo", chunk, [_srcDir], new byte[32], false));
    }

    [Fact]
    public async Task PushChunk_FileChangedDuringBackup_RehashesAndSucceeds()
    {
        var filePath = Path.Combine(_srcDir, "changing.txt");
        File.WriteAllText(filePath, "original");
        var originalHash = "stale-hash-from-scan"; // simulate stale hash from scan

        var files = new List<FileSnapshot> { new("changing.txt", originalHash, 8, DateTime.UtcNow) };
        var chunk = new FileChunk("test", files);

        var result = await _sut.PushChunkAsync("reg/repo", chunk, [_srcDir], new byte[32], false);

        // Should succeed — re-hashes the file and uses the correct hash
        Assert.NotNull(result);
        Assert.StartsWith("chunk-", result.Tag);
    }
}
