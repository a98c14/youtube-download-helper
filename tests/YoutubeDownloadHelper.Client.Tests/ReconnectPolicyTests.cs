using YoutubeDownloadHelper.Client.Events;

namespace YoutubeDownloadHelper.Client.Tests;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void Delays_double_up_to_the_maximum() =>
        Assert.Equal(
            [250, 500, 1000, 2000, 4000, 8000, 10000, 10000],
            Enumerable.Range(0, 8).Select(n => ReconnectPolicy.Default.DelayFor(n).TotalMilliseconds));
}
