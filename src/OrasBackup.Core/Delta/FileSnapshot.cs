namespace OrasBackup.Core.Delta;

public sealed record FileSnapshot(
    string RelativePath,
    string Sha256,
    long SizeBytes,
    DateTime LastModifiedUtc);
