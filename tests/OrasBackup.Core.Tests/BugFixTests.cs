using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Core.Tests;

/// <summary>
/// Tests for the "latest" tag being pushed on each backup so restore-without-id works.
/// </summary>
public class LatestTagTests
{
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();

    public LatestTagTests()
    {
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
    }

    [Fact]
    public async Task Backup_PushesLatestTag()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"orastest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "f.txt"), "data");
            var profile = new BackupProfile
            {
                Name = "test",
                SourcePaths = [tempDir],
                Registry = "registry.example.com/test",
                Encryption = new EncryptionConfig { Enabled = false }
            };
            var engine = new BackupEngine(new DeltaTracker(), _encryptor, _oras, NullLogger<BackupEngine>.Instance);
            var result = await engine.RunBackupAsync(profile, new byte[32], null);
            Assert.True(result.Success);

            // Should have pushed twice: once with backup ID tag, once with "latest" tag
            await _oras.Received(2).PushAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OrasLayer>>(),
                Arg.Any<CancellationToken>());

            await _oras.Received(1).PushAsync(
                Arg.Is<string>(r => r.EndsWith(":latest")),
                Arg.Any<IReadOnlyList<OrasLayer>>(),
                Arg.Any<CancellationToken>());
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }
}

/// <summary>
/// Tests that multi-source-path backup scans all paths into one file list before computing delta.
/// </summary>
public class MultiSourceDeltaTests : IDisposable
{
    private readonly string _srcA = Path.Combine(Path.GetTempPath(), $"orastest-srcA-{Guid.NewGuid():N}");
    private readonly string _srcB = Path.Combine(Path.GetTempPath(), $"orastest-srcB-{Guid.NewGuid():N}");
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();

    public MultiSourceDeltaTests()
    {
        Directory.CreateDirectory(_srcA);
        Directory.CreateDirectory(_srcB);
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
    }

    public void Dispose()
    {
        try { Directory.Delete(_srcA, true); } catch { }
        try { Directory.Delete(_srcB, true); } catch { }
    }

    [Fact]
    public async Task MultiSource_IncrementalDoesNotSpuriouslyDeleteCrossPathFiles()
    {
        // Setup: two source paths, each with one file
        File.WriteAllText(Path.Combine(_srcA, "a.txt"), "from A");
        File.WriteAllText(Path.Combine(_srcB, "b.txt"), "from B");

        var profile = new BackupProfile
        {
            Name = "multi",
            SourcePaths = [_srcA, _srcB],
            Registry = "registry.example.com/test",
            Encryption = new EncryptionConfig { Enabled = false }
        };

        var engine = new BackupEngine(new DeltaTracker(), _encryptor, _oras, NullLogger<BackupEngine>.Instance);

        // First backup (full)
        var first = await engine.RunBackupAsync(profile, new byte[32], null);
        Assert.True(first.Success);
        Assert.Equal(2, first.FilesAdded);

        // Build previous manifest from the first backup's state
        var tracker = new DeltaTracker();
        var allFiles = new List<FileSnapshot>();
        foreach (var src in profile.SourcePaths)
            allFiles.AddRange(tracker.ScanDirectory(src, profile.ExcludePatterns));
        var prevManifest = new DeltaManifest { BackupId = first.BackupId, Files = allFiles };

        // Second backup — nothing changed
        var second = await engine.RunBackupAsync(profile, new byte[32], prevManifest);
        Assert.True(second.Success);

        // BUG: without fix, files from srcB appear as "deleted" when scanning srcA
        Assert.Equal(0, second.FilesDeleted);
        Assert.Equal(0, second.FilesAdded);
        Assert.Equal(0, second.FilesModified);
        Assert.Equal(2, second.FilesUnchanged);
    }
}
