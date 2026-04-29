using Microsoft.Extensions.Logging.Abstractions;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Integration.Tests;

[Trait("Category", "Integration")]
public class RetentionTests : IntegrationFixture
{
    [Fact]
    public async Task Prune_RemainingBackupsStillRestorable()
    {
        EnsureRegistry();

        // Create 4 full backups with different content
        var profile = MakeProfile();
        var ids = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            WriteFile("file.txt", $"version {i}");
            var result = await BackupEngine.RunBackupAsync(profile, Key, null);
            Assert.True(result.Success);
            ids.Add(result.BackupId);
            TrackBackup(result.BackupId);
        }

        // Prune to max 2 backups — should delete the first 2
        var oras = new OrasClient(NullLoggerFactory.Instance.CreateLogger<OrasClient>(), plainHttp: true);
        var enforcer = new RetentionEnforcer(oras);
        await enforcer.EnforceAsync(Registry, new RetentionConfig { MaxBackups = 2 });

        // The latest 2 backups should still be restorable
        for (var i = 2; i < 4; i++)
        {
            CleanRestoreDir();
            var opts = new RestoreOptions(Registry, ids[i], RestoreDir, Key, false);
            await RestoreEngine.RestoreAsync(opts);
            Assert.Equal($"version {i + 1}", ReadRestored("file.txt"));
        }
    }

    [Fact]
    public async Task Prune_LatestTagStillWorks()
    {
        EnsureRegistry();

        var profile = MakeProfile();
        for (var i = 1; i <= 3; i++)
        {
            WriteFile("data.txt", $"v{i}");
            var result = await BackupEngine.RunBackupAsync(profile, Key, null);
            Assert.True(result.Success);
            TrackBackup(result.BackupId);
        }

        var oras = new OrasClient(NullLoggerFactory.Instance.CreateLogger<OrasClient>(), plainHttp: true);
        var enforcer = new RetentionEnforcer(oras);
        await enforcer.EnforceAsync(Registry, new RetentionConfig { MaxBackups = 1 });

        // Restore via :latest should still give the most recent content
        CleanRestoreDir();
        var opts = new RestoreOptions(Registry, null, RestoreDir, Key, false);
        await RestoreEngine.RestoreAsync(opts);
        Assert.Equal("v3", ReadRestored("data.txt"));
    }

    [Fact]
    public async Task AutoCompact_ProducesRestorableFullBackup()
    {
        EnsureRegistry();

        var profile = MakeProfile();

        // Create a chain: full → incr1 → incr2
        WriteFile("a.txt", "original");
        var first = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(first.Success);
        TrackBackup(first.BackupId);

        var manifest1 = new DeltaManifest
        {
            BackupId = first.BackupId,
            Files = Tracker.ScanDirectory(SourceDir, profile.ExcludePatterns)
        };

        WriteFile("a.txt", "updated");
        WriteFile("b.txt", "new");
        var second = await BackupEngine.RunBackupAsync(profile, Key, manifest1);
        Assert.True(second.Success);
        TrackBackup(second.BackupId);

        // Compact the chain
        var compacted = await CompactionEngine.CompactAsync(profile, Key);
        Assert.True(compacted.Success);
        TrackBackup(compacted.BackupId);

        // Prune everything except the compacted backup
        var oras = new OrasClient(NullLoggerFactory.Instance.CreateLogger<OrasClient>(), plainHttp: true);
        var enforcer = new RetentionEnforcer(oras);
        await enforcer.EnforceAsync(Registry, new RetentionConfig { MaxBackups = 1 });

        // Restore from compacted — should have the final state
        CleanRestoreDir();
        var opts = new RestoreOptions(Registry, compacted.BackupId, RestoreDir, Key, false);
        await RestoreEngine.RestoreAsync(opts);

        Assert.Equal("updated", ReadRestored("a.txt"));
        Assert.Equal("new", ReadRestored("b.txt"));
    }

    [Fact]
    public async Task Prune_DoesNotDeleteLatestTag()
    {
        EnsureRegistry();

        var profile = MakeProfile();
        WriteFile("keep.txt", "safe");
        var result = await BackupEngine.RunBackupAsync(profile, Key, null);
        Assert.True(result.Success);
        TrackBackup(result.BackupId);

        // Prune with maxBackups=0 — aggressive, but latest should survive
        var oras = new OrasClient(NullLoggerFactory.Instance.CreateLogger<OrasClient>(), plainHttp: true);
        var enforcer = new RetentionEnforcer(oras);
        await enforcer.EnforceAsync(Registry, new RetentionConfig { MaxBackups = 0 });

        // :latest should still work
        CleanRestoreDir();
        var opts = new RestoreOptions(Registry, null, RestoreDir, Key, false);
        await RestoreEngine.RestoreAsync(opts);
        Assert.Equal("safe", ReadRestored("keep.txt"));
    }
}
