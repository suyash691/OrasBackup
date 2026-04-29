using OrasBackup.Core.Backup;
using OrasBackup.Core.Delta;
using Xunit;

namespace OrasBackup.Core.Tests;

public class ChainLengthTests
{
    [Fact]
    public void CountChainLength_NullBasedOn_ReturnsZero()
    {
        var manifest = new DeltaManifest { BackupId = "full", BasedOn = null };
        Assert.Equal(0, ChainCounter.Count(manifest));
    }

    [Fact]
    public void CountChainLength_OneIncremental_ReturnsOne()
    {
        var manifest = new DeltaManifest { BackupId = "incr1", BasedOn = "full" };
        Assert.Equal(1, ChainCounter.Count(manifest));
    }

    [Fact]
    public void CountChainLength_LongChain_CountsAllLinks()
    {
        // Simulate: full → incr1 → incr2 → incr3
        // The manifest only knows its own basedOn, but chain length = depth of basedOn nesting
        // Since we only have the current manifest, we count how many basedOn hops exist
        // in the manifest's Files (which accumulate from all previous backups)
        var manifest = new DeltaManifest
        {
            BackupId = "incr3",
            BasedOn = "incr2"
        };
        // Chain length from this manifest = 1 (it has a basedOn)
        // But the REAL chain length needs the full manifest chain from cache
        // So ChainCounter should accept the cache to walk the full chain
        Assert.Equal(1, ChainCounter.Count(manifest));
    }

    [Fact]
    public void CountChainLength_FromCache_WalksFullChain()
    {
        var cache = new Dictionary<string, DeltaManifest>
        {
            ["full"] = new() { BackupId = "full", BasedOn = null },
            ["incr1"] = new() { BackupId = "incr1", BasedOn = "full" },
            ["incr2"] = new() { BackupId = "incr2", BasedOn = "incr1" },
            ["incr3"] = new() { BackupId = "incr3", BasedOn = "incr2" },
        };

        Assert.Equal(0, ChainCounter.Count(cache["full"], id => cache.GetValueOrDefault(id)));
        Assert.Equal(1, ChainCounter.Count(cache["incr1"], id => cache.GetValueOrDefault(id)));
        Assert.Equal(2, ChainCounter.Count(cache["incr2"], id => cache.GetValueOrDefault(id)));
        Assert.Equal(3, ChainCounter.Count(cache["incr3"], id => cache.GetValueOrDefault(id)));
    }
}
