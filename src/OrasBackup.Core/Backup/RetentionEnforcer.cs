using OrasBackup.Core.Config;
using OrasBackup.Core.Oras;

namespace OrasBackup.Core.Backup;

public sealed class RetentionEnforcer
{
    private readonly IOrasClient _oras;

    public RetentionEnforcer(IOrasClient oras) => _oras = oras;

    /// <summary>
    /// Deletes the oldest backup tags when count exceeds maxBackups.
    /// </summary>
    public async Task EnforceAsync(string registry, RetentionConfig config, CancellationToken ct = default)
    {
        var allTags = await _oras.ListTagsAsync(registry, ct);
        var backupTags = allTags.Where(t => t != "latest").ToList();

        if (backupTags.Count <= config.MaxBackups) return;

        var toDelete = backupTags.Take(backupTags.Count - config.MaxBackups).ToList();
        foreach (var tag in toDelete)
            await _oras.DeleteTagAsync(registry, tag, ct);
    }

    /// <summary>
    /// Returns true if the incremental chain is long enough to warrant compaction.
    /// </summary>
    public bool ShouldCompact(int chainLength, int compactAfter) => chainLength > compactAfter;
}
