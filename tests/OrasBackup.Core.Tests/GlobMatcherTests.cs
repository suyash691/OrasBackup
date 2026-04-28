using OrasBackup.Core.Delta;
using Xunit;

namespace OrasBackup.Core.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("**/.git", ".git/config", true)]
    [InlineData("**/.git", "src/.git/HEAD", true)]
    [InlineData("**/.git", "src/git/file", false)]
    [InlineData("**/node_modules", "node_modules/pkg/index.js", true)]
    [InlineData("**/node_modules", "src/node_modules/pkg/index.js", true)]
    [InlineData("*.log", "app.log", true)]
    [InlineData("*.log", "src/app.log", false)]
    [InlineData("**/*.log", "src/app.log", true)]
    [InlineData("**/*.log", "deep/nested/app.log", true)]
    [InlineData("**/bin", "bin/debug/app.dll", true)]
    [InlineData("**/bin", "src/bin/release/app.dll", true)]
    [InlineData("temp*", "temporary.txt", true)]
    [InlineData("temp*", "src/temporary.txt", false)]
    public void IsMatch_Patterns(string pattern, string path, bool expected)
    {
        var matcher = new GlobMatcher(pattern);
        Assert.Equal(expected, matcher.IsMatch(path));
    }

    [Fact]
    public void IsMatch_BackslashNormalized()
    {
        var matcher = new GlobMatcher("**/.git");
        Assert.True(matcher.IsMatch("src\\.git\\config".Replace('\\', '/')));
    }
}
