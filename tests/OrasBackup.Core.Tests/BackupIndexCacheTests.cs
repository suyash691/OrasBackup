using OrasBackup.Core.Backup;
using Xunit;

namespace OrasBackup.Core.Tests;

public class BackupIndexCacheTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"cache-test-{Guid.NewGuid():N}");
    private readonly BackupIndexCache _sut;

    public BackupIndexCacheTests() => _sut = new BackupIndexCache(_cacheDir);
    public void Dispose() { try { Directory.Delete(_cacheDir, true); } catch { } }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var index = new BackupIndex { BackupId = "test-123", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "docs" }] };
        _sut.Save("myprofile", index);
        var loaded = _sut.Load("myprofile");
        Assert.NotNull(loaded);
        Assert.Equal("test-123", loaded!.BackupId);
        Assert.Single(loaded.Chunks);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull() => Assert.Null(_sut.Load("nonexistent"));

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllText(Path.Combine(_cacheDir, "corrupt.index.json"), "not json");
        Assert.Null(_sut.Load("corrupt"));
    }

    [Fact]
    public void Delete_RemovesCachedFile()
    {
        var index = new BackupIndex { BackupId = "del-test" };
        _sut.Save("todelete", index);
        Assert.NotNull(_sut.Load("todelete"));
        _sut.Delete("todelete");
        Assert.Null(_sut.Load("todelete"));
    }

    [Fact]
    public void Delete_NonexistentProfile_DoesNotThrow()
    {
        _sut.Delete("nonexistent"); // should not throw
    }

    [Fact]
    public void InvalidProfileName_Throws()
    {
        Assert.Throws<ArgumentException>(() => _sut.Load("../../evil"));
        Assert.Throws<ArgumentException>(() => _sut.Save("../../evil", new BackupIndex()));
        Assert.Throws<ArgumentException>(() => _sut.Delete("../../evil"));
    }
}
