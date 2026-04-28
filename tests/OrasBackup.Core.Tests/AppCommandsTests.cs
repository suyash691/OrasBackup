using System.CommandLine;
using NSubstitute;
using OrasBackup.Cli;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;
using Xunit;

namespace OrasBackup.Core.Tests;

public class AppCommandsTests
{
    private readonly RootCommand _root;
    private readonly IServiceFactory _svc = Substitute.For<IServiceFactory>();
    private readonly IProfileStore _store = Substitute.For<IProfileStore>();
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly BackupEngine _engine;
    private readonly RestoreEngine _restoreEngine;

    public AppCommandsTests()
    {
        _svc.CreateProfileStore().Returns(_store);
        _svc.CreateOrasClient().Returns(_oras);
        _svc.CreateEncryptor().Returns(_encryptor);
        _svc.ResolveKey(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EncryptionConfig>()).Returns(new byte[32]);
        _svc.CreateBackupIndexCache().Returns(new BackupIndexCache(Path.Combine(Path.GetTempPath(), $"cmd-test-{Guid.NewGuid():N}")));
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _oras.UploadBlobFromFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrasLayerDescriptor(ci.ArgAt<string>(2), "sha256:blob", 100));
        _oras.ListTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());

        _engine = new BackupEngine(new Delta.DeltaTracker(),
            new ChunkEngine(_oras, _encryptor, Microsoft.Extensions.Logging.Abstractions.NullLogger<ChunkEngine>.Instance),
            _oras, _encryptor, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupEngine>.Instance);
        _restoreEngine = new RestoreEngine(_oras, _encryptor,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RestoreEngine>.Instance);

        _svc.CreateBackupEngine().Returns(_engine);
        _svc.CreateRestoreEngine().Returns(_restoreEngine);
        _svc.CreateLogger<BackupScheduler>().Returns(Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupScheduler>.Instance);

        _root = AppCommands.Build(_svc);
    }

    // --- Structure tests ---

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
    public void Parse_BackupMissingProfile_HasError()
    {
        Assert.NotEmpty(_root.Parse("backup").Errors);
    }

    [Fact]
    public void Parse_BackupWithProfile_NoError()
    {
        Assert.Empty(_root.Parse("backup --profile myprof --password secret").Errors);
    }

    [Fact]
    public void Restore_RequiresProfileAndTarget()
    {
        var restore = _root.Subcommands.Single(c => c.Name == "restore");
        var required = restore.Options.Where(o => o.Required).Select(o => o.Name).ToList();
        Assert.Contains("--profile", required);
        Assert.Contains("--target", required);
    }

    // --- Execution tests ---

    [Fact]
    public async Task Init_CreatesProfile()
    {
        await _root.Parse("init --name testprof --source /data --registry reg.io/repo").InvokeAsync();
        _store.Received(1).Save(Arg.Is<BackupProfile>(p => p.Name == "testprof" && p.Registry == "reg.io/repo"));
    }

    [Fact]
    public async Task Backup_ExecutesEngine()
    {
        var srcDir = Path.Combine(Path.GetTempPath(), $"cmd-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "f.txt"), "data");
        try
        {
            _store.Load("myprof").Returns(new BackupProfile
            {
                Name = "myprof", SourcePaths = [srcDir], Registry = "reg/repo",
                Encryption = new EncryptionConfig { Enabled = false }
            });

            await _root.Parse("backup --profile myprof --password test").InvokeAsync();

            // Engine should have pushed something
            await _oras.Received().PushManifestAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<OrasLayer>>(),
                Arg.Any<IReadOnlyList<OrasLayerDescriptor>>(), Arg.Any<CancellationToken>());
        }
        finally { try { Directory.Delete(srcDir, true); } catch { } }
    }

    [Fact]
    public async Task Restore_ExecutesEngine()
    {
        var targetDir = Path.Combine(Path.GetTempPath(), $"cmd-restore-{Guid.NewGuid():N}");
        try
        {
            _store.Load("myprof").Returns(new BackupProfile
            {
                Name = "myprof", Registry = "reg/repo",
                Encryption = new EncryptionConfig { Enabled = false }
            });

            var index = new BackupIndex { BackupId = "b1", Chunks = [] };
            var indexBytes = index.Serialize();
            _oras.FetchManifestLayersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns([new OrasManifestEntry("application/vnd.orasbackup.index.v2+json", "sha256:idx", indexBytes.Length)]);
            _oras.PullLayerAsync(Arg.Any<string>(), "sha256:idx", Arg.Any<CancellationToken>())
                .Returns(indexBytes);

            await _root.Parse($"restore --profile myprof --target {targetDir} --password test").InvokeAsync();

            Assert.True(Directory.Exists(targetDir));
        }
        finally { try { Directory.Delete(targetDir, true); } catch { } }
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
