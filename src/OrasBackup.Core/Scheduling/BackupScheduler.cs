using Microsoft.Extensions.Logging;
using OrasBackup.Core.Config;
using OrasBackup.Core.Backup;

namespace OrasBackup.Core.Scheduling;

public sealed class BackupScheduler : IDisposable
{
    private readonly BackupEngine _engine;
    private readonly ILogger<BackupScheduler> _logger;
    private readonly HealthServer? _health;
    private readonly BackupIndexCache? _cache;
    private readonly Func<BackupProfile, byte[], CancellationToken, Task>? _retentionRunner;
    private PeriodicTimer? _timer;

    public BackupScheduler(BackupEngine engine, ILogger<BackupScheduler> logger,
        HealthServer? health = null, BackupIndexCache? cache = null,
        Func<BackupProfile, byte[], CancellationToken, Task>? retentionRunner = null)
    {
        _engine = engine;
        _logger = logger;
        _health = health;
        _cache = cache;
        _retentionRunner = retentionRunner;
    }

    public async Task RunAsync(BackupProfile profile, byte[] encryptionKey, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(profile.Schedule.IntervalMinutes, 1));
        _logger.LogInformation("Scheduler started: backing up every {Interval}", interval);

        // Load cached index so first backup after restart can skip unchanged chunks
        var lastIndex = _cache?.Load(profile.Name);
        if (lastIndex != null)
            _logger.LogInformation("Loaded cached index for incremental backup");

        lastIndex = await RunOnceAsync(profile, encryptionKey, lastIndex, ct);

        _timer = new PeriodicTimer(interval);
        while (await _timer.WaitForNextTickAsync(ct))
            lastIndex = await RunOnceAsync(profile, encryptionKey, lastIndex, ct);
    }

    private async Task<BackupIndex?> RunOnceAsync(BackupProfile profile, byte[] encryptionKey,
        BackupIndex? lastIndex, CancellationToken ct)
    {
        try
        {
            var result = await _engine.RunBackupAsync(profile, encryptionKey, lastIndex, ct);
            if (result.Success)
            {
                var newIndex = _engine.LastIndex;
                _cache?.Save(profile.Name, newIndex!);
                _health?.UpdateStatus(true, result.BackupId);
                _logger.LogInformation("Scheduled backup {Id} succeeded: +{A} -{D}",
                    result.BackupId, result.FilesAdded, result.FilesDeleted);

                // Run retention after each successful backup
                if (_retentionRunner != null)
                {
                    try { await _retentionRunner(profile, encryptionKey, ct); }
                    catch (Exception rex) { _logger.LogWarning(rex, "Retention enforcement failed"); }
                }

                return newIndex;
            }
            _health?.UpdateStatus(false, result.BackupId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _health?.UpdateStatus(false, "error");
            _logger.LogError(ex, "Scheduled backup failed");
        }
        return lastIndex;
    }

    public void Dispose() => _timer?.Dispose();
}
