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
    private readonly string _srcDir = Path.Combine(Path.GetTempPath(), $"be-test-{Guid.NewGuid():N}");
    private readonly IDeltaTracker _delta = Substitute.For<IDeltaTracker>();
    private readonly IChunkEngine _chunk = Substitute.For<IChunkEngine>();
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly BackupEngine _sut;

    public BackupEngineTests()
    {
        Directory.CreateDirectory(_srcDir);
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _delta.ScanDirectory(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<FileSnapshot>?>())
            .Returns(ci =>
            {
                // Return real files from the source dir
                var dir = ci.ArgAt<string>(0);
                return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(f => new FileSnapshot(
                        Path.GetRelativePath(dir, f).Replace('\\', '/'),
                        "hash-" + Path.GetFileName(f), new FileInfo(f).Length, DateTime.UtcNow))
                    .ToList();
            });
        _chunk.PushChunkAsync(Arg.Any<string>(), Arg.Any<FileChunk>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var chunk = ci.ArgAt<FileChunk>(1);
                return new ChunkRef { Path = chunk.Path, Tag = "chunk-abc",
                    ContentHash = ChunkEngine.ComputeChunkHash(chunk.Files),
                    FileCount = chunk.Files.Count, TotalBytes = chunk.TotalBytes };
            });

        _sut = new BackupEngine(_delta, _chunk, _oras, _encryptor, NullLogger<BackupEngine>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_srcDir, true); } catch { } }

    private BackupProfile MakeProfile(bool encrypt = false) => new()
    {
        Name = "test", SourcePaths = [_srcDir], Registry = "reg/repo",
        Encryption = new EncryptionConfig { Enabled = encrypt }
    };

    [Fact]
    public async Task Backup_CallsChunkEngineAndPushesIndex()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello");

        var result = await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.FilesAdded);
        await _chunk.Received().PushChunkAsync("reg/repo", Arg.Any<FileChunk>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
        await _oras.Received().PushAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<OrasLayer>>(), Arg.Any<CancellationToken>());
        await _oras.Received().TagAsync("reg/repo", Arg.Any<string>(), "latest", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backup_SkipsUnchangedChunks()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello");
        var first = await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);
        Assert.True(first.Success);

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

    [Fact]
    public async Task Backup_EncryptedIndex_CallsEncrypt()
    {
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");
        await _sut.RunBackupAsync(MakeProfile(encrypt: true), new byte[32], null);
        _encryptor.Received().Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task Backup_WrongKeyLength_Fails()
    {
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");
        var result = await _sut.RunBackupAsync(MakeProfile(encrypt: true), new byte[16], null);
        Assert.False(result.Success);
        Assert.Contains("32 bytes", result.Error);
    }

    [Fact]
    public async Task Backup_DeletionTracking()
    {
        File.WriteAllText(Path.Combine(_srcDir, "keep.txt"), "keep");
        var first = await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);
        Assert.Contains("keep.txt", _sut.LastIndex!.AllFiles);

        // Remove file, scan returns empty
        File.Delete(Path.Combine(_srcDir, "keep.txt"));
        var second = await _sut.RunBackupAsync(MakeProfile(), new byte[32], _sut.LastIndex);
        Assert.Equal(1, second.FilesDeleted);
        Assert.Contains("keep.txt", _sut.LastIndex!.DeletedFiles);
    }

    [Fact]
    public async Task Backup_MissingSourcePath_Fails()
    {
        var profile = MakeProfile();
        profile.SourcePaths = ["/nonexistent/path"];
        _delta.ScanDirectory("/nonexistent/path", Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<FileSnapshot>?>())
            .Returns(_ => throw new DirectoryNotFoundException("not found"));

        var result = await _sut.RunBackupAsync(profile, new byte[32], null);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Backup_SetsLastIndex()
    {
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");
        Assert.Null(_sut.LastIndex);
        await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);
        Assert.NotNull(_sut.LastIndex);
    }
}
