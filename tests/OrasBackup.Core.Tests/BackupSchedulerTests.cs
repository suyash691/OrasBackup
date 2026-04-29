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
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"orastest-sched-{Guid.NewGuid():N}");
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();

    public BackupSchedulerTests()
    {
        Directory.CreateDirectory(_tempDir);
        _encryptor.Encrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task RunAsync_ExecutesImmediatelyOnStart()
    {
        File.WriteAllText(Path.Combine(_tempDir, "f.txt"), "data");
        var engine = MakeEngine();
        using var scheduler = new BackupScheduler(engine, NullLogger<BackupScheduler>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try { await scheduler.RunAsync(MakeProfile(), new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        // Should have pushed at least once (immediate run)
        await _oras.Received().PushAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<OrasLayer>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_StopsOnCancellation()
    {
        var engine = MakeEngine();
        using var scheduler = new BackupScheduler(engine, NullLogger<BackupScheduler>.Instance);
        using var cts = new CancellationTokenSource();

        var task = scheduler.RunAsync(MakeProfile(), new byte[32], cts.Token);
        await Task.Delay(500);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task RunAsync_UsesLastManifestForIncremental()
    {
        File.WriteAllText(Path.Combine(_tempDir, "f.txt"), "data");
        var engine = MakeEngine();
        using var scheduler = new BackupScheduler(engine, NullLogger<BackupScheduler>.Instance);

        // Run with very short interval so it fires twice
        var profile = MakeProfile();
        profile.Schedule.IntervalMinutes = 0; // will use minimum tick
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try { await scheduler.RunAsync(profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        // After first run, engine.LastManifest should be set
        Assert.NotNull(engine.LastManifest);
    }

    private BackupEngine MakeEngine() =>
        new(new DeltaTracker(), _encryptor, _oras, NullLogger<BackupEngine>.Instance);

    private BackupProfile MakeProfile() => new()
    {
        Name = "sched-test",
        SourcePaths = [_tempDir],
        Registry = "registry.example.com/test",
        Encryption = new EncryptionConfig { Enabled = false },
        Schedule = new ScheduleConfig { Enabled = true, IntervalMinutes = 1 }
    };
}
