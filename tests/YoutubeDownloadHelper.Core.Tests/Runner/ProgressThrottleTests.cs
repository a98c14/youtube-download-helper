using Microsoft.Extensions.Time.Testing;
using YoutubeDownloadHelper.Core.Runner;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

public sealed class ProgressThrottleTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<(TimeSpan At, string Value)> _sent = [];
    private readonly DateTimeOffset _start;

    public ProgressThrottleTests() => _start = _time.GetUtcNow();

    [Fact]
    public void A_flood_of_updates_is_sent_at_most_4_times_per_second()
    {
        using var throttle = Throttle();

        // 1000 updates per second for 10 seconds, as an unthrottled yt-dlp (no --progress-delta) could write them.
        for (var i = 0; i < 10_000; i++)
        {
            throttle.Post(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _time.Advance(TimeSpan.FromMilliseconds(1));
        }

        _time.Advance(TimeSpan.FromSeconds(1));

        // Every sliding one-second window holds at most 4 updates, and the gaps are never below 250 ms.
        for (var i = 4; i < _sent.Count; i++)
        {
            Assert.True(_sent[i].At - _sent[i - 4].At >= TimeSpan.FromSeconds(1), $"Updates {i - 4}..{i} within a second.");
        }

        Assert.All(_sent.Zip(_sent.Skip(1)), p => Assert.True(p.Second.At - p.First.At >= ProgressThrottle<string>.DefaultInterval));

        // ...but it keeps up: about 4 per second, and the last value is delivered.
        Assert.InRange(_sent.Count, 39, 41);
        Assert.Equal("9999", _sent[^1].Value);
    }

    [Fact]
    public void The_first_update_goes_out_at_once()
    {
        using var throttle = Throttle();

        throttle.Post("a");

        Assert.Equal([(TimeSpan.Zero, "a")], _sent);
    }

    [Fact]
    public void An_early_update_is_held_and_replaced_by_newer_ones()
    {
        using var throttle = Throttle();
        throttle.Post("a");

        _time.Advance(TimeSpan.FromMilliseconds(100));
        throttle.Post("b");
        throttle.Post("c");
        _time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Single(_sent);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([(TimeSpan.Zero, "a"), (TimeSpan.FromMilliseconds(250), "c")], _sent);
    }

    [Fact]
    public void An_update_after_a_quiet_period_goes_out_at_once()
    {
        using var throttle = Throttle();
        throttle.Post("a");

        _time.Advance(TimeSpan.FromSeconds(5));
        throttle.Post("b");

        Assert.Equal([(TimeSpan.Zero, "a"), (TimeSpan.FromSeconds(5), "b")], _sent);
    }

    [Fact]
    public void Disposing_drops_a_held_update()
    {
        var throttle = Throttle();
        throttle.Post("a");
        throttle.Post("b");

        throttle.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));
        throttle.Post("c");

        Assert.Equal([(TimeSpan.Zero, "a")], _sent);
    }

    private ProgressThrottle<string> Throttle() => new(v => _sent.Add((_time.GetUtcNow() - _start, v)), _time);
}
