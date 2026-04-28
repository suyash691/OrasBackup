using System.Security.Cryptography;

namespace OrasBackup.Core.Delta;

public sealed class DeltaTracker
{
    public List<FileSnapshot> ScanDirectory(string sourceDir, IReadOnlyList<string> excludePatterns,
        IReadOnlyList<FileSnapshot>? previousSnapshots = null)
    {
        var root = new DirectoryInfo(sourceDir);
        if (!root.Exists) throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        var matchers = excludePatterns.Select(p => new GlobMatcher(p)).ToList();
        var snapshots = new List<FileSnapshot>();

        // Build lookup from previous scan for fast-path (skip hashing if size+mtime unchanged)
        var previousByPath = previousSnapshots?.ToDictionary(s => s.RelativePath) ?? new();

        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file.FullName).Replace('\\', '/');
            if (matchers.Any(m => m.IsMatch(relative))) continue;

            // Fast-path: if size and mtime match previous snapshot, reuse its hash
            if (previousByPath.TryGetValue(relative, out var prev)
                && prev.SizeBytes == file.Length
                && prev.LastModifiedUtc == file.LastWriteTimeUtc)
            {
                snapshots.Add(prev);
                continue;
            }

            snapshots.Add(new FileSnapshot(
                relative,
                HashFile(file.FullName),
                file.Length,
                file.LastWriteTimeUtc,
                GetUnixMode(file.FullName)));
        }

        return snapshots;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int GetUnixMode(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return 0;
        try { return (int)File.GetUnixFileMode(path); }
        catch { return 0; }
    }
}

/// <summary>
/// Minimal glob matcher supporting **, *, *.ext, and prefix* wildcards.
/// <para>
/// Supported patterns: **/.git, **/node_modules, *.log, **/*.log, temp*, exact-name.
/// Not supported: mid-segment wildcards like test*.log (would require regex).
/// </para>
/// </summary>
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
        // Match if pattern is exhausted — remaining path segments are under a matched directory
        return pi == pattern.Length;
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
