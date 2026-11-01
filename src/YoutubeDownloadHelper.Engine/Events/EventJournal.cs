using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Engine.Events;

/// <summary>Settings of the event stream (PLAN.md §6.3).</summary>
public sealed class EventStreamOptions
{
    /// <summary>How many recent events are kept for replay on <c>Last-Event-ID</c>.</summary>
    public int BufferSize { get; init; } = 2000;

    /// <summary>
    /// How many events may wait for one slow client. Past that its stream is closed; it reconnects with
    /// <c>Last-Event-ID</c> and gets the replay or a <c>resync</c>.
    /// </summary>
    public int MaxPendingPerClient { get; init; } = 4096;

    /// <summary>How often an idle stream gets a comment line, so clients and proxies see that it is alive.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>One numbered SSE event, already serialized.</summary>
public sealed record SseEvent(long Id, string Type, string Data)
{
    /// <summary>The event in the <c>text/event-stream</c> format.</summary>
    public string Format() =>
        string.Create(CultureInfo.InvariantCulture, $"id: {Id}\nevent: {Type}\ndata: {Data}\n\n");
}

/// <summary>Whether a client is connected; the idle exit (PLAN.md §7.4) waits for there to be none.</summary>
public interface IClientPresence
{
    int ConnectedClients { get; }

    /// <summary>When the last client disconnected, or null if none ever did.</summary>
    DateTimeOffset? LastDisconnectedAt { get; }
}

/// <summary>
/// The event stream's source (PLAN.md §6.3): reads the <see cref="EventBus"/>, maps each event with the
/// <see cref="EventMapper"/>, keeps the last <see cref="EventStreamOptions.BufferSize"/> for replay, and fans them out to
/// the connected clients. The SSE <c>id</c> is the bus sequence number; events the API does not show leave gaps.
/// <para>
/// A client that reconnects with <c>Last-Event-ID</c> gets every later event if they are all still buffered; otherwise,
/// or with no <c>Last-Event-ID</c>, a <c>resync</c> first. When the host starts to stop, every client gets
/// <c>engine.stopping</c> and its stream ends.
/// </para>
/// </summary>
public sealed partial class EventJournal(
    EventBus bus,
    EventMapper mapper,
    EventStreamOptions options,
    IOptions<JsonOptions> jsonOptions,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<EventJournal> logger) : IClientPresence, IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly Queue<SseEvent> _buffer = new();
    private readonly HashSet<Connection> _connections = [];
    private readonly JsonSerializerOptions _json = jsonOptions.Value.SerializerOptions;
    private EventSubscription? _subscription;
    private CancellationTokenRegistration _stopping;
    private Task _loop = Task.CompletedTask;

    // Guarded by _lock. Every shown event with a sequence above _floor is in _buffer; _last is the last one handled.
    private long _floor;
    private long _last;
    private bool _stopped;
    private DateTimeOffset? _lastDisconnectedAt;

    public int ConnectedClients
    {
        get
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }
    }

    public DateTimeOffset? LastDisconnectedAt
    {
        get
        {
            lock (_lock)
            {
                return _lastDisconnectedAt;
            }
        }
    }

    /// <summary>The sequence number of the last bus event handled.</summary>
    public long LastSequence
    {
        get
        {
            lock (_lock)
            {
                return _last;
            }
        }
    }

    /// <summary>Starts reading the bus. Call once, before anything publishes (the queue actor's start-up recovery does).</summary>
    public void Start()
    {
        if (_subscription is not null)
        {
            throw new InvalidOperationException("The event journal is already started.");
        }

        _subscription = bus.Subscribe();
        _floor = _last = _subscription.StartSequence;
        var subscription = _subscription;
        _loop = Task.Run(() => ReadAsync(subscription), CancellationToken.None);
        _stopping = lifetime.ApplicationStopping.Register(() => bus.Publish(new EngineStoppingEvent()));
    }

    /// <summary>
    /// A new client. With <paramref name="lastEventId"/> it gets every later event if they are all buffered, otherwise
    /// a <c>resync</c> first. Dispose the connection when the client is gone.
    /// </summary>
    public Connection Connect(long? lastEventId)
    {
        lock (_lock)
        {
            // The id may be ahead of what this journal has handled: a snapshot's sequence is the bus's, and the events
            // up to it may still be on their way here. They are skipped when they arrive.
            if (lastEventId is { } after && after >= _floor && after <= bus.LastSequence)
            {
                var connection = new Connection(this, after);
                foreach (var e in _buffer)
                {
                    if (e.Id > after)
                    {
                        connection.Write(e);
                    }
                }

                return Register(connection);
            }

            var resynced = new Connection(this, _last);
            resynced.Write(new SseEvent(_last, EventTypes.Resync, JsonSerializer.Serialize(new ResyncData(_last), _json)));
            return Register(resynced);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.DisposeAsync();
        _subscription?.Dispose();
        await _loop;
    }

    private async Task ReadAsync(EventSubscription subscription)
    {
        await foreach (var engineEvent in subscription.Reader.ReadAllAsync())
        {
            try
            {
                Handle(engineEvent);
                if (EventMapper.NotificationFor(engineEvent) is { } notify)
                {
                    bus.Publish(notify);
                }
            }
            catch (Exception ex)
            {
                LogMapFailed(logger, ex, engineEvent.GetType().Name, engineEvent.Sequence);
            }
        }
    }

    private void Handle(EngineEvent engineEvent)
    {
        var mapped = mapper.Map(engineEvent);
        var sse = mapped is null
            ? null
            : new SseEvent(engineEvent.Sequence, mapped.Type, JsonSerializer.Serialize(mapped.Data, mapped.Data.GetType(), _json));

        lock (_lock)
        {
            _last = engineEvent.Sequence;
            if (sse is null)
            {
                return;
            }

            _buffer.Enqueue(sse);
            while (_buffer.Count > Math.Max(1, options.BufferSize))
            {
                _floor = _buffer.Dequeue().Id;
            }

            foreach (var connection in _connections.ToList())
            {
                if (connection.Pending >= options.MaxPendingPerClient)
                {
                    LogClientTooSlow(logger, connection.Pending);
                    connection.Complete();
                    Remove(connection);
                }
                else
                {
                    connection.Write(sse);
                }
            }

            if (engineEvent is EngineStoppingEvent)
            {
                _stopped = true;
                foreach (var connection in _connections)
                {
                    connection.Complete();
                }

                _connections.Clear();
            }
        }
    }

    private Connection Register(Connection connection)
    {
        if (_stopped)
        {
            connection.Complete();
        }
        else
        {
            _connections.Add(connection);
        }

        return connection;
    }

    private void Remove(Connection connection)
    {
        lock (_lock)
        {
            if (_connections.Remove(connection))
            {
                _lastDisconnectedAt = time.GetUtcNow();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not stream the event {EventType} ({Sequence})")]
    private static partial void LogMapFailed(ILogger logger, Exception exception, string eventType, long sequence);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed the event stream of a client that fell {Pending} events behind")]
    private static partial void LogClientTooSlow(ILogger logger, int pending);

    /// <summary>One connected client's queue of events. Ends when the engine stops or the client falls too far behind.</summary>
    public sealed class Connection : IDisposable
    {
        private readonly EventJournal _journal;
        private readonly Channel<SseEvent> _channel = Channel.CreateUnbounded<SseEvent>();

        private readonly long _after;

        internal Connection(EventJournal journal, long after)
        {
            _journal = journal;
            _after = after;
        }

        public ChannelReader<SseEvent> Reader => _channel.Reader;

        internal int Pending => _channel.Reader.Count;

        public void Dispose()
        {
            _journal.Remove(this);
            Complete();
        }

        /// <summary>Queues the event unless the client already has it.</summary>
        internal void Write(SseEvent e)
        {
            if (e.Id > _after || e.Type == EventTypes.Resync)
            {
                _channel.Writer.TryWrite(e);
            }
        }

        internal void Complete() => _channel.Writer.TryComplete();
    }
}

/// <summary>Writes a <see cref="EventJournal.Connection"/> to an HTTP response as <c>text/event-stream</c>.</summary>
public static class EventStreamWriter
{
    private static readonly byte[] Heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");

    /// <summary>Streams until the connection ends (engine stopping, slow client) or the client goes away.</summary>
    public static async Task WriteAsync(
        HttpResponse response,
        EventJournal.Connection connection,
        TimeSpan heartbeatInterval,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(connection);
        var reader = connection.Reader;
        Task<bool>? waiting = null;
        while (true)
        {
            while (reader.TryRead(out var e))
            {
                await response.Body.WriteAsync(Encoding.UTF8.GetBytes(e.Format()), cancellationToken);
            }

            await response.Body.FlushAsync(cancellationToken);

            waiting ??= reader.WaitToReadAsync(cancellationToken).AsTask();
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(heartbeatInterval, time, heartbeat.Token);
            if (await Task.WhenAny(waiting, delay) == delay)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await response.Body.WriteAsync(Heartbeat, cancellationToken);
                continue;
            }

            await heartbeat.CancelAsync();
            var more = await waiting;
            waiting = null;
            if (!more)
            {
                return;
            }
        }
    }
}
