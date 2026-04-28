using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Core.Tests;

public class BackupEngineTests : IDisposable
{
    private readonly string _srcDir = Path.Combine(Path.GetTempPath(), $"v2test-{Guid.NewGuid():N}");
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly BackupEngine _sut;

    public BackupEngineTests()
    {
        Directory.CreateDirectory(_srcDir);
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _oras.UploadBlobFromFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrasLayerDescriptor(ci.ArgAt<string>(2), "sha256:blob", 100));

        var chunkEngine = new ChunkEngine(_oras, _encryptor, NullLogger<ChunkEngine>.Instance);
        _sut = new BackupEngine(new DeltaTracker(), chunkEngine, _oras, _encryptor,
            NullLogger<BackupEngine>.Instance, new DirectoryChunker(maxChunkBytes: 1_000_000, minChunkBytes: 100));
    }

    public void Dispose() { try { Directory.Delete(_srcDir, true); } catch { } }

    private BackupProfile MakeProfile(bool encrypt = false) => new()
    {
        Name = "v2test", SourcePaths = [_srcDir], Registry = "reg/repo",
        Encryption = new EncryptionConfig { Enabled = encrypt }
    };

    [Fact]
    public async Task Backup_PushesChunksAndIndex()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_srcDir, "b.txt"), "world");

        var result = await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FilesAdded);
        Assert.NotNull(_sut.LastIndex);
        Assert.NotEmpty(_sut.LastIndex!.Chunks);

        // At least 2 pushes: chunk image + root index
        await _oras.Received().PushAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<OrasLayer>>(), Arg.Any<CancellationToken>());
        await _oras.Received().TagAsync("reg/repo", Arg.Any<string>(), "latest", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backup_SkipsUnchangedChunks()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello");

        var first = await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);
        Assert.True(first.Success);

        // Second backup with same files — chunks should be skipped
        var second = await _sut.RunBackupAsync(MakeProfile(), new byte[32], _sut.LastIndex);
        Assert.True(second.Success);
        Assert.Equal(0, second.FilesAdded);
        Assert.Equal(1, second.FilesUnchanged);
    }

    [Fact]
    public async Task Backup_EmptyDirectory_Succeeds()
    {
        var result = await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);
        Assert.True(result.Success);
        Assert.Equal(0, result.FilesAdded);
    }
}
