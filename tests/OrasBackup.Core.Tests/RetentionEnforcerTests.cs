using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Core.Tests;

public class RetentionEnforcerTests
{
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();

    [Fact]
    public async Task Prune_DeletesOldestWhenOverMaxBackups()
    {
        var tags = Enumerable.Range(1, 12).Select(i => $"backup{i:D3}").Append("latest").ToArray();
        _oras.ListTagsAsync("reg/repo", Arg.Any<CancellationToken>()).Returns(tags);

        var enforcer = new RetentionEnforcer(_oras);
        var config = new RetentionConfig { MaxBackups = 10, CompactAfter = 100 };

        await enforcer.EnforceAsync("reg/repo", config);

        // 12 backups (excluding "latest") - max 10 = 2 to delete (oldest first)
        await _oras.Received(1).DeleteTagAsync("reg/repo", "backup001", Arg.Any<CancellationToken>());
        await _oras.Received(1).DeleteTagAsync("reg/repo", "backup002", Arg.Any<CancellationToken>());
        await _oras.DidNotReceive().DeleteTagAsync("reg/repo", "backup003", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prune_DoesNothingWhenUnderMax()
    {
        var tags = new[] { "abc", "def", "latest" };
        _oras.ListTagsAsync("reg/repo", Arg.Any<CancellationToken>()).Returns(tags);

        var enforcer = new RetentionEnforcer(_oras);
        await enforcer.EnforceAsync("reg/repo", new RetentionConfig { MaxBackups = 10 });

        await _oras.DidNotReceive().DeleteTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prune_IgnoresLatestTag()
    {
        var tags = new[] { "a", "b", "c", "latest" };
        _oras.ListTagsAsync("reg/repo", Arg.Any<CancellationToken>()).Returns(tags);

        var enforcer = new RetentionEnforcer(_oras);
        await enforcer.EnforceAsync("reg/repo", new RetentionConfig { MaxBackups = 2 });

        // Should delete "a" (oldest), keep "b", "c", and "latest"
        await _oras.Received(1).DeleteTagAsync("reg/repo", "a", Arg.Any<CancellationToken>());
        await _oras.DidNotReceive().DeleteTagAsync("reg/repo", "latest", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompactNeeded_ReturnsTrueWhenChainExceedsThreshold()
    {
        var enforcer = new RetentionEnforcer(_oras);
        Assert.True(enforcer.ShouldCompact(chainLength: 11, compactAfter: 10));
    }

    [Fact]
    public async Task CompactNeeded_ReturnsFalseWhenUnderThreshold()
    {
        var enforcer = new RetentionEnforcer(_oras);
        Assert.False(enforcer.ShouldCompact(chainLength: 5, compactAfter: 10));
    }
}
