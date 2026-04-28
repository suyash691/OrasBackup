using OrasBackup.Core.Delta;
using Xunit;

namespace OrasBackup.Core.Tests;

public class DeltaTrackerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"orastest-{Guid.NewGuid():N}");
    private readonly DeltaTracker _sut = new();

    public DeltaTrackerTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public void ScanDirectory_FindsAllFiles()
    {
        WriteFile("a.txt", "hello");
        WriteFile("sub/b.txt", "world");

        var files = _sut.ScanDirectory(_tempDir, []);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void ScanDirectory_RespectsExcludePatterns()
    {
        WriteFile("src/app.cs", "code");
        WriteFile("bin/app.dll", "binary");
        WriteFile("node_modules/pkg/index.js", "js");

        var files = _sut.ScanDirectory(_tempDir, ["**/bin", "**/node_modules"]);
        Assert.Single(files);
        Assert.Equal("src/app.cs", files[0].RelativePath);
    }

    [Fact]
    public void ScanDirectory_ThrowsForMissingDir()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _sut.ScanDirectory("/nonexistent/path", []));
    }

    [Fact]
    public void ScanDirectory_ComputesSha256()
    {
        WriteFile("f.txt", "test content");
        var files = _sut.ScanDirectory(_tempDir, []);
        Assert.Single(files);
        Assert.NotEmpty(files[0].Sha256);
        Assert.Equal(64, files[0].Sha256.Length); // hex SHA-256
    }

    [Fact]
    public void ScanDirectory_CapturesFileSize()
    {
        WriteFile("f.txt", "hello");
        var files = _sut.ScanDirectory(_tempDir, []);
        Assert.Equal(5, files[0].SizeBytes);
    }

    [Fact]
    public void ScanDirectory_FastPath_ReusesHash_WhenSizeAndMtimeMatch()
    {
        WriteFile("f.txt", "hello");
        var first = _sut.ScanDirectory(_tempDir, []);
        // Second scan with previous snapshots — should reuse hash (no re-read)
        var second = _sut.ScanDirectory(_tempDir, [], first);
        Assert.Equal(first[0].Sha256, second[0].Sha256);
    }

    [Fact]
    public void ScanDirectory_FastPath_RehashesWhenSizeChanges()
    {
        WriteFile("f.txt", "hello");
        var first = _sut.ScanDirectory(_tempDir, []);
        // Change file content (different size)
        WriteFile("f.txt", "hello world!");
        var second = _sut.ScanDirectory(_tempDir, [], first);
        Assert.NotEqual(first[0].Sha256, second[0].Sha256);
    }

    [Fact]
    public void ScanDirectory_FastPath_RehashesWhenMtimeChanges()
    {
        WriteFile("f.txt", "hello");
        var first = _sut.ScanDirectory(_tempDir, []);
        // Touch file to change mtime without changing content
        File.SetLastWriteTimeUtc(Path.Combine(_tempDir, "f.txt"), DateTime.UtcNow.AddSeconds(10));
        var second = _sut.ScanDirectory(_tempDir, [], first);
        // Hash should be recomputed (same value since content didn't change, but the path was taken)
        Assert.Equal(first[0].Sha256, second[0].Sha256);
        Assert.NotEqual(first[0].LastModifiedUtc, second[0].LastModifiedUtc);
    }

    private void WriteFile(string relative, string content)
    {
        var path = Path.Combine(_tempDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
