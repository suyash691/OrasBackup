using OrasBackup.Core.Backup;
using OrasBackup.Core.Delta;
using Xunit;

namespace OrasBackup.Integration.Tests;

[Trait("Category", "Integration")]
public class DeltaSyncTests : IntegrationFixture
{
    [Fact]
    public async Task Delta_DetectsAddedModifiedDeleted()
    {
        EnsureRegistry();

        // Initial full backup
        WriteFile("a.txt", "v1");
        WriteFile("b.txt", "keep");
        var profile = MakeProfile();
        var first = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(first.Success);
        TrackBackup(first.BackupId);

        var prevManifest = new DeltaManifest
        {
            BackupId = first.BackupId,
            Files = Tracker.ScanDirectory(SourceDir, profile.ExcludePatterns)
        };

        // Modify, add, delete
        WriteFile("a.txt", "v2");
        WriteFile("c.txt", "new");
        DeleteFile("b.txt");

        var second = await BackupEngine.RunBackupAsync(profile, Key, prevManifest);
        Assert.True(second.Success);
        Assert.Equal(1, second.FilesAdded);
        Assert.Equal(1, second.FilesModified);
        Assert.Equal(1, second.FilesDeleted);
        TrackBackup(second.BackupId);
    }

    [Fact]
    public async Task Delta_NoChanges_ZeroDiff()
    {
        EnsureRegistry();

        WriteFile("stable.txt", "unchanged");
        var profile = MakeProfile();
        var first = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(first.Success);
        TrackBackup(first.BackupId);

        var prevManifest = new DeltaManifest
        {
            BackupId = first.BackupId,
            Files = Tracker.ScanDirectory(SourceDir, profile.ExcludePatterns)
        };

        var second = await BackupEngine.RunBackupAsync(profile, Key, prevManifest);
        Assert.True(second.Success);
        Assert.Equal(0, second.FilesAdded);
        Assert.Equal(0, second.FilesModified);
        Assert.Equal(0, second.FilesDeleted);
        Assert.Equal(1, second.FilesUnchanged);
        TrackBackup(second.BackupId);
    }

    [Fact]
    public async Task Delta_MultipleIncrementals_RestoreProducesCorrectState()
    {
        EnsureRegistry();

        // Full backup: a.txt, b.txt
        WriteFile("a.txt", "v1");
        WriteFile("b.txt", "original");
        var profile = MakeProfile();
        var first = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(first.Success);
        TrackBackup(first.BackupId);

        var manifest1 = new DeltaManifest
        {
            BackupId = first.BackupId,
            Files = Tracker.ScanDirectory(SourceDir, profile.ExcludePatterns)
        };

        // Incremental 1: modify a.txt, add c.txt
        WriteFile("a.txt", "v2");
        WriteFile("c.txt", "added");
        var second = await BackupEngine.RunBackupAsync(profile, Key, manifest1);
        Assert.True(second.Success);
        TrackBackup(second.BackupId);

        var manifest2 = new DeltaManifest
        {
            BackupId = second.BackupId,
            BasedOn = first.BackupId,
            Files = Tracker.ScanDirectory(SourceDir, profile.ExcludePatterns)
        };

        // Incremental 2: delete b.txt, modify c.txt
        DeleteFile("b.txt");
        WriteFile("c.txt", "modified");
        var third = await BackupEngine.RunBackupAsync(profile, Key, manifest2);
        Assert.True(third.Success);
        Assert.Equal(1, third.FilesDeleted);
        TrackBackup(third.BackupId);

        // Restore latest and verify final state
        var opts = new RestoreOptions(Registry, third.BackupId, RestoreDir, Key, false);
        await RestoreEngine.RestoreAsync(opts);

        Assert.Equal("v2", ReadRestored("a.txt"));
        Assert.Equal("modified", ReadRestored("c.txt"));
        Assert.False(RestoredExists("b.txt"));
    }

    [Fact]
    public async Task Delta_Encrypted_IncrementalRoundTrip()
    {
        EnsureRegistry();

        WriteFile("data.txt", "initial");
        var profile = MakeProfile(encrypt: true);
        var first = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(first.Success);
        TrackBackup(first.BackupId);

        var prevManifest = new DeltaManifest
        {
            BackupId = first.BackupId,
            Files = Tracker.ScanDirectory(SourceDir, profile.ExcludePatterns)
        };

        WriteFile("data.txt", "updated");
        WriteFile("extra.txt", "new file");
        var second = await BackupEngine.RunBackupAsync(profile, Key, prevManifest);
        Assert.True(second.Success);
        TrackBackup(second.BackupId);

        var opts = new RestoreOptions(Registry, second.BackupId, RestoreDir, Key, true);
        await RestoreEngine.RestoreAsync(opts);

        Assert.Equal("updated", ReadRestored("data.txt"));
        Assert.Equal("new file", ReadRestored("extra.txt"));
    }
}
