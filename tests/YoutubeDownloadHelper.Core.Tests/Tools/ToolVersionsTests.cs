using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Tools;

public sealed class ToolVersionsTests
{
    [Theory]
    [InlineData("2026.10.26", "2026.10.9")]
    [InlineData("2026.11.01.233311", "2026.11.01.010203")]
    [InlineData("2026.10.26.1", "2026.10.26")]
    [InlineData("2.10.0", "2.9.8")]
    [InlineData("2026-11-01-1909", "2026-10-31-2359")]
    [InlineData("10.0", "9.99999999999999999999")]
    [InlineData("1.0b", "1.0a")]
    public void The_first_is_newer(string newer, string older)
    {
        Assert.True(ToolVersions.IsNewer(newer, older));
        Assert.False(ToolVersions.IsNewer(older, newer));
    }

    [Fact]
    public void Equal_versions_are_not_newer()
    {
        Assert.False(ToolVersions.IsNewer("2026.10.26", "2026.10.26"));
        Assert.Equal(0, ToolVersions.Comparer.Compare("1.02", "1.2"));
    }
}
