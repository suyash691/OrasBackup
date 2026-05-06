using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;
using Xunit;

namespace OrasBackup.Integration.Tests;

[Trait("Category", "Integration")]
public class BackupRestoreTests : IDisposable
{
    private readonly string _sourceDir;
    private readonly string _restoreDir;
    private readonly string _registry;
    private readonly byte[] _key;
    private readonly BackupEngine _backupEngine;
    private readonly RestoreEngine _restoreEngine;
    private readonly AesEncryptor _encryptor = new(pbkdf2Iterations: 1000);
    private readonly IOrasClient _oras;

    public BackupRestoreTests()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _sourceDir = Path.Combine(Path.GetTempPath(), $"v2-int-src-{id}");
        _restoreDir = Path.Combine(Path.GetTempPath(), $"v2-int-dst-{id}");
        Directory.CreateDirectory(_sourceDir);

        var registryHost = Environment.GetEnvironmentVariable("ORAS_REGISTRY") ?? "localhost:5000";
        _registry = $"{registryHost}/test/v2backup-{id}";

        var salt = _encryptor.GenerateSalt();
        _key = _encryptor.DeriveKey("test-password", salt);

        var lf = NullLoggerFactory.Instance;
        var http = new HttpClient { BaseAddress = new Uri($"http://{registryHost}") };
        _oras = new HttpOrasClient(http, lf.CreateLogger<HttpOrasClient>());
        var chunkEngine = new ChunkEngine(_oras, _encryptor, lf.CreateLogger<ChunkEngine>());
        _backupEngine = new BackupEngine(new DeltaTracker(), chunkEngine, _oras, _encryptor,
            lf.CreateLogger<BackupEngine>(), new DirectoryChunker(maxChunkBytes: 1_000_000, minChunkBytes: 100));
        _restoreEngine = new RestoreEngine(_oras, _encryptor, lf.CreateLogger<RestoreEngine>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_sourceDir, true); } catch { }
        try { Directory.Delete(_restoreDir, true); } catch { }
    }

    private static void EnsureRegistry()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORAS_REGISTRY")))
            Assert.Fail("ORAS_REGISTRY not set");
    }

    private BackupProfile MakeProfile(bool encrypt = false) => new()
    {
        Name = "v2-int", SourcePaths = [_sourceDir], Registry = _registry,
        Encryption = new EncryptionConfig { Enabled = encrypt }
    };

    private void WriteFile(string rel, string content)
    {
        var path = Path.Combine(_sourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadRestored(string rel) =>
        File.ReadAllText(Path.Combine(_restoreDir, rel.Replace('/', Path.DirectorySeparatorChar)));

    private bool RestoredExists(string rel) =>
        File.Exists(Path.Combine(_restoreDir, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task FullBackup_ThenRestore()
    {
        EnsureRegistry();
        WriteFile("hello.txt", "world");
        WriteFile("sub/data.bin", "binary");

        var result = await _backupEngine.RunBackupAsync(MakeProfile(), _key, null);
        Assert.True(result.Success, result.Error);

        await _restoreEngine.RestoreAsync(_registry, null, _restoreDir, _key, false);
        Assert.Equal("world", ReadRestored("hello.txt"));
        Assert.Equal("binary", ReadRestored("sub/data.bin"));
    }

    [Fact]
    public async Task EncryptedBackup_ThenRestore()
    {
        EnsureRegistry();
        WriteFile("secret.txt", "classified");

        var result = await _backupEngine.RunBackupAsync(MakeProfile(encrypt: true), _key, null);
        Assert.True(result.Success, result.Error);

        await _restoreEngine.RestoreAsync(_registry, null, _restoreDir, _key, true);
        Assert.Equal("classified", ReadRestored("secret.txt"));
    }

    [Fact]
    public async Task IncrementalBackup_SkipsUnchangedChunks()
    {
        EnsureRegistry();
        WriteFile("stable.txt", "unchanged");
        WriteFile("changing.txt", "v1");

        var first = await _backupEngine.RunBackupAsync(MakeProfile(), _key, null);
        Assert.True(first.Success);

        WriteFile("changing.txt", "v2");
        var second = await _backupEngine.RunBackupAsync(MakeProfile(), _key, _backupEngine.LastIndex);
        Assert.True(second.Success);

        // Restore latest — should have v2
        await _restoreEngine.RestoreAsync(_registry, null, _restoreDir, _key, false);
        Assert.Equal("v2", ReadRestored("changing.txt"));
        Assert.Equal("unchanged", ReadRestored("stable.txt"));
    }

    [Fact]
    public async Task EmptyDirectory_Succeeds()
    {
        EnsureRegistry();
        var result = await _backupEngine.RunBackupAsync(MakeProfile(), _key, null);
        Assert.True(result.Success);
        Assert.Equal(0, result.FilesAdded);
    }

    [Fact]
    public async Task DeletionTracking_EndToEnd()
    {
        EnsureRegistry();
        WriteFile("keep.txt", "keep");
        WriteFile("remove.txt", "gone");

        var first = await _backupEngine.RunBackupAsync(MakeProfile(), _key, null);
        Assert.True(first.Success);

        File.Delete(Path.Combine(_sourceDir, "remove.txt"));
        var second = await _backupEngine.RunBackupAsync(MakeProfile(), _key, _backupEngine.LastIndex);
        Assert.True(second.Success);
        Assert.Equal(1, second.FilesDeleted);

        // Pre-create the file in restore dir to verify it gets deleted
        Directory.CreateDirectory(_restoreDir);
        File.WriteAllText(Path.Combine(_restoreDir, "remove.txt"), "should be deleted");

        await _restoreEngine.RestoreAsync(_registry, null, _restoreDir, _key, false);
        Assert.Equal("keep", ReadRestored("keep.txt"));
        Assert.False(RestoredExists("remove.txt"));
    }

    [Fact]
    public async Task MultiSource_BackupAndRestore()
    {
        EnsureRegistry();
        var src2 = _sourceDir + "-src2";
        Directory.CreateDirectory(src2);
        try
        {
            WriteFile("a.txt", "from source 1");
            File.WriteAllText(Path.Combine(src2, "b.txt"), "from source 2");

            var profile = MakeProfile();
            profile.SourcePaths = [_sourceDir, src2];
            var result = await _backupEngine.RunBackupAsync(profile, _key, null);
            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.FilesAdded);

            await _restoreEngine.RestoreAsync(_registry, null, _restoreDir, _key, false);
            // Files should be under their source dir name prefixes
            Assert.True(Directory.Exists(_restoreDir));
        }
        finally { try { Directory.Delete(src2, true); } catch { } }
    }

    [Fact]
    public async Task Retention_DeletesOldBackups()
    {
        EnsureRegistry();
        WriteFile("f.txt", "v1");
        var r1 = await _backupEngine.RunBackupAsync(MakeProfile(), _key, null);
        Assert.True(r1.Success);

        WriteFile("f.txt", "v2");
        var r2 = await _backupEngine.RunBackupAsync(MakeProfile(), _key, _backupEngine.LastIndex);
        Assert.True(r2.Success);

        WriteFile("f.txt", "v3");
        var r3 = await _backupEngine.RunBackupAsync(MakeProfile(), _key, _backupEngine.LastIndex);
        Assert.True(r3.Success);

        // Keep only 1 backup
        var svc = NSubstitute.Substitute.For<Cli.IServiceFactory>();
        svc.CreateOrasClient(Arg.Any<string?>(), Arg.Any<string?>()).Returns(_oras);
        await Cli.AppCommands.EnforceRetentionAsync(svc, _registry, 1, null, false, CancellationToken.None);

        var tags = await _oras.ListTagsAsync(_registry);
        var backupTags = tags.Where(t => t != "latest" && !t.StartsWith("chunk-")).ToList();
        Assert.Single(backupTags); // only the newest remains
    }

    [Fact]
    public async Task LargeFile_StreamingRoundTrip()
    {
        EnsureRegistry();
        // Create a file larger than the default test chunk size (1MB)
        var data = new byte[1_500_000];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(Path.Combine(_sourceDir, "large.bin"), data);

        var result = await _backupEngine.RunBackupAsync(MakeProfile(), _key, null);
        Assert.True(result.Success, result.Error);

        await _restoreEngine.RestoreAsync(_registry, null, _restoreDir, _key, false);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_restoreDir, "large.bin")));
    }

    [Fact]
    public async Task DaemonLifecycle_BacksUpAndCachesIndex()
    {
        EnsureRegistry();
        WriteFile("daemon-test.txt", "hello");

        var cacheDir = Path.Combine(Path.GetTempPath(), $"daemon-cache-{Guid.NewGuid():N}");
        var cache = new BackupIndexCache(cacheDir);
        var profile = MakeProfile();
        profile.Schedule.IntervalMinutes = 1;

        using var scheduler = new BackupScheduler(_backupEngine,
            NullLoggerFactory.Instance.CreateLogger<BackupScheduler>(), cache: cache);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await scheduler.RunAsync(profile, _key, cts.Token); }
        catch (OperationCanceledException) { }

        // Verify index was cached
        var cached = cache.Load("v2-int");
        Assert.NotNull(cached);
        Assert.NotEmpty(cached!.Chunks);

        try { Directory.Delete(cacheDir, true); } catch { }
    }
}
