using System.Runtime.CompilerServices;
using YoutubeDownloadHelper.Client.Discovery;
using YoutubeDownloadHelper.Contracts.Discovery;

namespace YoutubeDownloadHelper.Client.Events;

/// <summary>How long to wait before reconnecting: doubling from <see cref="InitialDelay"/> up to <see cref="MaxDelay"/>.</summary>
public sealed record ReconnectPolicy(TimeSpan InitialDelay, TimeSpan MaxDelay)
{
    public static ReconnectPolicy Default { get; } = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));

    /// <summary>The wait after <paramref name="failures"/> failed attempts in a row (0 after a working connection ended).</summary>
    public TimeSpan DelayFor(int failures)
    {
        var delay = InitialDelay;
        for (var i = 0; i < failures && delay < MaxDelay; i++)
        {
            delay *= 2;
        }

        return delay < MaxDelay ? delay : MaxDelay;
    }
}

/// <summary>What a <see cref="ReconnectingEventStream"/> reports.</summary>
public abstract record EventStreamSignal;

/// <summary>A stream is open.</summary>
/// <param name="Client">The client of the engine it is connected to.</param>
/// <param name="EngineChanged">
/// It is another engine than the previous connection's (or the first connection): its sequence numbers start over, and
/// the stream starts with <c>resync</c>.
/// </param>
/// <param name="ResumedAfter">The <c>Last-Event-ID</c> it was opened with; null when it starts with <c>resync</c>.</param>
public sealed record StreamConnected(EngineClient Client, bool EngineChanged, long? ResumedAfter) : EventStreamSignal;

/// <summary>An event arrived.</summary>
public sealed record StreamEventReceived(ServerEvent Event) : EventStreamSignal;

/// <summary>The stream ended or could not be opened; the next attempt follows after <paramref name="RetryIn"/>.</summary>
/// <param name="Error">Why; null when the engine closed the stream (e.g. after <c>engine.stopping</c>) or none was found.</param>
/// <param name="RetryIn">The wait before the next attempt.</param>
public sealed record StreamDisconnected(Exception? Error, TimeSpan RetryIn) : EventStreamSignal;

/// <summary>
/// The engine's event stream that never gives up (PLAN.md §6.3). Before each connection it asks the
/// <see cref="IEngineLocator"/> for the engine; it reconnects with backoff, and with <c>Last-Event-ID</c> so the engine
/// replays what was missed (or sends <c>resync</c>). Moving to another engine (a new token or PID) forgets the
/// position, since sequence numbers start over.
/// </summary>
public sealed class ReconnectingEventStream(
    IEngineLocator locator,
    EngineClientOptions? clientOptions = null,
    ReconnectPolicy? policy = null) : IDisposable
{
    private readonly EngineClientOptions _clientOptions = clientOptions ?? new EngineClientOptions();
    private readonly ReconnectPolicy _policy = policy ?? ReconnectPolicy.Default;
    private readonly Lock _lock = new();
    private EngineClient? _client;
    private long? _lastEventId;
    private int _failures;

    /// <summary>The client of the engine last connected to (or found).</summary>
    public EngineClient? Client
    {
        get
        {
            lock (_lock)
            {
                return _client;
            }
        }
    }

    /// <summary>The id of the last event read; the next connection resumes after it.</summary>
    public long? LastEventId
    {
        get
        {
            lock (_lock)
            {
                return _lastEventId;
            }
        }
    }

    /// <summary>Opens the next connection without <c>Last-Event-ID</c>, so it starts with <c>resync</c>.</summary>
    public void ForgetPosition()
    {
        lock (_lock)
        {
            _lastEventId = null;
        }
    }

    /// <summary>
    /// Connects, reads and reconnects until cancelled. Connection problems are reported as
    /// <see cref="StreamDisconnected"/>, never thrown. One reader at a time.
    /// </summary>
    public async IAsyncEnumerable<EventStreamSignal> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Exception? error = null;
            EventStreamConnection? connection = null;
            var engineChanged = false;
            long? resumeAfter = null;
            try
            {
                if (await locator.LocateAsync(cancellationToken) is { } engine)
                {
                    engineChanged = UseEngine(engine);
                    resumeAfter = LastEventId;
                    connection = await Client!.OpenEventStreamAsync(resumeAfter, cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsConnectionProblem(ex))
            {
                error = ex;
            }

            if (connection is not null)
            {
                _failures = 0;
                try
                {
                    yield return new StreamConnected(Client!, engineChanged, resumeAfter);
                    while (true)
                    {
                        ServerEvent? next;
                        try
                        {
                            next = await connection.ReadAsync(cancellationToken);
                        }
                        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsConnectionProblem(ex))
                        {
                            error = ex;
                            break;
                        }

                        if (next is null)
                        {
                            break;
                        }

                        lock (_lock)
                        {
                            _lastEventId = next.Id;
                        }

                        yield return new StreamEventReceived(next);
                    }
                }
                finally
                {
                    await connection.DisposeAsync();
                }
            }
            else
            {
                _failures++;
            }

            var delay = _policy.DelayFor(Math.Max(0, _failures - 1));
            yield return new StreamDisconnected(error, delay);
            await Task.Delay(delay, _clientOptions.TimeProvider, cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    /// <summary>Switches to <paramref name="engine"/> unless it is the current one; true when it switched.</summary>
    private bool UseEngine(EngineDiscovery engine)
    {
        lock (_lock)
        {
            var current = _client?.Engine;
            if (current is not null
                && string.Equals(current.BaseUrl, engine.BaseUrl, StringComparison.Ordinal)
                && string.Equals(current.Token, engine.Token, StringComparison.Ordinal)
                && current.Pid == engine.Pid)
            {
                return false;
            }

            // The old engine is gone; calls still running on its client fail either way.
            _client?.Dispose();
            _client = new EngineClient(engine, _clientOptions);
            _lastEventId = null;
            return true;
        }
    }

    private static bool IsConnectionProblem(Exception ex) =>
        ex is HttpRequestException or IOException or TimeoutException or EngineApiException or OperationCanceledException
            or ObjectDisposedException or InvalidOperationException;
}
