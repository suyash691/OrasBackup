using OrasBackup.Core.Delta;
using Xunit;

namespace OrasBackup.Core.Tests;

/// <summary>
/// Tests for ManifestCache — local persistence of DeltaManifest between backup runs.
/// </summary>
public class ManifestCacheTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"orastest-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, true); } catch { }
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var cache = new ManifestCache(_cacheDir);
        var manifest = new DeltaManifest
        {
            BackupId = "abc123",
            Files = [new FileSnapshot("a.txt", "hash1", 100, DateTime.UtcNow)],
            Deleted = ["old.txt"]
        };

        cache.Save("myprofile", manifest);
        var loaded = cache.Load("myprofile");

        Assert.NotNull(loaded);
        Assert.Equal("abc123", loaded!.BackupId);
        Assert.Single(loaded.Files);
        Assert.Single(loaded.Deleted);
    }

    [Fact]
    public void Load_NoCache_ReturnsNull()
    {
        var cache = new ManifestCache(_cacheDir);
        Assert.Null(cache.Load("nonexistent"));
    }

    [Fact]
    public void Save_OverwritesPrevious()
    {
        var cache = new ManifestCache(_cacheDir);
        cache.Save("p", new DeltaManifest { BackupId = "first" });
        cache.Save("p", new DeltaManifest { BackupId = "second" });

        var loaded = cache.Load("p");
        Assert.Equal("second", loaded!.BackupId);
    }

    [Fact]
    public void MultipleProfiles_Independent()
    {
        var cache = new ManifestCache(_cacheDir);
        cache.Save("a", new DeltaManifest { BackupId = "id-a" });
        cache.Save("b", new DeltaManifest { BackupId = "id-b" });

        Assert.Equal("id-a", cache.Load("a")!.BackupId);
        Assert.Equal("id-b", cache.Load("b")!.BackupId);
    }
}
