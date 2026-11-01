using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Tests.Queue;

public sealed class EventBusTests
{
    private sealed record TestEvent(int Value) : EngineEvent;

    [Fact]
    public async Task Subscribers_get_later_events_in_order_from_concurrent_publishers()
    {
        var bus = new EventBus();
        bus.Publish(new TestEvent(-1));

        using var subscription = bus.Subscribe();
        Assert.Equal(1, subscription.StartSequence);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
            {
                bus.Publish(new TestEvent(i));
            }
        }, TestContext.Current.CancellationToken)));

        var sequences = new List<long>();
        while (subscription.Reader.TryRead(out var e))
        {
            sequences.Add(e.Sequence);
        }

        Assert.Equal(Enumerable.Range(2, 2000).Select(i => (long)i), sequences);
        Assert.Equal(2001, bus.LastSequence);
    }

    [Fact]
    public void A_disposed_subscription_stops_receiving()
    {
        var bus = new EventBus();
        var subscription = bus.Subscribe();
        subscription.Dispose();
        bus.Publish(new TestEvent(1));

        Assert.False(subscription.Reader.TryRead(out _));
        Assert.True(subscription.Reader.Completion.IsCompleted);
    }
}
