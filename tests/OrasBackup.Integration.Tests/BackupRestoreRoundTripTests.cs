using Microsoft.Extensions.Logging.Abstractions;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Integration.Tests;

/// <summary>
/// Integration tests against a real OCI registry (registry:2) + oras CLI.
/// Only run in CI where ORAS_REGISTRY is set. Locally they are skipped.
/// </summary>
[Trait("Category", "Integration")]
public class BackupRestoreRoundTripTests : IDisposable
{
    private readonly string _sourceDir;
    private readonly string _restoreDir;
    private readonly string _registry;
    private readonly byte[] _key;
    private readonly BackupEngine _backupEngine;
    private readonly RestoreEngine _restoreEngine;
    private readonly CompactionEngine _compactionEngine;
    private readonly AesEncryptor _encryptor = new(pbkdf2Iterations: 1000);

    public BackupRestoreRoundTripTests()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _sourceDir = Path.Combine(Path.GetTempPath(), $"oras-int-src-{id}");
        _restoreDir = Path.Combine(Path.GetTempPath(), $"oras-int-dst-{id}");
        Directory.CreateDirectory(_sourceDir);

        var registryHost = Environment.GetEnvironmentVariable("ORAS_REGISTRY") ?? "localhost:5000";
        _registry = $"{registryHost}/test/backup-{id}";

        var salt = _encryptor.GenerateSalt();
        _key = _encryptor.DeriveKey("test-password", salt);

        var lf = NullLoggerFactory.Instance;
        var oras = new OrasClient(lf.CreateLogger<OrasClient>());
        _backupEngine = new BackupEngine(new DeltaTracker(), _encryptor, oras, lf.CreateLogger<BackupEngine>());
        _restoreEngine = new RestoreEngine(_encryptor, oras, lf.CreateLogger<RestoreEngine>());
        _compactionEngine = new CompactionEngine(_restoreEngine, _backupEngine, lf.CreateLogger<CompactionEngine>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_sourceDir, true); } catch { }
        try { Directory.Delete(_restoreDir, true); } catch { }
    }

    private BackupProfile MakeProfile(bool encrypt = false) => new()
    {
        Name = "int-test",
        SourcePaths = [_sourceDir],
        Registry = _registry,
        Encryption = new EncryptionConfig { Enabled = encrypt }
    };

    [Fact]
    public async Task FullBackup_ThenRestore_FilesMatch()
    {
        EnsureRegistry();

        File.WriteAllText(Path.Combine(_sourceDir, "hello.txt"), "world");
        File.WriteAllText(Path.Combine(_sourceDir, "data.bin"), "binary content");

        var profile = MakeProfile();
        var result = await _backupEngine.RunBackupAsync(profile, _key, null);
        Assert.True(result.Success, result.Error);

        var opts = new RestoreOptions(_registry, result.BackupId, _restoreDir, _key, false);
        await _restoreEngine.RestoreAsync(opts);

        Assert.Equal("world", File.ReadAllText(Path.Combine(_restoreDir, "hello.txt")));
        Assert.Equal("binary content", File.ReadAllText(Path.Combine(_restoreDir, "data.bin")));
    }

    [Fact]
    public async Task IncrementalBackup_OnlyPushesChanges()
    {
        EnsureRegistry();

        File.WriteAllText(Path.Combine(_sourceDir, "a.txt"), "v1");
        File.WriteAllText(Path.Combine(_sourceDir, "b.txt"), "keep");
        var profile = MakeProfile();
        var first = await _backupEngine.RunBackupAsync(profile, _key, null);
        Assert.True(first.Success);

        var tracker = new DeltaTracker();
        var prevManifest = new DeltaManifest
        {
            BackupId = first.BackupId,
            Files = tracker.ScanDirectory(_sourceDir, profile.ExcludePatterns)
        };

        File.WriteAllText(Path.Combine(_sourceDir, "a.txt"), "v2");
        File.WriteAllText(Path.Combine(_sourceDir, "c.txt"), "new");
        File.Delete(Path.Combine(_sourceDir, "b.txt"));

        var second = await _backupEngine.RunBackupAsync(profile, _key, prevManifest);
        Assert.True(second.Success);
        Assert.Equal(1, second.FilesAdded);
        Assert.Equal(1, second.FilesModified);
        Assert.Equal(1, second.FilesDeleted);
    }

    [Fact]
    public async Task EncryptedBackup_ThenRestore_FilesMatch()
    {
        EnsureRegistry();

        File.WriteAllText(Path.Combine(_sourceDir, "secret.txt"), "classified");
        var profile = MakeProfile(encrypt: true);

        var result = await _backupEngine.RunBackupAsync(profile, _key, null);
        Assert.True(result.Success, result.Error);

        var opts = new RestoreOptions(_registry, result.BackupId, _restoreDir, _key, true);
        await _restoreEngine.RestoreAsync(opts);

        Assert.Equal("classified", File.ReadAllText(Path.Combine(_restoreDir, "secret.txt")));
    }

    [Fact]
    public async Task Compaction_ProducesFreshFullBackup()
    {
        EnsureRegistry();

        File.WriteAllText(Path.Combine(_sourceDir, "file.txt"), "original");
        var profile = MakeProfile();
        var first = await _backupEngine.RunBackupAsync(profile, _key, null);
        Assert.True(first.Success);

        var compacted = await _compactionEngine.CompactAsync(profile, _key);
        Assert.True(compacted.Success, compacted.Error);
        Assert.NotEqual(first.BackupId, compacted.BackupId);

        var opts = new RestoreOptions(_registry, compacted.BackupId, _restoreDir, _key, false);
        await _restoreEngine.RestoreAsync(opts);

        Assert.Equal("original", File.ReadAllText(Path.Combine(_restoreDir, "file.txt")));
    }

    private static void EnsureRegistry()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORAS_REGISTRY")))
            Assert.Fail("ORAS_REGISTRY not set — run via integration test workflow");
    }
}
