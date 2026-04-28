using OrasBackup.Core.Delta;
using OrasBackup.Core.Backup;
using Xunit;

namespace OrasBackup.Core.Tests;

public class DirectoryChunkerTests
{
    private static FileSnapshot F(string path, long size) =>
        new(path, "hash", size, DateTime.UtcNow);

    [Fact]
    public void EmptyFiles_ReturnsNoChunks()
    {
        var chunker = new DirectoryChunker();
        Assert.Empty(chunker.BuildChunks([]));
    }

    [Fact]
    public void SingleDirectory_OneChunk()
    {
        var files = new[] { F("docs/a.txt", 1000), F("docs/b.txt", 2000) };
        var chunker = new DirectoryChunker(maxChunkBytes: 1_000_000, minChunkBytes: 100);
        var chunks = chunker.BuildChunks(files);

        Assert.Single(chunks);
        Assert.Equal("docs", chunks[0].Path);
        Assert.Equal(2, chunks[0].Files.Count);
    }

    [Fact]
    public void LargeDirectory_SplitsIntoSubChunks()
    {
        var files = Enumerable.Range(0, 10)
            .Select(i => F($"photos/img{i}.jpg", 50_000_000)) // 50MB each = 500MB total
            .ToList();

        var chunker = new DirectoryChunker(maxChunkBytes: 200_000_000, minChunkBytes: 10_000_000);
        var chunks = chunker.BuildChunks(files);

        Assert.True(chunks.Count >= 3); // 500MB / 200MB = at least 3 chunks
        Assert.All(chunks, c => Assert.True(c.TotalBytes <= 200_000_000));
    }

    [Fact]
    public void SmallDirectories_MergedTogether()
    {
        var files = new[]
        {
            F("tiny1/a.txt", 100),
            F("tiny2/b.txt", 200),
            F("tiny3/c.txt", 300),
        };

        var chunker = new DirectoryChunker(maxChunkBytes: 1_000_000, minChunkBytes: 10_000);
        var chunks = chunker.BuildChunks(files);

        // All 3 tiny dirs should merge into 1 chunk
        Assert.Single(chunks);
        Assert.Equal(3, chunks[0].Files.Count);
    }

    [Fact]
    public void MixedSizes_SmallMergedIntoMedium()
    {
        var files = new[]
        {
            F("small/a.txt", 100),
            F("medium/b.txt", 50_000_000),
            F("large/c1.bin", 100_000_000),
            F("large/c2.bin", 100_000_000),
            F("large/c3.bin", 100_000_000),
        };

        var chunker = new DirectoryChunker(maxChunkBytes: 200_000_000, minChunkBytes: 10_000_000);
        var chunks = chunker.BuildChunks(files);

        // All files accounted for
        Assert.Equal(5, chunks.Sum(c => c.Files.Count));
        // No chunk should be undersized (below min) — small/ must merge into medium/
        // Exception: the last flush can be undersized if there's nothing to merge with
        var nonLastChunks = chunks.Take(chunks.Count - 1).ToList();
        Assert.All(nonLastChunks, c => Assert.True(c.TotalBytes >= 10_000_000,
            $"Chunk '{c.Path}' is undersized at {c.TotalBytes} bytes"));
    }

    [Fact]
    public void SmallBeforeMedium_MergedNotFlushedSeparately()
    {
        // Regression test: small dir followed by medium dir should merge, not flush small alone
        var files = new[]
        {
            F("alpha/tiny.txt", 100),       // small — below min
            F("beta/data.bin", 50_000_000), // medium — between min and max
        };

        var chunker = new DirectoryChunker(maxChunkBytes: 200_000_000, minChunkBytes: 10_000_000);
        var chunks = chunker.BuildChunks(files);

        // Should be 1 merged chunk, NOT 2 separate chunks
        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].Files.Count);
    }

    [Fact]
    public void RootFiles_GroupedCorrectly()
    {
        var files = new[] { F("readme.md", 1000), F("license.txt", 500) };
        var chunker = new DirectoryChunker(maxChunkBytes: 1_000_000, minChunkBytes: 100);
        var chunks = chunker.BuildChunks(files);

        Assert.Single(chunks);
        Assert.Equal("(root)", chunks[0].Path);
    }
}
