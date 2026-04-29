using OrasBackup.Core.Config;
using OrasBackup.Core.Delta;
using Xunit;

namespace OrasBackup.Core.Tests;

public class DeltaManifestSerializationTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTrips()
    {
        var manifest = new DeltaManifest
        {
            BackupId = "test123",
            BasedOn = "prev456",
            Files = [
                new FileSnapshot("a.txt", "hash1", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                new FileSnapshot("b/c.txt", "hash2", 200, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc))
            ],
            Deleted = ["old.txt", "removed/file.txt"]
        };

        var bytes = manifest.Serialize();
        var deserialized = DeltaManifest.Deserialize(bytes);

        Assert.Equal("test123", deserialized.BackupId);
        Assert.Equal("prev456", deserialized.BasedOn);
        Assert.Equal(2, deserialized.Files.Count);
        Assert.Equal("a.txt", deserialized.Files[0].RelativePath);
        Assert.Equal(2, deserialized.Deleted.Count);
    }

    [Fact]
    public void Serialize_NullBasedOn_RoundTrips()
    {
        var manifest = new DeltaManifest { BackupId = "full", BasedOn = null };
        var bytes = manifest.Serialize();
        var deserialized = DeltaManifest.Deserialize(bytes);

        Assert.Null(deserialized.BasedOn);
    }

    [Fact]
    public void Serialize_EmptyManifest_RoundTrips()
    {
        var manifest = new DeltaManifest();
        var bytes = manifest.Serialize();
        var deserialized = DeltaManifest.Deserialize(bytes);

        Assert.NotNull(deserialized.BackupId);
        Assert.Empty(deserialized.Files);
        Assert.Empty(deserialized.Deleted);
    }
}
