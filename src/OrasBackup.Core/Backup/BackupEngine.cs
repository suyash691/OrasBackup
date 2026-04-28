using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;

namespace OrasBackup.Core.Backup;

public sealed class BackupEngine
{
    private readonly DeltaTracker _delta;
    private readonly ChunkEngine _chunkEngine;
    private readonly IOrasClient _oras;
    private readonly IEncryptor _encryptor;
    private readonly ILogger<BackupEngine> _logger;
    private readonly DirectoryChunker _chunker;

    public BackupIndex? LastIndex { get; private set; }
    public IReadOnlyList<FileSnapshot>? LastSnapshots { get; private set; }

    public BackupEngine(DeltaTracker delta, ChunkEngine chunkEngine, IOrasClient oras,
        IEncryptor encryptor, ILogger<BackupEngine> logger, DirectoryChunker? chunker = null)
    {
        _delta = delta;
        _chunkEngine = chunkEngine;
        _oras = oras;
        _encryptor = encryptor;
        _logger = logger;
        _chunker = chunker ?? new DirectoryChunker();
    }

    public async Task<BackupResult> RunBackupAsync(BackupProfile profile, byte[] encryptionKey, BackupIndex? previous, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var backupId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            if (profile.Encryption.Enabled && encryptionKey.Length != 32)
                throw new ArgumentException($"Encryption key must be 32 bytes, got {encryptionKey.Length}");

            // 1. Scan all source paths
            var allFiles = new List<FileSnapshot>();
            var multiSource = profile.SourcePaths.Count > 1;
            var dirNames = profile.SourcePaths.Select(s => Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar, '/'))).ToList();
            var hasDuplicates = dirNames.Distinct().Count() != dirNames.Count;

            for (var i = 0; i < profile.SourcePaths.Count; i++)
            {
                var source = profile.SourcePaths[i];
                if (!Directory.Exists(source))
                    throw new DirectoryNotFoundException($"Source path does not exist: {source}");

                var files = _delta.ScanDirectory(source, profile.ExcludePatterns, LastSnapshots);
                if (multiSource)
                {
                    var prefix = hasDuplicates ? $"{dirNames[i]}_{i}" : dirNames[i];
                    files = files.Select(f => f with { RelativePath = $"{prefix}/{f.RelativePath}" }).ToList();
                }
                allFiles.AddRange(files);
            }

            // 2. Determine what changed (skip unchanged chunks)
            var previousChunkHashes = previous?.Chunks.ToDictionary(c => c.Path, c => c.ContentHash) ?? new();

            // 3. Build chunks from directory tree
            var chunks = _chunker.BuildChunks(allFiles);
            _logger.LogInformation("Scan complete: {Files} files in {Chunks} chunks", allFiles.Count, chunks.Count);

            // 4. Push each chunk (skip if content hash unchanged)
            var chunkRefs = new List<ChunkRef>();
            var filesAdded = 0;
            var filesSkipped = 0;

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();

                // Compute content hash to check if chunk changed
                var contentHash = ChunkEngine.ComputeChunkHash(chunk.Files);
                if (previousChunkHashes.TryGetValue(chunk.Path, out var prevHash) && prevHash == contentHash)
                {
                    // Chunk unchanged — reuse previous ref
                    chunkRefs.Add(previous!.Chunks.First(c => c.Path == chunk.Path));
                    filesSkipped += chunk.Files.Count;
                    _logger.LogDebug("Chunk {Path} unchanged, skipping", chunk.Path);
                    continue;
                }

                var chunkRef = await _chunkEngine.PushChunkAsync(
                    profile.Registry, chunk, profile.SourcePaths, encryptionKey, profile.Encryption.Enabled, ct);
                chunkRefs.Add(chunkRef);
                filesAdded += chunk.Files.Count;
            }

            // 5. Detect deleted files
            var currentPaths = allFiles.Select(f => f.RelativePath).ToHashSet();
            var deletedFiles = previous?.AllFiles
                .Where(f => !currentPaths.Contains(f))
                .ToList() ?? [];

            // 6. Build and push root index
            var index = new BackupIndex
            {
                BackupId = backupId,
                TimestampUtc = DateTime.UtcNow,
                Encrypted = profile.Encryption.Enabled,
                Chunks = chunkRefs,
                DeletedFiles = deletedFiles,
                AllFiles = allFiles.Select(f => f.RelativePath).ToList()
            };

            var indexBytes = index.Serialize();
            if (profile.Encryption.Enabled)
                indexBytes = _encryptor.Encrypt(indexBytes, encryptionKey);

            var indexLayers = new List<OrasLayer> { new("application/vnd.orasbackup.index.v2+json", indexBytes) };
            await _oras.PushAsync($"{profile.Registry}:{backupId}", indexLayers, ct);
            await _oras.TagAsync(profile.Registry, backupId, "latest", ct);

            LastIndex = index;
            LastSnapshots = allFiles;
            sw.Stop();

            _logger.LogInformation("Backup {Id}: {Added} files pushed, {Skipped} unchanged, {Chunks} chunks ({Duration})",
                backupId, filesAdded, filesSkipped, chunkRefs.Count, sw.Elapsed);

            return new BackupResult(backupId, filesAdded, deletedFiles.Count,
                filesSkipped, allFiles.Sum(f => f.SizeBytes), sw.Elapsed, true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Backup failed");
            return new BackupResult(backupId, 0, 0, 0, 0, sw.Elapsed, false, ex.Message);
        }
    }

}
