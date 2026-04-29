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

    /// <summary>The manifest from the last successful backup, for caching.</summary>
    public DeltaManifest? LastManifest { get; private set; }

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
            // 1. Scan ALL source paths into one file list with namespaced paths
            var allCurrentFiles = new List<FileSnapshot>();
            var multiSource = profile.SourcePaths.Count > 1;
            foreach (var source in profile.SourcePaths)
            {
                var files = _delta.ScanDirectory(source, profile.ExcludePatterns);
                if (multiSource)
                {
                    // Prefix with source dir name to prevent collisions
                    var prefix = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, '/'));
                    files = files.Select(f => f with { RelativePath = $"{prefix}/{f.RelativePath}" }).ToList();
                }
                allCurrentFiles.AddRange(files);
            }

            var added = new List<FileSnapshot>();
            var modified = new List<FileSnapshot>();
            var unchanged = new List<FileSnapshot>();
            var deleted = new List<string>();

            if (previous is null)
            {
                added.AddRange(allCurrentFiles);
            }
            else
            {
                var prevByPath = previous.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
                foreach (var file in allCurrentFiles)
                {
                    if (!prevByPath.TryGetValue(file.RelativePath, out var prev))
                        added.Add(file);
                    else if (prev.Sha256 != file.Sha256)
                        modified.Add(file);
                    else
                        unchanged.Add(file);
                }
                var currentPaths = allCurrentFiles.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
                deleted.AddRange(previous.Files.Where(f => !currentPaths.Contains(f.RelativePath)).Select(f => f.RelativePath));
            }

            var changedFiles = added.Concat(modified).ToList();
            _logger.LogInformation("Delta: {Added} added, {Modified} modified, {Deleted} deleted, {Unchanged} unchanged",
                added.Count, modified.Count, deleted.Count, unchanged.Count);

            // 2. Build manifest
            var manifest = new DeltaManifest
            {
                BackupId = backupId,
                BasedOn = previous?.BackupId,
                Files = added.Concat(modified).Concat(unchanged).ToList(),
                Deleted = deleted
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

            // 5. Push to registry with backup ID tag
            var layers = new List<OrasLayer>
            {
                new("application/vnd.orasbackup.manifest+json", encManifest)
            };
            if (encTar.Length > 0)
                layers.Add(new("application/vnd.orasbackup.layer.v1.tar+encrypted", encTar));

            var reference = $"{profile.Registry}:{backupId}";
            await _oras.PushAsync(reference, layers, ct);

            // 6. Also push as :latest for easy restore-without-id
            var latestRef = $"{profile.Registry}:latest";
            await _oras.PushAsync(latestRef, layers, ct);

            LastManifest = manifest;
            sw.Stop();
            _logger.LogInformation("Backup {Id} completed in {Duration}", backupId, sw.Elapsed);

            return new BackupResult(backupId, added.Count, modified.Count, deleted.Count,
                unchanged.Count, encTar.Length, sw.Elapsed, true);
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
        var multiSource = sourcePaths.Count > 1;

        using var ms = new MemoryStream();
        await using (var tar = new TarWriter(ms, leaveOpen: true))
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                string? fullPath = null;

                if (multiSource)
                {
                    // RelativePath is "prefix/actual/path" — match prefix to source dir name
                    var firstSlash = file.RelativePath.IndexOf('/');
                    if (firstSlash > 0)
                    {
                        var prefix = file.RelativePath[..firstSlash];
                        var innerPath = file.RelativePath[(firstSlash + 1)..].Replace('/', Path.DirectorySeparatorChar);
                        fullPath = sourcePaths
                            .Where(s => Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar, '/')) == prefix)
                            .Select(s => Path.Combine(s, innerPath))
                            .FirstOrDefault(File.Exists);
                    }
                }
                else
                {
                    fullPath = sourcePaths
                        .Select(s => Path.Combine(s, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                        .FirstOrDefault(File.Exists);
                }

                if (fullPath is null)
                    throw new FileNotFoundException($"File not found: {file.RelativePath}");

                await tar.WriteEntryAsync(fullPath, file.RelativePath, ct);
            }
        }
        return ms.ToArray();
    }
}
