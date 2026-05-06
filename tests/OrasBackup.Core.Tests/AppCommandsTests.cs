using System.CommandLine;
using NSubstitute;
using OrasBackup.Cli;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;
using Xunit;

namespace OrasBackup.Core.Tests;

public class AppCommandsTests
{
    private readonly RootCommand _root;
    private readonly IServiceFactory _svc = Substitute.For<IServiceFactory>();
    private readonly IProfileStore _store = Substitute.For<IProfileStore>();
    private readonly IBackupEngine _engine = Substitute.For<IBackupEngine>();
    private readonly IRestoreEngine _restoreEngine = Substitute.For<IRestoreEngine>();
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();

    public AppCommandsTests()
    {
        _svc.CreateProfileStore().Returns(_store);
        _svc.CreateOrasClient(Arg.Any<string?>(), Arg.Any<string?>()).Returns(_oras);
        _svc.CreateBackupEngine(Arg.Any<string?>(), Arg.Any<string?>()).Returns(_engine);
        _svc.CreateRestoreEngine(Arg.Any<string?>(), Arg.Any<string?>()).Returns(_restoreEngine);
        _svc.CreateBackupIndexCache().Returns(Substitute.For<IBackupIndexCache>());
        _svc.ResolveKey(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EncryptionConfig>()).Returns(new byte[32]);
        _svc.CreateLogger<BackupScheduler>().Returns(Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupScheduler>.Instance);
        _oras.ListTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());

        _engine.RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(), Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>())
            .Returns(new BackupResult("b1", 1, 0, 0, 100, TimeSpan.FromSeconds(1), true));
        _engine.LastIndex.Returns(new BackupIndex { BackupId = "b1" });

        _root = AppCommands.Build(_svc);
    }

    [Fact]
    public void Build_HasAllSubcommands()
    {
        var names = _root.Subcommands.Select(c => c.Name).ToList();
        Assert.Contains("init", names);
        Assert.Contains("backup", names);
        Assert.Contains("restore", names);
        Assert.Contains("list", names);
        Assert.Contains("daemon", names);
        Assert.Equal(5, names.Count);
    }

    [Fact]
    public void Parse_BackupMissingProfile_HasError() =>
        Assert.NotEmpty(_root.Parse("backup").Errors);

    [Fact]
    public void Restore_RequiresProfileAndTarget()
    {
        var restore = _root.Subcommands.Single(c => c.Name == "restore");
        var required = restore.Options.Where(o => o.Required).Select(o => o.Name).ToList();
        Assert.Contains("--profile", required);
        Assert.Contains("--target", required);
    }

    [Fact]
    public async Task Init_CreatesProfile()
    {
        await _root.Parse("init --name testprof --source /data --registry reg.io/repo").InvokeAsync();
        _store.Received(1).Save(Arg.Is<BackupProfile>(p => p.Name == "testprof" && p.Registry == "reg.io/repo"));
    }

    [Fact]
    public async Task Backup_ExecutesEngine()
    {
        _store.Load("myprof").Returns(new BackupProfile
        {
            Name = "myprof", SourcePaths = ["/data"], Registry = "reg/repo",
            Encryption = new EncryptionConfig { Enabled = false }
        });

        await _root.Parse("backup --profile myprof --password test").InvokeAsync();

        await _engine.Received().RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(),
            Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_ExecutesEngine()
    {
        _store.Load("myprof").Returns(new BackupProfile
        {
            Name = "myprof", Registry = "reg/repo",
            Encryption = new EncryptionConfig { Enabled = false }
        });

        await _root.Parse("restore --profile myprof --target /tmp/out --password test").InvokeAsync();

        await _restoreEngine.Received().RestoreAsync("reg/repo", Arg.Any<string?>(), "/tmp/out",
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_WithoutProfile_ListsProfiles()
    {
        _store.ListProfiles().Returns(new[] { "alpha", "beta" });
        await _root.Parse("list").InvokeAsync();
        _store.Received().ListProfiles();
    }

    [Fact]
    public async Task List_WithProfile_ListsTags()
    {
        _store.Load("myprof").Returns(new BackupProfile { Name = "myprof", Registry = "reg/repo" });
        _oras.ListTagsAsync("reg/repo", Arg.Any<CancellationToken>()).Returns(new[] { "v1", "v2" });
        await _root.Parse("list --profile myprof").InvokeAsync();
        await _oras.Received().ListTagsAsync("reg/repo", Arg.Any<CancellationToken>());
    }
}
