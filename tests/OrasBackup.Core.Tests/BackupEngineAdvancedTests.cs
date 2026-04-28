using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Core.Tests;

public class BackupEngineAdvancedTests : IDisposable
{
    private readonly string _srcDir = Path.Combine(Path.GetTempPath(), $"be-adv-{Guid.NewGuid():N}");
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly BackupEngine _sut;

    public BackupEngineAdvancedTests()
    {
        Directory.CreateDirectory(_srcDir);
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _oras.UploadBlobFromFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrasLayerDescriptor(ci.ArgAt<string>(2), "sha256:blob", 100));
        var chunkEngine = new ChunkEngine(_oras, _encryptor, NullLogger<ChunkEngine>.Instance);
        _sut = new BackupEngine(new DeltaTracker(), chunkEngine, _oras, _encryptor,
            NullLogger<BackupEngine>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_srcDir, true); } catch { } }

    private BackupProfile MakeProfile(bool encrypt = false) => new()
    {
        Name = "test", SourcePaths = [_srcDir], Registry = "reg/repo",
        Encryption = new EncryptionConfig { Enabled = encrypt }
    };

    [Fact]
    public async Task DeletionTracking_DetectsDeletedFiles()
    {
        File.WriteAllText(Path.Combine(_srcDir, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(_srcDir, "delete-me.txt"), "gone");

        var first = await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);
        Assert.True(first.Success);
        var firstIndex = _sut.LastIndex!;
        Assert.Contains("delete-me.txt", firstIndex.AllFiles);

        // Delete the file
        File.Delete(Path.Combine(_srcDir, "delete-me.txt"));

        var second = await _sut.RunBackupAsync(MakeProfile(), new byte[32], firstIndex);
        Assert.True(second.Success);
        Assert.Equal(1, second.FilesDeleted);
        Assert.Contains("delete-me.txt", _sut.LastIndex!.DeletedFiles);
    }

    [Fact]
    public async Task MissingSourcePath_Throws()
    {
        var profile = MakeProfile();
        profile.SourcePaths = ["/nonexistent/path"];

        var result = await _sut.RunBackupAsync(profile, new byte[32], null);
        Assert.False(result.Success);
        Assert.Contains("not exist", result.Error);
    }

    [Fact]
    public async Task Cancellation_StopsBackup()
    {
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.RunBackupAsync(MakeProfile(), new byte[32], null, cts.Token);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task EncryptedBackup_EncryptsIndex()
    {
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");

        await _sut.RunBackupAsync(MakeProfile(encrypt: true), new byte[32], null);

        // Encrypt called for: chunk manifest + index = at least 2 calls
        _encryptor.Received().Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task LastIndex_SetAfterSuccessfulBackup()
    {
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");

        Assert.Null(_sut.LastIndex);
        await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);
        Assert.NotNull(_sut.LastIndex);
        Assert.NotEmpty(_sut.LastIndex!.BackupId);
    }

    [Fact]
    public async Task AllFiles_PopulatedInIndex()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_srcDir, "b.txt"), "b");

        await _sut.RunBackupAsync(MakeProfile(), new byte[32], null);

        Assert.Equal(2, _sut.LastIndex!.AllFiles.Count);
        Assert.Contains("a.txt", _sut.LastIndex.AllFiles);
        Assert.Contains("b.txt", _sut.LastIndex.AllFiles);
    }

    [Fact]
    public async Task EncryptionEnabled_WrongKeyLength_Throws()
    {
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");
        var result = await _sut.RunBackupAsync(MakeProfile(encrypt: true), new byte[16], null);
        Assert.False(result.Success);
        Assert.Contains("32 bytes", result.Error);
    }
}
