using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;
using Xunit;

namespace OrasBackup.Core.Tests;

public class BackupSchedulerTests : IDisposable
{
    private readonly string _srcDir = Path.Combine(Path.GetTempPath(), $"sched-test-{Guid.NewGuid():N}");

    public BackupSchedulerTests() { Directory.CreateDirectory(_srcDir); File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "x"); }
    public void Dispose() { try { Directory.Delete(_srcDir, true); } catch { } }

    [Fact]
    public async Task RunOnce_Success_SetsLastIndex()
    {
        var oras = Substitute.For<IOrasClient>();
        var encryptor = Substitute.For<IEncryptor>();
        encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        oras.UploadBlobFromFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrasLayerDescriptor(ci.ArgAt<string>(2), "sha256:blob", 100));
        var engine = new BackupEngine(new DeltaTracker(), new ChunkEngine(oras, encryptor, NullLogger<ChunkEngine>.Instance),
            oras, encryptor, NullLogger<BackupEngine>.Instance);

        using var scheduler = new BackupScheduler(engine, NullLogger<BackupScheduler>.Instance);

        var profile = new BackupProfile
        {
            Name = "test",
            SourcePaths = [_srcDir],
            Registry = "reg/repo",
            Schedule = new ScheduleConfig { IntervalMinutes = 1 },
            Encryption = new EncryptionConfig { Enabled = false }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await scheduler.RunAsync(profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        Assert.NotNull(engine.LastIndex);
    }

    [Fact]
    public async Task RunOnce_SavesIndexToCache()
    {
        var oras = Substitute.For<IOrasClient>();
        var encryptor = Substitute.For<IEncryptor>();
        encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        oras.UploadBlobFromFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrasLayerDescriptor(ci.ArgAt<string>(2), "sha256:blob", 100));
        var engine = new BackupEngine(new DeltaTracker(), new ChunkEngine(oras, encryptor, NullLogger<ChunkEngine>.Instance),
            oras, encryptor, NullLogger<BackupEngine>.Instance);

        var cacheDir = Path.Combine(Path.GetTempPath(), $"sched-cache-{Guid.NewGuid():N}");
        var cache = new BackupIndexCache(cacheDir);

        using var scheduler = new BackupScheduler(engine, NullLogger<BackupScheduler>.Instance, cache: cache);

        var profile = new BackupProfile
        {
            Name = "cachetest", SourcePaths = [_srcDir], Registry = "reg/repo",
            Schedule = new ScheduleConfig { IntervalMinutes = 1 },
            Encryption = new EncryptionConfig { Enabled = false }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await scheduler.RunAsync(profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        Assert.NotNull(cache.Load("cachetest"));
        try { Directory.Delete(cacheDir, true); } catch { }
    }

    [Fact]
    public async Task RunOnce_InvokesRetentionRunner()
    {
        var oras = Substitute.For<IOrasClient>();
        var encryptor = Substitute.For<IEncryptor>();
        encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        oras.UploadBlobFromFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrasLayerDescriptor(ci.ArgAt<string>(2), "sha256:blob", 100));
        var engine = new BackupEngine(new DeltaTracker(), new ChunkEngine(oras, encryptor, NullLogger<ChunkEngine>.Instance),
            oras, encryptor, NullLogger<BackupEngine>.Instance);

        var retentionCalled = false;
        using var scheduler = new BackupScheduler(engine, NullLogger<BackupScheduler>.Instance,
            retentionRunner: (_, _, _) => { retentionCalled = true; return Task.CompletedTask; });

        var profile = new BackupProfile
        {
            Name = "rettest", SourcePaths = [_srcDir], Registry = "reg/repo",
            Schedule = new ScheduleConfig { IntervalMinutes = 1 },
            Encryption = new EncryptionConfig { Enabled = false }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await scheduler.RunAsync(profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        Assert.True(retentionCalled);
    }
}
