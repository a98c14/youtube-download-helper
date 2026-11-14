using System.Globalization;
using System.Text;

namespace YoutubeDownloadHelper.Client.Events;

/// <summary>
/// One open <c>GET /events</c> stream (<c>text/event-stream</c>). Read it with <see cref="ReadAsync"/> until it returns
/// null (the engine closed the stream), then dispose it. No reconnect: <see cref="ReconnectingEventStream"/> does that.
/// </summary>
public sealed class EventStreamConnection : IAsyncDisposable, IDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;

    internal EventStreamConnection(HttpResponseMessage response, Stream body, TimeSpan idleTimeout, TimeProvider time)
    {
        _response = response;
        _reader = new StreamReader(body, Encoding.UTF8);
        _idleTimeout = idleTimeout;
        _time = time;
    }

    /// <summary>
    /// The next event, or null when the engine closed the stream. Comment lines (heartbeats) are skipped.
    /// </summary>
    /// <exception cref="TimeoutException">Nothing, not even a heartbeat, arrived within the idle timeout.</exception>
    /// <exception cref="IOException">The connection broke.</exception>
    public async Task<ServerEvent?> ReadAsync(CancellationToken cancellationToken = default)
    {
        long? id = null;
        string? type = null;
        StringBuilder? data = null;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                // A blank line dispatches the event. The engine names every event and numbers it.
                if (data is not null)
                {
                    return new ServerEvent(id ?? 0, type ?? "message", data.ToString());
                }

                (id, type) = (null, null);
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "id" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                    id = parsed;
                    break;
                case "event":
                    type = value;
                    break;
                case "data":
                    data = data is null ? new StringBuilder(value) : data.Append('\n').Append(value);
                    break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _response.Dispose();
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_idleTimeout == Timeout.InfiniteTimeSpan)
        {
            return await _reader.ReadLineAsync(cancellationToken);
        }

        using var timeout = new CancellationTokenSource(_idleTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await _reader.ReadLineAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The event stream sent nothing, not even a heartbeat, in time.");
        }
    }
}
