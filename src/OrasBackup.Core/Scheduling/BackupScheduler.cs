using Microsoft.Extensions.Logging;
using OrasBackup.Core.Config;
using OrasBackup.Core.Delta;

namespace OrasBackup.Core.Scheduling;

public sealed class BackupScheduler : IDisposable
{
    private readonly Backup.BackupEngine _engine;
    private readonly ILogger<BackupScheduler> _logger;
    private PeriodicTimer? _timer;
    private DeltaManifest? _lastManifest;

    public BackupScheduler(Backup.BackupEngine engine, ILogger<BackupScheduler> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task RunAsync(BackupProfile profile, byte[] encryptionKey, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(profile.Schedule.IntervalMinutes);
        _logger.LogInformation("Scheduler started: backing up every {Interval}", interval);

        // Run immediately on start
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
                _lastManifest = new DeltaManifest { BackupId = result.BackupId };
                _logger.LogInformation("Scheduled backup {Id} succeeded: +{A} ~{M} -{D}",
                    result.BackupId, result.FilesAdded, result.FilesModified, result.FilesDeleted);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled backup failed");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
