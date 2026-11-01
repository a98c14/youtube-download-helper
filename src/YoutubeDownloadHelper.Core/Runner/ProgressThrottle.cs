namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// Coalesces progress snapshots to at most one per <see cref="Interval"/> (PLAN.md §5.4: 4 per second per item).
/// The first snapshot goes out at once; one that arrives too soon is held and sent when the interval is up, replaced by
/// any newer one in the meantime, so the latest state (a phase change, say) is never lost, only delayed. Thread-safe;
/// the sink is called under a lock, so it must be quick and must not call back into the throttle.
/// </summary>
public sealed class ProgressThrottle<T> : IDisposable
    where T : class
{
    /// <summary>The default interval: 4 updates per second.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private readonly Action<T> _sink;
    private readonly TimeProvider _time;
    private long? _lastSent;
    private T? _pending;
    private ITimer? _timer;
    private bool _disposed;

    public ProgressThrottle(Action<T> sink, TimeProvider time, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(time);
        _sink = sink;
        _time = time;
        Interval = interval ?? DefaultInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Interval, TimeSpan.Zero, nameof(interval));
    }

    public TimeSpan Interval { get; }

    public void Post(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _pending = value;
            if (_timer is not null)
            {
                return;
            }

            var wait = _lastSent is { } last ? Interval - _time.GetElapsedTime(last) : TimeSpan.Zero;
            if (wait <= TimeSpan.Zero)
            {
                SendPending();
            }
            else
            {
                _timer = _time.CreateTimer(_ => OnTimer(), null, wait, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Stops sending; a held snapshot is dropped.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimer()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (!_disposed && _pending is not null)
            {
                SendPending();
            }
        }
    }

    private void SendPending()
    {
        var value = _pending!;
        _pending = null;
        _lastSent = _time.GetTimestamp();
        _sink(value);
    }
}
