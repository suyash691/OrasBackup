namespace OrasBackup.Core.Backup;

public sealed record BackupResult(
    string BackupId,
    int FilesAdded,
    int FilesModified,
    int FilesDeleted,
    int FilesUnchanged,
    long TotalBytes,
    TimeSpan Duration,
    bool Success,
    string? Error = null);
