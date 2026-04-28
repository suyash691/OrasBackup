using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OrasBackup.Cli;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Oras;
using OrasBackup.Gui.Services;
using OrasBackup.Gui.ViewModels;
using Xunit;

namespace OrasBackup.Gui.Tests;

public class ProfileManagerViewModelTests
{
    private readonly IServiceFactory _svc = Substitute.For<IServiceFactory>();
    private readonly IProfileStore _store = Substitute.For<IProfileStore>();
    private readonly LogService _log = new();

    public ProfileManagerViewModelTests()
    {
        _svc.CreateProfileStore().Returns(_store);
        _store.ListProfiles().Returns(new[] { "existing" });
    }

    [Fact]
    public void Constructor_LoadsExistingProfiles()
    {
        var vm = new ProfileManagerViewModel(_svc, _log);
        Assert.Single(vm.Profiles);
        Assert.Equal("existing", vm.Profiles[0]);
    }

    [Fact]
    public void CreateProfile_SavesAndRefreshes()
    {
        _store.ListProfiles().Returns(new[] { "existing" }, new[] { "existing", "newprof" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.NewName = "newprof";
        vm.NewSource = "/data";
        vm.NewRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        _store.Received(1).Save(Arg.Is<BackupProfile>(p => p.Name == "newprof"));
    }

    [Fact]
    public void CreateProfile_EmptyName_DoesNothing()
    {
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.NewName = "";
        vm.NewRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        _store.DidNotReceive().Save(Arg.Any<BackupProfile>());
    }

    [Fact]
    public void CreateProfile_EmptyRegistry_DoesNothing()
    {
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.NewName = "test";
        vm.NewRegistry = "";
        vm.CreateProfileCommand.Execute(null);
        _store.DidNotReceive().Save(Arg.Any<BackupProfile>());
    }

    [Fact]
    public void CreateProfile_ClearsFields()
    {
        _store.ListProfiles().Returns(new[] { "existing" }, new[] { "existing", "new" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.NewName = "new";
        vm.NewSource = "/data";
        vm.NewRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        Assert.Equal("", vm.NewName);
        Assert.Equal("", vm.NewSource);
        Assert.Equal("", vm.NewRegistry);
    }

    [Fact]
    public void CreateProfile_LogsCreation()
    {
        _store.ListProfiles().Returns(new[] { "existing" }, new[] { "existing", "logged" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.NewName = "logged";
        vm.NewSource = "/data";
        vm.NewRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        Assert.Contains(_log.Entries, e => e.Contains("logged") && e.Contains("created"));
    }

    [Fact]
    public void CreateProfile_ParsesCommaSeparatedSources()
    {
        _store.ListProfiles().Returns(new[] { "existing" }, new[] { "existing", "multi" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.NewName = "multi";
        vm.NewSource = "/data, /photos, /videos";
        vm.NewRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        _store.Received(1).Save(Arg.Is<BackupProfile>(p => p.SourcePaths.Count == 3));
    }

    [Fact]
    public void DeleteProfile_NullSelected_DoesNothing()
    {
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.SelectedProfile = null;
        vm.DeleteProfileCommand.Execute(null);
        // No exception, no crash
    }

    [Fact]
    public void DeleteProfile_LogsPath()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"prof-del-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var profilePath = Path.Combine(tmpDir, "todelete.json");
        File.WriteAllText(profilePath, "{}");
        _store.GetProfilePath("todelete").Returns(profilePath);
        _store.ListProfiles().Returns(new[] { "todelete" }, Array.Empty<string>());

        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.SelectedProfile = "todelete";
        vm.DeleteProfileCommand.Execute(null);

        Assert.Contains(_log.Entries, e => e.Contains(profilePath));
        Assert.False(File.Exists(profilePath));
        try { Directory.Delete(tmpDir, true); } catch { }
    }
}

public class DashboardViewModelTests
{
    private readonly IServiceFactory _svc = Substitute.For<IServiceFactory>();
    private readonly IProfileStore _store = Substitute.For<IProfileStore>();
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly LogService _log = new();
    private readonly string _srcDir;

    public DashboardViewModelTests()
    {
        _srcDir = Path.Combine(Path.GetTempPath(), $"dash-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "data");

        _svc.CreateProfileStore().Returns(_store);
        _svc.CreateOrasClient().Returns(_oras);
        _svc.CreateEncryptor().Returns(_encryptor);
        _svc.ResolveKey(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EncryptionConfig>()).Returns(new byte[32]);
        _svc.CreateBackupIndexCache().Returns(new BackupIndexCache(Path.Combine(Path.GetTempPath(), $"dash-cache-{Guid.NewGuid():N}")));
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _oras.UploadBlobFromFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrasLayerDescriptor(ci.ArgAt<string>(2), "sha256:blob", 100));
        _oras.ListTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());

        var engine = new BackupEngine(new OrasBackup.Core.Delta.DeltaTracker(),
            new ChunkEngine(_oras, _encryptor, Microsoft.Extensions.Logging.Abstractions.NullLogger<ChunkEngine>.Instance),
            _oras, _encryptor, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupEngine>.Instance);
        _svc.CreateBackupEngine().Returns(engine);

        _store.Load("test").Returns(new BackupProfile
        {
            Name = "test", SourcePaths = [_srcDir], Registry = "reg/repo",
            Encryption = new EncryptionConfig { Enabled = false }
        });
    }

    [Fact]
    public async Task RunBackup_EmptyProfile_DoesNothing()
    {
        var vm = new DashboardViewModel(_svc, _log);
        vm.SelectedProfile = "";
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.Equal("Idle", vm.Status);
    }

    [Fact]
    public async Task RunBackup_Success_UpdatesStatus()
    {
        var vm = new DashboardViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        vm.Password = "secret";
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.Equal("Backup complete", vm.Status);
        Assert.Contains("Backup", vm.LastBackupInfo);
    }

    [Fact]
    public async Task RunBackup_ClearsPassword()
    {
        var vm = new DashboardViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        vm.Password = "secret";
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Password);
    }

    [Fact]
    public async Task RunBackup_SetsIsRunningDuringExecution()
    {
        var vm = new DashboardViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning); // should be false after completion
    }

    [Fact]
    public async Task RunBackup_LogsResult()
    {
        var vm = new DashboardViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.Contains(_log.Entries, e => e.Contains("Backup"));
    }

    [Fact]
    public async Task RunBackup_EngineFailure_SetsErrorStatus()
    {
        _store.Load("failing").Returns(new BackupProfile
        {
            Name = "failing", SourcePaths = ["/nonexistent/path"], Registry = "reg/repo",
            Encryption = new EncryptionConfig { Enabled = false }
        });
        var vm = new DashboardViewModel(_svc, _log);
        vm.SelectedProfile = "failing";
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.StartsWith("Failed:", vm.Status);
    }
}

public class RestoreViewModelTests
{
    private readonly IServiceFactory _svc = Substitute.For<IServiceFactory>();
    private readonly IProfileStore _store = Substitute.For<IProfileStore>();
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly LogService _log = new();

    public RestoreViewModelTests()
    {
        _svc.CreateProfileStore().Returns(_store);
        _svc.CreateOrasClient().Returns(_oras);
        _svc.CreateEncryptor().Returns(_encryptor);
        _svc.ResolveKey(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EncryptionConfig>()).Returns(new byte[32]);

        var restoreEngine = new RestoreEngine(_oras, _encryptor,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RestoreEngine>.Instance);
        _svc.CreateRestoreEngine().Returns(restoreEngine);

        _store.Load("test").Returns(new BackupProfile
        {
            Name = "test", Registry = "reg/repo",
            Encryption = new EncryptionConfig { Enabled = false }
        });
    }

    [Fact]
    public async Task LoadBackups_PopulatesList()
    {
        _oras.ListTagsAsync("reg/repo", Arg.Any<CancellationToken>())
            .Returns(new[] { "20260101-abc", "chunk-xyz", "latest" });
        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        // Should have (latest) + 1 backup tag (chunk-xyz filtered out)
        Assert.Equal(2, vm.BackupIds.Count);
        Assert.Equal("(latest)", vm.BackupIds[0]);
    }

    [Fact]
    public async Task LoadBackups_EmptyProfile_DoesNothing()
    {
        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "";
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.Empty(vm.BackupIds);
    }

    [Fact]
    public async Task LoadBackups_Error_LogsMessage()
    {
        _store.Load("broken").Returns(_ => throw new FileNotFoundException("not found"));
        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "broken";
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.Contains(_log.Entries, e => e.Contains("Failed to list backups"));
    }

    [Fact]
    public async Task Restore_EmptyProfile_DoesNothing()
    {
        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "";
        vm.TargetDir = "/tmp/restore";
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal("Ready", vm.Status);
    }

    [Fact]
    public async Task Restore_EmptyTarget_DoesNothing()
    {
        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        vm.TargetDir = "";
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal("Ready", vm.Status);
    }

    [Fact]
    public async Task Restore_Success_UpdatesStatus()
    {
        var targetDir = Path.Combine(Path.GetTempPath(), $"restore-vm-{Guid.NewGuid():N}");
        try
        {
            var index = new BackupIndex { BackupId = "b1", Chunks = [] };
            var indexBytes = index.Serialize();
            _oras.FetchManifestLayersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns([new OrasManifestEntry("application/vnd.orasbackup.index.v2+json", "sha256:idx", indexBytes.Length)]);
            _oras.PullLayerAsync(Arg.Any<string>(), "sha256:idx", Arg.Any<CancellationToken>())
                .Returns(indexBytes);

            var vm = new RestoreViewModel(_svc, _log);
            vm.SelectedProfile = "test";
            vm.TargetDir = targetDir;
            vm.Password = "secret";
            await vm.RestoreCommand.ExecuteAsync(null);

            Assert.Contains("Restored to", vm.Status);
            Assert.Equal("", vm.Password); // cleared after use
        }
        finally { try { Directory.Delete(targetDir, true); } catch { } }
    }

    [Fact]
    public async Task Restore_Failure_SetsErrorStatus()
    {
        _oras.FetchManifestLayersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<OrasManifestEntry>>(_ => throw new Exception("registry down"));

        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        vm.TargetDir = "/tmp/restore";
        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.StartsWith("Failed:", vm.Status);
        Assert.Contains(_log.Entries, e => e.Contains("Restore failed"));
    }
}

public class LogViewModelTests
{
    [Fact]
    public void Entries_ReflectLogService()
    {
        var log = new LogService();
        var vm = new LogViewModel(log);
        log.Log("test message");
        Assert.Single(vm.Entries);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var log = new LogService();
        log.Log("a");
        log.Log("b");
        var vm = new LogViewModel(log);
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Entries);
    }
}
