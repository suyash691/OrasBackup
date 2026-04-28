using System.Security.Cryptography;

namespace OrasBackup.Core.Delta;

public sealed record DeltaResult(
    List<FileSnapshot> Added,
    List<FileSnapshot> Modified,
    List<string> Deleted,
    List<FileSnapshot> Unchanged);

public sealed class DeltaTracker
{
    /// <summary>
    /// Scans <paramref name="sourceDir"/> and computes the delta against <paramref name="previous"/>.
    /// </summary>
    public DeltaResult ComputeDelta(string sourceDir, DeltaManifest? previous, IReadOnlyList<string> excludePatterns)
    {
        var currentFiles = ScanDirectory(sourceDir, excludePatterns);
        if (previous is null)
            return new DeltaResult(currentFiles, [], [], []);

        var prevByPath = previous.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var added = new List<FileSnapshot>();
        var modified = new List<FileSnapshot>();
        var unchanged = new List<FileSnapshot>();

        foreach (var file in currentFiles)
        {
            if (!prevByPath.TryGetValue(file.RelativePath, out var prev))
                added.Add(file);
            else if (prev.Sha256 != file.Sha256)
                modified.Add(file);
            else
                unchanged.Add(file);
        }

        var currentPaths = currentFiles.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        var deleted = previous.Files
            .Where(f => !currentPaths.Contains(f.RelativePath))
            .Select(f => f.RelativePath)
            .ToList();

        return new DeltaResult(added, modified, deleted, unchanged);
    }

    public List<FileSnapshot> ScanDirectory(string sourceDir, IReadOnlyList<string> excludePatterns)
    {
        var root = new DirectoryInfo(sourceDir);
        if (!root.Exists) throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        var matchers = excludePatterns.Select(p => new GlobMatcher(p)).ToList();
        var snapshots = new List<FileSnapshot>();

        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file.FullName).Replace('\\', '/');
            if (matchers.Any(m => m.IsMatch(relative))) continue;

            snapshots.Add(new FileSnapshot(
                relative,
                HashFile(file.FullName),
                file.Length,
                file.LastWriteTimeUtc));
        }

        return snapshots;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>Minimal glob matcher supporting ** and * wildcards.</summary>
internal sealed class GlobMatcher
{
    private readonly string _pattern;

    public GlobMatcher(string pattern) => _pattern = pattern;

    public bool IsMatch(string path)
    {
        // Convert glob to simple regex-like matching
        var segments = _pattern.Replace('\\', '/').Split('/');
        var pathSegments = path.Split('/');
        return Match(segments, 0, pathSegments, 0);
    }

    private static bool Match(string[] pattern, int pi, string[] path, int si)
    {
        while (pi < pattern.Length && si < path.Length)
        {
            if (pattern[pi] == "**")
            {
                // ** matches zero or more path segments
                for (var skip = si; skip <= path.Length; skip++)
                    if (Match(pattern, pi + 1, path, skip))
                        return true;
                return false;
            }

            if (!SegmentMatch(pattern[pi], path[si])) return false;
            pi++;
            si++;
        }

        // Consume trailing **
        while (pi < pattern.Length && pattern[pi] == "**") pi++;
        return pi == pattern.Length && si == path.Length;
    }

    private static bool SegmentMatch(string pattern, string segment)
    {
        if (pattern == "*") return true;
        // Simple wildcard: *.ext
        if (pattern.StartsWith('*'))
            return segment.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith('*'))
            return segment.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(pattern, segment, StringComparison.OrdinalIgnoreCase);
    }
}
