using Microsoft.Extensions.Logging;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;

namespace OrasBackup.Core.Backup;

public sealed class CompactionEngine
{
    private readonly RestoreEngine _restore;
    private readonly BackupEngine _backup;
    private readonly ILogger<CompactionEngine> _logger;

    public CompactionEngine(RestoreEngine restore, BackupEngine backup, ILogger<CompactionEngine> logger)
    {
        _restore = restore;
        _backup = backup;
        _logger = logger;
    }

    /// <summary>
    /// Compacts the delta chain into a single full backup.
    /// Restores the full chain to a temp dir, then creates a fresh full backup from it.
    /// </summary>
    public async Task<BackupResult> CompactAsync(BackupProfile profile, byte[] encryptionKey, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"orasbackup-compact-{Guid.NewGuid():N}");
        try
        {
            _logger.LogInformation("Compacting: restoring full chain to temp directory");

            // 1. Restore the full chain
            var restoreOpts = new RestoreOptions(profile.Registry, null, tempDir, encryptionKey, profile.Encryption.Enabled);
            await _restore.RestoreAsync(restoreOpts, ct);

            // 2. Create a new full backup from the restored state (no basedOn)
            _logger.LogInformation("Compacting: creating fresh full backup from restored state");
            var compactProfile = new BackupProfile
            {
                Name = profile.Name,
                SourcePaths = [tempDir],
                Registry = profile.Registry,
                Encryption = profile.Encryption,
                ExcludePatterns = [] // already filtered
            };

            return await _backup.RunBackupAsync(compactProfile, encryptionKey, null, ct);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }
}
