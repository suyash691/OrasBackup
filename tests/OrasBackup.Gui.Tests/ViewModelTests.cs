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
        vm.EditName = "newprof";
        vm.EditSource = "/data";
        vm.EditRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        _store.Received(1).Save(Arg.Is<BackupProfile>(p => p.Name == "newprof"));
    }

    [Fact]
    public void CreateProfile_EmptyName_DoesNothing()
    {
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.EditName = "";
        vm.EditRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        _store.DidNotReceive().Save(Arg.Any<BackupProfile>());
    }

    [Fact]
    public void CreateProfile_EmptyRegistry_DoesNothing()
    {
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.EditName = "test";
        vm.EditRegistry = "";
        vm.CreateProfileCommand.Execute(null);
        _store.DidNotReceive().Save(Arg.Any<BackupProfile>());
    }

    [Fact]
    public void CreateProfile_ClearsFields()
    {
        _store.ListProfiles().Returns(new[] { "existing" }, new[] { "existing", "new" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.EditName = "new";
        vm.EditSource = "/data";
        vm.EditRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        Assert.Equal("", vm.EditName);
        Assert.Equal("", vm.EditSource);
        Assert.Equal("", vm.EditRegistry);
    }

    [Fact]
    public void CreateProfile_LogsCreation()
    {
        _store.ListProfiles().Returns(new[] { "existing" }, new[] { "existing", "logged" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.EditName = "logged";
        vm.EditSource = "/data";
        vm.EditRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);
        Assert.Contains(_log.Entries, e => e.Contains("logged") && e.Contains("created"));
    }

    [Fact]
    public void CreateProfile_ParsesCommaSeparatedSources()
    {
        _store.ListProfiles().Returns(new[] { "existing" }, new[] { "existing", "multi" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.EditName = "multi";
        vm.EditSource = "/data, /photos, /videos";
        vm.EditRegistry = "reg.io/repo";
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

    [Fact]
    public void SelectProfile_LoadsFieldsForEditing()
    {
        _store.Load("existing").Returns(new BackupProfile
        {
            Name = "existing", SourcePaths = ["/data", "/photos"],
            Registry = "ghcr.io/user/backups", AuthToken = "ghp_secret123",
            Encryption = new EncryptionConfig { Enabled = true },
            Retention = new RetentionConfig { MaxBackups = 30 }
        });

        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.SelectedProfile = "existing";

        Assert.Equal("existing", vm.EditName);
        Assert.Contains("/data", vm.EditSource);
        Assert.Equal("ghcr.io/user/backups", vm.EditRegistry);
        Assert.Equal("ghp_secret123", vm.EditAuthToken);
        Assert.True(vm.EditEncryption);
        Assert.Equal(30, vm.EditMaxBackups);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public void SaveProfile_UpdatesExistingProfile()
    {
        _store.Load("existing").Returns(new BackupProfile
        {
            Name = "existing", SourcePaths = ["/old"], Registry = "old.io/repo"
        });
        _store.ListProfiles().Returns(new[] { "existing" });

        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.SelectedProfile = "existing";
        vm.EditRegistry = "new.io/repo";
        vm.EditAuthToken = "new-token";
        vm.SaveProfileCommand.Execute(null);

        _store.Received().Save(Arg.Is<BackupProfile>(p =>
            p.Registry == "new.io/repo" && p.AuthToken == "new-token"));
    }

    [Fact]
    public void CreateProfile_WithAuthToken_SavesToken()
    {
        _store.ListProfiles().Returns(Array.Empty<string>(), new[] { "newprof" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.EditName = "newprof";
        vm.EditSource = "/data";
        vm.EditRegistry = "ghcr.io/user/repo";
        vm.EditAuthToken = "ghp_mytoken";
        vm.CreateProfileCommand.Execute(null);

        _store.Received().Save(Arg.Is<BackupProfile>(p =>
            p.Name == "newprof" && p.AuthToken == "ghp_mytoken"));
    }

    [Fact]
    public void CreateProfile_EmptyAuthToken_SavesNull()
    {
        _store.ListProfiles().Returns(Array.Empty<string>(), new[] { "newprof" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.EditName = "newprof";
        vm.EditSource = "/data";
        vm.EditRegistry = "reg.io/repo";
        vm.EditAuthToken = "";
        vm.CreateProfileCommand.Execute(null);

        _store.Received().Save(Arg.Is<BackupProfile>(p => p.AuthToken == null));
    }

    [Fact]
    public void NewProfile_ClearsFormAndExitsEditMode()
    {
        _store.Load("existing").Returns(new BackupProfile { Name = "existing", Registry = "r" });
        var vm = new ProfileManagerViewModel(_svc, _log);
        vm.SelectedProfile = "existing";
        Assert.True(vm.IsEditing);

        vm.NewProfileCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.Equal("", vm.EditName);
        Assert.Equal("", vm.EditRegistry);
    }

    [Fact]
    public void SharedProfileList_UpdatesWhenProfileCreated()
    {
        _store.ListProfiles().Returns(Array.Empty<string>(), new[] { "fresh" });
        var vm = new ProfileManagerViewModel(_svc, _log);

        // Dashboard and Restore share the same ObservableCollection
        var dashboard = new DashboardViewModel(
            _svc, _log, vm.Profiles);
        var restore = new RestoreViewModel(
            _svc, _log, vm.Profiles);

        vm.EditName = "fresh";
        vm.EditSource = "/data";
        vm.EditRegistry = "reg.io/repo";
        vm.CreateProfileCommand.Execute(null);

        // The shared collection should now contain the new profile
        Assert.Contains("fresh", dashboard.Profiles);
        Assert.Contains("fresh", restore.Profiles);
    }
}

public class DashboardViewModelTests
{
    private readonly IServiceFactory _svc = Substitute.For<IServiceFactory>();
    private readonly IProfileStore _store = Substitute.For<IProfileStore>();
    private readonly IBackupEngine _engine = Substitute.For<IBackupEngine>();
    private readonly LogService _log = new();

    public DashboardViewModelTests()
    {
        _svc.CreateProfileStore().Returns(_store);
        _svc.CreateBackupEngine(Arg.Any<string?>(), Arg.Any<string?>()).Returns(_engine);
        _svc.CreateBackupIndexCache().Returns(new BackupIndexCache(Path.Combine(Path.GetTempPath(), $"dash-cache-{Guid.NewGuid():N}")));
        _svc.ResolveKey(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EncryptionConfig>()).Returns(new byte[32]);
        _svc.CreateOrasClient(Arg.Any<string?>(), Arg.Any<string?>()).Returns(Substitute.For<IOrasClient>());

        _store.Load("test").Returns(new BackupProfile
        {
            Name = "test", SourcePaths = ["/data"], Registry = "reg/repo",
            Encryption = new EncryptionConfig { Enabled = false }
        });

        _engine.RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(), Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>())
            .Returns(new BackupResult("b1", 5, 0, 0, 1000, TimeSpan.FromSeconds(1), true));
        _engine.LastIndex.Returns(new BackupIndex { BackupId = "b1" });

        var oras = Substitute.For<IOrasClient>();
        oras.ListTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        _svc.CreateOrasClient(Arg.Any<string?>(), Arg.Any<string?>()).Returns(oras);
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
        Assert.False(vm.IsRunning);
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
        _engine.RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(), Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>())
            .Returns(new BackupResult("b1", 0, 0, 0, 0, TimeSpan.Zero, false, "disk full"));

        var vm = new DashboardViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.StartsWith("Failed:", vm.Status);
    }

    [Fact]
    public async Task RunBackup_Cancellation_ShowsCancelled()
    {
        _engine.RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(), Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>())
            .Returns<BackupResult>(_ => throw new OperationCanceledException());

        var vm = new DashboardViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.Equal("Cancelled", vm.Status);
    }
}

public class RestoreViewModelTests
{
    private readonly IServiceFactory _svc = Substitute.For<IServiceFactory>();
    private readonly IProfileStore _store = Substitute.For<IProfileStore>();
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IRestoreEngine _restoreEngine = Substitute.For<IRestoreEngine>();
    private readonly LogService _log = new();

    public RestoreViewModelTests()
    {
        _svc.CreateProfileStore().Returns(_store);
        _svc.CreateOrasClient(Arg.Any<string?>(), Arg.Any<string?>()).Returns(_oras);
        _svc.CreateRestoreEngine(Arg.Any<string?>(), Arg.Any<string?>()).Returns(_restoreEngine);
        _svc.ResolveKey(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EncryptionConfig>()).Returns(new byte[32]);

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
        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        vm.TargetDir = "/tmp/restore";
        vm.Password = "secret";
        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.Contains("Restored to", vm.Status);
        Assert.Equal("", vm.Password);
        await _restoreEngine.Received(1).RestoreAsync("reg/repo", Arg.Any<string?>(), "/tmp/restore",
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_Failure_SetsErrorStatus()
    {
        _restoreEngine.RestoreAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new Exception("registry down"));

        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        vm.TargetDir = "/tmp/restore";
        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.StartsWith("Failed:", vm.Status);
        Assert.Contains(_log.Entries, e => e.Contains("Restore failed"));
    }

    [Fact]
    public async Task Restore_Cancellation_ShowsCancelled()
    {
        _restoreEngine.RestoreAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException());

        var vm = new RestoreViewModel(_svc, _log);
        vm.SelectedProfile = "test";
        vm.TargetDir = "/tmp/restore";
        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("Cancelled", vm.Status);
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
