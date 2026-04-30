using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Oras;

namespace OrasBackup.Core.Backup;

public sealed class RestoreEngine : IRestoreEngine
{
    private readonly IOrasClient _oras;
    private readonly IEncryptor _encryptor;
    private readonly ILogger<RestoreEngine> _logger;

    public RestoreEngine(IOrasClient oras, IEncryptor encryptor, ILogger<RestoreEngine> logger)
    {
        _oras = oras;
        _encryptor = encryptor;
        _logger = logger;
    }

    public async Task RestoreAsync(string registry, string? backupId, string targetDir,
        byte[] encryptionKey, bool encrypted, CancellationToken ct = default)
    {
        var tag = backupId ?? "latest";
        var targetDirFull = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;

        // 1. Pull root index (small — in-memory is fine)
        var indexRef = $"{registry}:{tag}";
        var indexLayers = await _oras.FetchManifestLayersAsync(indexRef, ct);
        var indexLayer = indexLayers.FirstOrDefault(l => l.MediaType.Contains("index.v2"))
            ?? throw new InvalidOperationException($"No v2 index layer in {indexRef}");

        var indexData = await _oras.PullLayerAsync(indexRef, indexLayer.Digest, ct);
        if (encrypted) indexData = _encryptor.Decrypt(indexData, encryptionKey);
        var index = BackupIndex.Deserialize(indexData);

        _logger.LogInformation("Restoring backup {Id}: {Chunks} chunks", index.BackupId, index.Chunks.Count);
        Directory.CreateDirectory(targetDir);

        // 2. Pull each chunk and extract files
        foreach (var chunkRef in index.Chunks)
        {
            ct.ThrowIfCancellationRequested();

            var chunkImageRef = $"{registry}:{chunkRef.Tag}";
            var chunkLayers = await _oras.FetchManifestLayersAsync(chunkImageRef, ct);

            var manifestLayer = chunkLayers.FirstOrDefault(l => l.MediaType.Contains("chunk.manifest"))
                ?? throw new InvalidOperationException($"No chunk manifest in {chunkImageRef}");

            var manifestData = await _oras.PullLayerAsync(chunkImageRef, manifestLayer.Digest, ct);
            if (encrypted) manifestData = _encryptor.Decrypt(manifestData, encryptionKey);
            var chunkManifest = ChunkManifest.Deserialize(manifestData);

            _logger.LogInformation("Restoring chunk {Path}: {Files} files", chunkRef.Path, chunkManifest.Files.Count);

            foreach (var file in chunkManifest.Files)
            {
                ct.ThrowIfCancellationRequested();

                if (file.LayerIndex >= chunkLayers.Count)
                    throw new InvalidOperationException($"Layer index {file.LayerIndex} out of range for {file.RelativePath}");

                // Path traversal protection
                var filePath = Path.GetFullPath(Path.Combine(targetDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!filePath.StartsWith(targetDirFull))
                    throw new InvalidOperationException($"Path traversal detected: {file.RelativePath}");

                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                var fileLayer = chunkLayers[file.LayerIndex];

                if (encrypted)
                {
                    var tempPath = filePath + ".tmp";
                    try
                    {
                        await _oras.PullLayerToFileAsync(chunkImageRef, fileLayer.Digest, tempPath, ct);
                        var decPath = _encryptor.DecryptFile(tempPath, encryptionKey, ct);
                        File.Move(decPath, filePath, overwrite: true);
                    }
                    finally
                    {
                        try { File.Delete(filePath + ".tmp"); } catch { }
                        try { File.Delete(filePath + ".tmp.dec"); } catch { }
                    }
                }
                else
                {
                    await _oras.PullLayerToFileAsync(chunkImageRef, fileLayer.Digest, filePath, ct);
                }

                // Integrity verification — mandatory, fail if hash missing
                if (string.IsNullOrEmpty(file.Sha256))
                    throw new InvalidOperationException($"Missing SHA-256 hash for {file.RelativePath} — possible tampered manifest");
                using var hashStream = File.OpenRead(filePath);
                var actual = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();
                if (actual != file.Sha256)
                    throw new InvalidOperationException($"Integrity check failed for {file.RelativePath}: expected {file.Sha256}, got {actual}");

                if (file.UnixMode != 0 && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                    File.SetUnixFileMode(filePath, (UnixFileMode)file.UnixMode);

                _logger.LogDebug("Restored {File}", file.RelativePath);
            }
        }

        // 3. Apply deletions (with path traversal protection)
        foreach (var deleted in index.DeletedFiles)
        {
            var path = Path.GetFullPath(Path.Combine(targetDir, deleted.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(targetDirFull))
            {
                _logger.LogWarning("Path traversal in DeletedFiles, skipping: {File}", deleted);
                continue;
            }
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Deleted {File}", deleted);
            }
        }

        _logger.LogInformation("Restore complete to {Dir}", targetDir);
    }
}
