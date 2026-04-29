using Microsoft.Extensions.Logging;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Delta;

namespace OrasBackup.Core.Scheduling;

public sealed class BackupScheduler : IDisposable
{
    private readonly BackupEngine _engine;
    private readonly ILogger<BackupScheduler> _logger;
    private readonly HealthServer? _health;
    private readonly RetentionEnforcer? _retention;
    private PeriodicTimer? _timer;
    private DeltaManifest? _lastManifest;

    public BackupScheduler(BackupEngine engine, ILogger<BackupScheduler> logger, HealthServer? health = null, RetentionEnforcer? retention = null)
    {
        _engine = engine;
        _logger = logger;
        _health = health;
        _retention = retention;
    }

    public async Task RunAsync(BackupProfile profile, byte[] encryptionKey, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(profile.Schedule.IntervalMinutes, 1));
        _logger.LogInformation("Scheduler started: backing up every {Interval}", interval);

        await RunOnceAsync(profile, encryptionKey, ct);

        _timer = new PeriodicTimer(interval);
        while (await _timer.WaitForNextTickAsync(ct))
        {
            await RunOnceAsync(profile, encryptionKey, ct);
        }
    }

    private async Task RunOnceAsync(BackupProfile profile, byte[] encryptionKey, CancellationToken ct)
    {
        try
        {
            var result = await _engine.RunBackupAsync(profile, encryptionKey, _lastManifest, ct);
            if (result.Success)
            {
                _lastManifest = _engine.LastManifest;
                _health?.UpdateStatus(true, result.BackupId);
                _logger.LogInformation("Scheduled backup {Id} succeeded: +{A} ~{M} -{D}",
                    result.BackupId, result.FilesAdded, result.FilesModified, result.FilesDeleted);

                // Run retention enforcement
                if (_retention is not null)
                {
                    await _retention.EnforceAsync(profile.Registry, profile.Retention, ct);
                    _logger.LogDebug("Retention enforced");
                }
            }
            else
            {
                _health?.UpdateStatus(false, result.BackupId);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _health?.UpdateStatus(false, "error");
            _logger.LogError(ex, "Scheduled backup failed");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
