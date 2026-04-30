using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;

namespace OrasBackup.Core.Backup;

/// <summary>
/// Pushes a FileChunk as an OCI image: layer 0 = ChunkManifest, layer 1+ = files.
/// Memory usage = one encryption chunk (64MB) regardless of file size.
/// </summary>
public sealed class ChunkEngine : IChunkEngine
{
    private readonly IOrasClient _oras;
    private readonly IEncryptor _encryptor;
    private readonly ILogger<ChunkEngine> _logger;

    public ChunkEngine(IOrasClient oras, IEncryptor encryptor, ILogger<ChunkEngine> logger)
    {
        _oras = oras;
        _encryptor = encryptor;
        _logger = logger;
    }

    public async Task<ChunkRef> PushChunkAsync(
        string registry, FileChunk chunk, IReadOnlyList<string> sourcePaths,
        byte[] encryptionKey, bool encrypt, CancellationToken ct = default)
    {
        var chunkManifest = new ChunkManifest();
        var blobLayers = new List<OrasLayerDescriptor>();

        var layerIndex = 1;
        foreach (var file in chunk.Files)
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = FindFile(sourcePaths, file.RelativePath);

            // Verify file hasn't changed since scan (race between scan and upload)
            var currentFile = file;
            using (var verifyStream = File.OpenRead(fullPath))
            {
                var currentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(verifyStream)).ToLowerInvariant();
                if (currentHash != file.Sha256)
                {
                    _logger.LogWarning("File {File} changed during backup (expected {Expected}, got {Actual}), re-hashing",
                        file.RelativePath, file.Sha256, currentHash);
                    currentFile = file with { Sha256 = currentHash, SizeBytes = verifyStream.Length };
                }
            }

            var mediaType = $"application/vnd.orasbackup.file.v2{(encrypt ? "+encrypted" : "")}";
            OrasLayerDescriptor descriptor;

            if (encrypt)
            {
                var encPath = _encryptor.EncryptFile(fullPath, encryptionKey, ct);
                try { descriptor = await _oras.UploadBlobFromFileAsync(registry, encPath, mediaType, ct); }
                finally { try { File.Delete(encPath); } catch { } }
            }
            else
            {
                descriptor = await _oras.UploadBlobFromFileAsync(registry, fullPath, mediaType, ct);
            }

            blobLayers.Add(descriptor);

            chunkManifest.Files.Add(new ChunkFile
            {
                RelativePath = currentFile.RelativePath,
                Sha256 = currentFile.Sha256,
                Size = currentFile.SizeBytes,
                UnixMode = currentFile.UnixMode,
                LayerIndex = layerIndex
            });

            _logger.LogDebug("Chunk {Path}: added {File} ({Size} bytes) as layer {Index}",
                chunk.Path, file.RelativePath, descriptor.Size, layerIndex);
            layerIndex++;
        }

        // Build manifest layer (small, in-memory is fine)
        var manifestBytes = chunkManifest.Serialize();
        if (encrypt) manifestBytes = _encryptor.Encrypt(manifestBytes, encryptionKey);
        var manifestLayer = new OrasLayer("application/vnd.orasbackup.chunk.manifest+json", manifestBytes);

        var contentHash = ComputeChunkHash(chunk.Files);
        var tag = $"chunk-{contentHash[..16]}";

        await _oras.PushManifestAsync($"{registry}:{tag}", [manifestLayer], blobLayers, ct);

        _logger.LogInformation("Pushed chunk {Path} as {Tag} ({Files} files, {Bytes} bytes)",
            chunk.Path, tag, chunk.Files.Count, chunk.TotalBytes);

        return new ChunkRef
        {
            Path = chunk.Path,
            Tag = tag,
            ContentHash = contentHash,
            FileCount = chunk.Files.Count,
            TotalBytes = chunk.TotalBytes
        };
    }

    private static string FindFile(IReadOnlyList<string> sourcePaths, string relativePath)
    {
        if (sourcePaths.Count > 1)
        {
            var firstSlash = relativePath.IndexOf('/');
            if (firstSlash > 0)
            {
                var prefix = relativePath[..firstSlash];
                var innerPath = relativePath[(firstSlash + 1)..].Replace('/', Path.DirectorySeparatorChar);

                // Match prefix to the correct source directory (handles both "dirName" and "dirName_N" formats)
                foreach (var source in sourcePaths)
                {
                    var dirName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, '/'));
                    // Exact match: "docs" matches "docs", "docs_0" matches source named "docs" at index 0
                    // Use regex to avoid "data_backup" matching source "data" via prefix
                    if (prefix == dirName || System.Text.RegularExpressions.Regex.IsMatch(prefix, $@"^{System.Text.RegularExpressions.Regex.Escape(dirName)}_\d+$"))
                    {
                        var candidate = Path.Combine(source, innerPath);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
        }

        return sourcePaths
            .Select(s => Path.Combine(s, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"File not found: {relativePath}");
    }

    internal static string ComputeChunkHash(IReadOnlyList<FileSnapshot> files)
    {
        var combined = string.Join("|", files.OrderBy(f => f.RelativePath).Select(f => $"{f.RelativePath}:{f.Sha256}"));
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
