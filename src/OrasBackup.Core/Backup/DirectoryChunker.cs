using OrasBackup.Core.Delta;

namespace OrasBackup.Core.Backup;

/// <summary>
/// Groups files into chunks by directory structure.
/// Each chunk targets a configurable max size. Small directories merge with parent,
/// large directories split into sub-chunks.
/// </summary>
public sealed class DirectoryChunker
{
    private readonly long _maxChunkBytes;
    private readonly long _minChunkBytes;

    public DirectoryChunker(long maxChunkBytes = 256 * 1024 * 1024, long minChunkBytes = 10 * 1024 * 1024)
    {
        _maxChunkBytes = maxChunkBytes;
        _minChunkBytes = minChunkBytes;
    }

    public List<FileChunk> BuildChunks(IReadOnlyList<FileSnapshot> files)
    {
        if (files.Count == 0) return [];

        // Group files by their top-level directory
        var groups = files
            .GroupBy(f => GetTopDir(f.RelativePath))
            .OrderBy(g => g.Key)
            .ToList();

        var chunks = new List<FileChunk>();
        var overflow = new List<FileSnapshot>(); // small dirs that need merging

        foreach (var group in groups)
        {
            var groupFiles = group.ToList();
            var groupSize = groupFiles.Sum(f => f.SizeBytes);

            if (groupSize > _maxChunkBytes)
            {
                // Split large directory into sub-chunks by size
                FlushOverflow(overflow, chunks);
                SplitIntoChunks(group.Key, groupFiles, chunks);
            }
            else if (groupSize < _minChunkBytes)
            {
                // Accumulate small directories
                overflow.AddRange(groupFiles);
                var overflowSize = overflow.Sum(f => f.SizeBytes);
                if (overflowSize >= _minChunkBytes)
                    FlushOverflow(overflow, chunks);
            }
            else
            {
                // Medium directory — merge any accumulated small dirs into this chunk
                var merged = new List<FileSnapshot>(overflow);
                merged.AddRange(groupFiles);
                overflow.Clear();
                chunks.Add(new FileChunk(group.Key, merged));
            }
        }

        FlushOverflow(overflow, chunks);
        return chunks;
    }

    private void SplitIntoChunks(string basePath, List<FileSnapshot> files, List<FileChunk> chunks)
    {
        var current = new List<FileSnapshot>();
        long currentSize = 0;
        var partIndex = 0;

        foreach (var file in files.OrderBy(f => f.RelativePath))
        {
            if (currentSize + file.SizeBytes > _maxChunkBytes && current.Count > 0)
            {
                chunks.Add(new FileChunk($"{basePath}/part-{partIndex++}", current));
                current = [];
                currentSize = 0;
            }
            current.Add(file);
            currentSize += file.SizeBytes;
        }

        if (current.Count > 0)
            chunks.Add(new FileChunk($"{basePath}/part-{partIndex}", current));
    }

    private static void FlushOverflow(List<FileSnapshot> overflow, List<FileChunk> chunks)
    {
        if (overflow.Count == 0) return;
        var path = GetCommonPrefix(overflow);
        chunks.Add(new FileChunk(path, new List<FileSnapshot>(overflow)));
        overflow.Clear();
    }

    private static string GetTopDir(string relativePath)
    {
        var slash = relativePath.IndexOf('/');
        return slash > 0 ? relativePath[..slash] : "(root)";
    }

    private static string GetCommonPrefix(List<FileSnapshot> files)
    {
        if (files.Count == 0) return "(root)";
        var dirs = files.Select(f => GetTopDir(f.RelativePath)).Distinct().ToList();
        return dirs.Count == 1 ? dirs[0] : "mixed";
    }
}

/// <summary>A group of files that will become one OCI chunk image.</summary>
public sealed record FileChunk(string Path, List<FileSnapshot> Files)
{
    public long TotalBytes => Files.Sum(f => f.SizeBytes);
}
