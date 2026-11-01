using System.Threading.Channels;

namespace YoutubeDownloadHelper.Core.Events;

/// <summary>
/// The engine's internal event stream. Publishers (the queue actor, and later watchers, auth and the tool manager)
/// publish only after their state change is durable. Every subscriber sees every event published after it
/// subscribed, in sequence order.
/// </summary>
public sealed class EventBus
{
    private readonly Lock _lock = new();
    private readonly List<Channel<EngineEvent>> _subscribers = [];
    private long _sequence;

    /// <summary>The sequence number of the last published event, or 0 when nothing has been published.</summary>
    public long LastSequence
    {
        get
        {
            lock (_lock)
            {
                return _sequence;
            }
        }
    }

    /// <summary>Assigns the next sequence number and delivers the event to every subscriber.</summary>
    public T Publish<T>(T engineEvent)
        where T : EngineEvent
    {
        ArgumentNullException.ThrowIfNull(engineEvent);

        // Numbering and delivery under one lock, so subscribers never see sequence numbers out of order.
        lock (_lock)
        {
            var published = engineEvent with { Sequence = ++_sequence };
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(published);
            }

            return (T)published;
        }
    }

    /// <summary>Starts receiving events. Dispose the subscription to stop.</summary>
    public EventSubscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<EngineEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_lock)
        {
            _subscribers.Add(channel);
            return new EventSubscription(this, channel, _sequence);
        }
    }

    internal void Unsubscribe(Channel<EngineEvent> channel)
    {
        lock (_lock)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }
}

public sealed class EventSubscription : IDisposable
{
    private readonly EventBus _bus;
    private readonly Channel<EngineEvent> _channel;

    internal EventSubscription(EventBus bus, Channel<EngineEvent> channel, long startSequence)
    {
        _bus = bus;
        _channel = channel;
        StartSequence = startSequence;
    }

    /// <summary>The last sequence number published before this subscription; it receives every later one.</summary>
    public long StartSequence { get; }

    public ChannelReader<EngineEvent> Reader => _channel.Reader;

    public void Dispose() => _bus.Unsubscribe(_channel);
}
