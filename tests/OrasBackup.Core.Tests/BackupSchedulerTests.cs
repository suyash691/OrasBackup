using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Scheduling;
using Xunit;

namespace OrasBackup.Core.Tests;

public class BackupSchedulerTests
{
    private readonly IBackupEngine _engine = Substitute.For<IBackupEngine>();
    private readonly IBackupIndexCache _cache = Substitute.For<IBackupIndexCache>();

    private readonly BackupProfile _profile = new()
    {
        Name = "test", SourcePaths = ["/data"], Registry = "reg/repo",
        Schedule = new ScheduleConfig { IntervalMinutes = 1 },
        Encryption = new EncryptionConfig { Enabled = false }
    };

    public BackupSchedulerTests()
    {
        _engine.RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(), Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>())
            .Returns(new BackupResult("b1", 1, 0, 0, 100, TimeSpan.FromSeconds(1), true));
        _engine.LastIndex.Returns(new BackupIndex { BackupId = "b1" });
    }

    [Fact]
    public async Task RunOnce_SetsLastIndex()
    {
        using var scheduler = new BackupScheduler(_engine, NullLogger<BackupScheduler>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await scheduler.RunAsync(_profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        await _engine.Received().RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(), Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnce_SavesIndexToCache()
    {
        using var scheduler = new BackupScheduler(_engine, NullLogger<BackupScheduler>.Instance, cache: _cache);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await scheduler.RunAsync(_profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        _cache.Received().Save("test", Arg.Any<BackupIndex>());
    }

    [Fact]
    public async Task RunOnce_InvokesRetentionRunner()
    {
        var retentionCalled = false;
        using var scheduler = new BackupScheduler(_engine, NullLogger<BackupScheduler>.Instance,
            retentionRunner: (_, _, _) => { retentionCalled = true; return Task.CompletedTask; });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await scheduler.RunAsync(_profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        Assert.True(retentionCalled);
    }

    [Fact]
    public async Task RunOnce_LoadsCachedIndex()
    {
        var cached = new BackupIndex { BackupId = "cached" };
        _cache.Load("test").Returns(cached);

        using var scheduler = new BackupScheduler(_engine, NullLogger<BackupScheduler>.Instance, cache: _cache);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await scheduler.RunAsync(_profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        // First RunBackupAsync call should receive the cached index
        await _engine.Received().RunBackupAsync(_profile, Arg.Any<byte[]>(), cached, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnce_EngineFailure_DoesNotCrash()
    {
        _engine.RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(), Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>())
            .Returns(new BackupResult("b1", 0, 0, 0, 0, TimeSpan.Zero, false, "disk full"));

        using var scheduler = new BackupScheduler(_engine, NullLogger<BackupScheduler>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await scheduler.RunAsync(_profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        // Should not save to cache on failure
        _cache.DidNotReceive().Save(Arg.Any<string>(), Arg.Any<BackupIndex>());
    }

    [Fact]
    public async Task RunOnce_EngineThrows_ContinuesRunning()
    {
        _engine.RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(), Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>())
            .Returns<BackupResult>(_ => throw new IOException("disk error"));

        using var scheduler = new BackupScheduler(_engine, NullLogger<BackupScheduler>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await scheduler.RunAsync(_profile, new byte[32], cts.Token); }
        catch (OperationCanceledException) { }

        // Should have attempted at least once without crashing
        await _engine.Received().RunBackupAsync(Arg.Any<BackupProfile>(), Arg.Any<byte[]>(),
            Arg.Any<BackupIndex?>(), Arg.Any<CancellationToken>());
    }
}
