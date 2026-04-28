using OrasBackup.Core.Backup;
using Xunit;

namespace OrasBackup.Integration.Tests;

[Trait("Category", "Integration")]
public class FullSyncTests : IntegrationFixture
{
    [Fact]
    public async Task FullBackup_ThenRestore_AllFilesMatch()
    {
        EnsureRegistry();

        WriteFile("hello.txt", "world");
        WriteFile("sub/data.bin", "binary content");

        var profile = MakeProfile();
        var result = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FilesAdded);
        TrackBackup(result.BackupId);

        var opts = new RestoreOptions(Registry, result.BackupId, RestoreDir, Key, false);
        await RestoreEngine.RestoreAsync(opts);

        Assert.Equal("world", ReadRestored("hello.txt"));
        Assert.Equal("binary content", ReadRestored("sub/data.bin"));
    }

    [Fact]
    public async Task EncryptedBackup_ThenRestore_FilesMatch()
    {
        EnsureRegistry();

        WriteFile("secret.txt", "classified");
        WriteFile("docs/readme.md", "# Hello");

        var profile = MakeProfile(encrypt: true);
        var result = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(result.Success, result.Error);
        TrackBackup(result.BackupId);

        var opts = new RestoreOptions(Registry, result.BackupId, RestoreDir, Key, true);
        await RestoreEngine.RestoreAsync(opts);

        Assert.Equal("classified", ReadRestored("secret.txt"));
        Assert.Equal("# Hello", ReadRestored("docs/readme.md"));
    }

    [Fact]
    public async Task Compaction_ProducesFreshFullBackup()
    {
        EnsureRegistry();

        WriteFile("file.txt", "original");
        var profile = MakeProfile();

        var first = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(first.Success);
        TrackBackup(first.BackupId);

        var compacted = await CompactionEngine.CompactAsync(profile, Key);
        Assert.True(compacted.Success, compacted.Error);
        Assert.NotEqual(first.BackupId, compacted.BackupId);
        TrackBackup(compacted.BackupId);

        CleanRestoreDir();
        var opts = new RestoreOptions(Registry, compacted.BackupId, RestoreDir, Key, false);
        await RestoreEngine.RestoreAsync(opts);

        Assert.Equal("original", ReadRestored("file.txt"));
    }

    [Fact]
    public async Task EmptyDirectory_BackupSucceeds()
    {
        EnsureRegistry();

        var profile = MakeProfile();
        var result = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(result.Success);
        Assert.Equal(0, result.FilesAdded);
        TrackBackup(result.BackupId);
    }
}
