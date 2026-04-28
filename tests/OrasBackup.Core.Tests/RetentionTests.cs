using NSubstitute;
using OrasBackup.Cli;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Core.Tests;

public class EnforceRetentionTests
{
    [Fact]
    public async Task UnderMax_DoesNotDelete()
    {
        var svc = Substitute.For<IServiceFactory>();
        var oras = Substitute.For<IOrasClient>();
        svc.CreateOrasClient().Returns(oras);
        oras.ListTagsAsync("reg/repo", Arg.Any<CancellationToken>())
            .Returns(new[] { "20260101-120000-aaa", "20260102-120000-bbb", "latest", "chunk-abc" });

        await AppCommands.EnforceRetentionAsync(svc, "reg/repo", 5, null, false, CancellationToken.None);

        await oras.DidNotReceive().DeleteTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OverMax_DeletesOldestAndGCsOrphanedChunks()
    {
        var svc = Substitute.For<IServiceFactory>();
        var oras = Substitute.For<IOrasClient>();
        svc.CreateOrasClient().Returns(oras);

        // First call: pre-deletion state (3 backups + 2 chunks)
        // Second call: post-deletion state (2 backups + 2 chunks, oldest deleted)
        oras.ListTagsAsync("reg/repo", Arg.Any<CancellationToken>())
            .Returns(
                new[] { "20260101-120000-aaa", "20260102-120000-bbb", "20260103-120000-ccc", "latest", "chunk-abc", "chunk-orphan" },
                new[] { "20260102-120000-bbb", "20260103-120000-ccc", "latest", "chunk-abc", "chunk-orphan" });

        // Remaining backups both reference chunk-abc but NOT chunk-orphan
        var index = new BackupIndex { BackupId = "b", Chunks = [new ChunkRef { Tag = "chunk-abc" }] };
        var indexBytes = index.Serialize();
        oras.FetchManifestLayersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new OrasManifestEntry("application/vnd.orasbackup.index.v2+json", "sha256:idx", indexBytes.Length)]);
        oras.PullLayerAsync(Arg.Any<string>(), "sha256:idx", Arg.Any<CancellationToken>())
            .Returns(indexBytes);

        await AppCommands.EnforceRetentionAsync(svc, "reg/repo", 2, null, false, CancellationToken.None);

        // Should delete oldest backup
        await oras.Received(1).DeleteTagAsync("reg/repo", "20260101-120000-aaa", Arg.Any<CancellationToken>());
        // Should GC orphaned chunk (not referenced by remaining backups)
        await oras.Received(1).DeleteTagAsync("reg/repo", "chunk-orphan", Arg.Any<CancellationToken>());
        // Should NOT delete referenced chunk
        await oras.DidNotReceive().DeleteTagAsync("reg/repo", "chunk-abc", Arg.Any<CancellationToken>());
        // Should NOT delete latest
        await oras.DidNotReceive().DeleteTagAsync("reg/repo", "latest", Arg.Any<CancellationToken>());
    }
}
