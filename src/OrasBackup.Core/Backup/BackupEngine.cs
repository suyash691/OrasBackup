using System.Diagnostics;
using System.Formats.Tar;
using Microsoft.Extensions.Logging;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;

namespace OrasBackup.Core.Backup;

public sealed class BackupEngine
{
    private readonly DeltaTracker _delta;
    private readonly IEncryptor _encryptor;
    private readonly IOrasClient _oras;
    private readonly ILogger<BackupEngine> _logger;

    public BackupEngine(DeltaTracker delta, IEncryptor encryptor, IOrasClient oras, ILogger<BackupEngine> logger)
    {
        _delta = delta;
        _encryptor = encryptor;
        _oras = oras;
        _logger = logger;
    }

    public async Task<BackupResult> RunBackupAsync(BackupProfile profile, byte[] encryptionKey, DeltaManifest? previous, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var backupId = Guid.NewGuid().ToString("N")[..12];

        try
        {
            // 1. Compute delta across all source paths
            var allAdded = new List<FileSnapshot>();
            var allModified = new List<FileSnapshot>();
            var allDeleted = new List<string>();
            var allUnchanged = new List<FileSnapshot>();

            foreach (var source in profile.SourcePaths)
            {
                var result = _delta.ComputeDelta(source, previous, profile.ExcludePatterns);
                allAdded.AddRange(result.Added);
                allModified.AddRange(result.Modified);
                allDeleted.AddRange(result.Deleted);
                allUnchanged.AddRange(result.Unchanged);
            }

            var changedFiles = allAdded.Concat(allModified).ToList();
            _logger.LogInformation("Delta: {Added} added, {Modified} modified, {Deleted} deleted, {Unchanged} unchanged",
                allAdded.Count, allModified.Count, allDeleted.Count, allUnchanged.Count);

            // 2. Build manifest
            var manifest = new DeltaManifest
            {
                BackupId = backupId,
                BasedOn = previous?.BackupId,
                Files = allAdded.Concat(allModified).Concat(allUnchanged).ToList(),
                Deleted = allDeleted
            };

            // 3. Tar changed files
            var tarBytes = await CreateTarAsync(profile.SourcePaths, changedFiles, ct);

            // 4. Encrypt
            var encManifest = profile.Encryption.Enabled
                ? _encryptor.Encrypt(manifest.Serialize(), encryptionKey)
                : manifest.Serialize();

            var encTar = profile.Encryption.Enabled && tarBytes.Length > 0
                ? _encryptor.Encrypt(tarBytes, encryptionKey)
                : tarBytes;

            // 5. Push to registry
            var layers = new List<OrasLayer>
            {
                new("application/vnd.orasbackup.manifest+json", encManifest)
            };
            if (encTar.Length > 0)
                layers.Add(new("application/vnd.orasbackup.layer.v1.tar+encrypted", encTar));

            var reference = $"{profile.Registry}:{backupId}";
            await _oras.PushAsync(reference, layers, ct);

            sw.Stop();
            _logger.LogInformation("Backup {Id} completed in {Duration}", backupId, sw.Elapsed);

            return new BackupResult(backupId, allAdded.Count, allModified.Count, allDeleted.Count,
                allUnchanged.Count, encTar.Length, sw.Elapsed, true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Backup failed");
            return new BackupResult(backupId, 0, 0, 0, 0, 0, sw.Elapsed, false, ex.Message);
        }
    }

    private static async Task<byte[]> CreateTarAsync(IReadOnlyList<string> sourcePaths, IReadOnlyList<FileSnapshot> files, CancellationToken ct)
    {
        if (files.Count == 0) return [];

        using var ms = new MemoryStream();
        await using (var tar = new TarWriter(ms, leaveOpen: true))
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                // Find the actual file path from source paths
                var fullPath = sourcePaths
                    .Select(s => Path.Combine(s, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                    .FirstOrDefault(File.Exists)
                    ?? throw new FileNotFoundException($"File not found: {file.RelativePath}");

                await tar.WriteEntryAsync(fullPath, file.RelativePath, ct);
            }
        }
        return ms.ToArray();
    }
}
