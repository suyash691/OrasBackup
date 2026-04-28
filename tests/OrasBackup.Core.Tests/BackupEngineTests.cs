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
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"orastest-{Guid.NewGuid():N}");
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly BackupEngine _sut;
    private readonly byte[] _key = new byte[32];

    public BackupEngineTests()
    {
        Directory.CreateDirectory(_tempDir);
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _sut = new BackupEngine(new DeltaTracker(), _encryptor, _oras, NullLogger<BackupEngine>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task RunBackup_PushesLayersToRegistry()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "hello");
        var profile = MakeProfile();

        var result = await _sut.RunBackupAsync(profile, _key, null);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesAdded);
        Assert.Equal(0, result.FilesDeleted);
        await _oras.Received(1).PushAsync(
            Arg.Is<string>(r => r.StartsWith(profile.Registry + ":")),
            Arg.Is<IReadOnlyList<OrasLayer>>(l => l.Count == 2), // manifest + data
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackup_NoChanges_PushesManifestOnly()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "hello");
        var profile = MakeProfile();

        // First backup
        var first = await _sut.RunBackupAsync(profile, _key, null);
        // Build a manifest matching current state
        var tracker = new DeltaTracker();
        var prev = new DeltaManifest
        {
            BackupId = first.BackupId,
            Files = tracker.ScanDirectory(_tempDir, profile.ExcludePatterns)
        };

        // Second backup — nothing changed
        var result = await _sut.RunBackupAsync(profile, _key, prev);

        Assert.True(result.Success);
        Assert.Equal(0, result.FilesAdded);
        Assert.Equal(0, result.FilesModified);
    }

    [Fact]
    public async Task RunBackup_WithEncryption_CallsEncryptor()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "data");
        var profile = MakeProfile();

        await _sut.RunBackupAsync(profile, _key, null);

        _encryptor.Received().Encrypt(Arg.Any<byte[]>(), _key);
    }

    [Fact]
    public async Task RunBackup_EmptyDirectory_Succeeds()
    {
        var profile = MakeProfile();

        var result = await _sut.RunBackupAsync(profile, _key, null);

        Assert.True(result.Success);
        Assert.Equal(0, result.FilesAdded);
    }

    private BackupProfile MakeProfile() => new()
    {
        Name = "test",
        SourcePaths = [_tempDir],
        Registry = "registry.example.com/test/backups/test",
        Encryption = new EncryptionConfig { Enabled = true }
    };
}
