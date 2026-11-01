using System.Globalization;
using System.Net;
using System.Text.Json;
using YoutubeDownloadHelper.Contracts;

namespace YoutubeDownloadHelper.Engine.Tests.Events;

/// <summary>One parsed server-sent event.</summary>
public sealed record SseMessage(long Id, string Type, JsonElement Data);

/// <summary>A client of <c>GET /events</c> that parses the stream as it arrives.</summary>
public sealed class SseClient : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;
    private readonly CancellationTokenSource _cts;

    private SseClient(HttpClient client, HttpResponseMessage response, StreamReader reader, CancellationTokenSource cts)
    {
        _client = client;
        _response = response;
        _reader = reader;
        _cts = cts;
    }

    /// <summary>Every event received so far, in order.</summary>
    public List<SseMessage> Received { get; } = [];

    /// <summary>Comment lines (heartbeats) received so far.</summary>
    public int Comments { get; private set; }

    /// <summary>The stream ended (the server closed it).</summary>
    public bool Ended { get; private set; }

    public HttpResponseMessage Response => _response;

    public static async Task<SseClient> ConnectAsync(TestEngine engine, long? lastEventId = null)
    {
        var client = engine.CreateClient();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Events);
        if (lastEventId is { } id)
        {
            request.Headers.Add("Last-Event-ID", id.ToString(CultureInfo.InvariantCulture));
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        return new SseClient(client, response, reader, cts);
    }

    /// <summary>The next event, or null when the stream ended.</summary>
    public async Task<SseMessage?> NextAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(Timeout);
        long? id = null;
        string? type = null;
        string? data = null;
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("No event arrived in time.");
            }

            if (line is null)
            {
                Ended = true;
                return null;
            }

            if (line.Length == 0)
            {
                if (type is not null)
                {
                    using var document = JsonDocument.Parse(data ?? "{}");
                    var message = new SseMessage(id ?? -1, type, document.RootElement.Clone());
                    Received.Add(message);
                    return message;
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                Comments++;
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = long.Parse(line[4..], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
        }
    }

    /// <summary>Reads until <paramref name="count"/> more comment lines (heartbeats) arrived; events read meanwhile are kept.</summary>
    public async Task WaitForCommentsAsync(int count)
    {
        var target = Comments + count;
        while (Comments < target)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(Timeout);
            var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The stream ended.");
            if (line.StartsWith(':'))
            {
                Comments++;
            }
        }
    }

    /// <summary>Reads until an event matches; fails if the stream ends first.</summary>
    public async Task<SseMessage> WaitForAsync(Func<SseMessage, bool> match, string what)
    {
        while (await NextAsync() is { } message)
        {
            if (match(message))
            {
                return message;
            }
        }

        throw new InvalidOperationException($"The stream ended before {what}.");
    }

    public Task<SseMessage> WaitForAsync(string type) => WaitForAsync(m => m.Type == type, type);

    /// <summary>Reads to the end of the stream.</summary>
    public async Task ReadToEndAsync()
    {
        while (await NextAsync() is not null)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _reader.Dispose();
        _response.Dispose();
        _client.Dispose();
        _cts.Dispose();
    }
}
