using OrasBackup.Core.Delta;
using Xunit;

namespace OrasBackup.Core.Tests;

public class DeltaTrackerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"orastest-{Guid.NewGuid():N}");
    private readonly DeltaTracker _sut = new();

    public DeltaTrackerTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void ComputeDelta_NoPrevious_AllFilesAreAdded()
    {
        WriteFile("a.txt", "hello");
        WriteFile("sub/b.txt", "world");

        var result = _sut.ComputeDelta(_tempDir, null, []);

        Assert.Equal(2, result.Added.Count);
        Assert.Empty(result.Modified);
        Assert.Empty(result.Deleted);
        Assert.Empty(result.Unchanged);
    }

    [Fact]
    public void ComputeDelta_DetectsModifiedFile()
    {
        WriteFile("a.txt", "v1");
        var prev = BuildManifest(_sut.ScanDirectory(_tempDir, []));

        WriteFile("a.txt", "v2"); // modify

        var result = _sut.ComputeDelta(_tempDir, prev, []);

        Assert.Empty(result.Added);
        Assert.Single(result.Modified);
        Assert.Equal("a.txt", result.Modified[0].RelativePath);
        Assert.Empty(result.Deleted);
    }

    [Fact]
    public void ComputeDelta_DetectsDeletedFile()
    {
        WriteFile("a.txt", "keep");
        WriteFile("b.txt", "remove");
        var prev = BuildManifest(_sut.ScanDirectory(_tempDir, []));

        File.Delete(Path.Combine(_tempDir, "b.txt"));

        var result = _sut.ComputeDelta(_tempDir, prev, []);

        Assert.Empty(result.Added);
        Assert.Empty(result.Modified);
        Assert.Single(result.Deleted);
        Assert.Equal("b.txt", result.Deleted[0]);
        Assert.Single(result.Unchanged);
    }

    [Fact]
    public void ComputeDelta_DetectsNewFile()
    {
        WriteFile("a.txt", "existing");
        var prev = BuildManifest(_sut.ScanDirectory(_tempDir, []));

        WriteFile("b.txt", "new file");

        var result = _sut.ComputeDelta(_tempDir, prev, []);

        Assert.Single(result.Added);
        Assert.Equal("b.txt", result.Added[0].RelativePath);
        Assert.Single(result.Unchanged);
    }

    [Fact]
    public void ComputeDelta_UnchangedFilesDetected()
    {
        WriteFile("a.txt", "same");
        var prev = BuildManifest(_sut.ScanDirectory(_tempDir, []));

        var result = _sut.ComputeDelta(_tempDir, prev, []);

        Assert.Empty(result.Added);
        Assert.Empty(result.Modified);
        Assert.Empty(result.Deleted);
        Assert.Single(result.Unchanged);
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
        Assert.Throws<DirectoryNotFoundException>(() =>
            _sut.ScanDirectory("/nonexistent/path", []));
    }

    private void WriteFile(string relative, string content)
    {
        var path = Path.Combine(_tempDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static DeltaManifest BuildManifest(List<FileSnapshot> files) =>
        new() { Files = files };
}
