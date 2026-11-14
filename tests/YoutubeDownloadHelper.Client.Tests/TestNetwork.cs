using System.Collections.Concurrent;
using System.Net;

namespace YoutubeDownloadHelper.Client.Tests;

/// <summary>
/// An HTTP handler between the client and a test engine that can break the network: drop the open event streams, and
/// refuse new connections until it is restored. It forwards to whichever engine is <see cref="Target"/>.
/// </summary>
public sealed class TestNetwork : HttpMessageHandler
{
    private readonly ConcurrentDictionary<DroppableStream, byte> _streams = new();
    private readonly ConcurrentQueue<Request> _requests = new();
    private volatile HttpMessageInvoker? _target;
    private volatile bool _down;

    public TestNetwork(HttpMessageHandler target) => Target = target;

    /// <summary>The handler of the engine the requests go to (a TestServer's).</summary>
    public HttpMessageHandler Target
    {
        set => _target = new HttpMessageInvoker(value, disposeHandler: false);
    }

    /// <summary>Every request so far.</summary>
    public IReadOnlyList<Request> Requests => [.. _requests];

    /// <summary>The requests to <paramref name="path"/> (whatever the query).</summary>
    public IReadOnlyList<Request> To(HttpMethod method, string path) =>
        [.. _requests.Where(r => r.Method == method && string.Equals(r.Path, path, StringComparison.Ordinal))];

    /// <summary>Breaks every open event stream, and refuses new connections until <see cref="Restore"/>.</summary>
    public void Disconnect()
    {
        _down = true;
        foreach (var stream in _streams.Keys)
        {
            stream.Drop();
        }
    }

    public void Restore() => _down = false;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(new Request(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.Headers.TryGetValues("Last-Event-ID", out var ids) ? ids.Single() : null));
        if (_down)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "The test network is down.");
        }

        var response = await _target!.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var stream = new DroppableStream(await response.Content.ReadAsStreamAsync(cancellationToken), s => _streams.TryRemove(s, out _));
            _streams.TryAdd(stream, 0);
            var content = new StreamContent(stream);
            foreach (var header in response.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = content;
        }

        return response;
    }

    /// <summary>One request that went through.</summary>
    public sealed record Request(HttpMethod Method, string Path, string? LastEventId);

    /// <summary>A response stream whose reads fail once it is dropped, as when the connection breaks.</summary>
    private sealed class DroppableStream(Stream inner, Action<DroppableStream> disposed) : Stream
    {
        private readonly CancellationTokenSource _dropped = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Drop()
        {
            try
            {
                _dropped.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _dropped.Token);
            try
            {
                return await inner.ReadAsync(buffer, linked.Token);
            }
            catch (OperationCanceledException) when (_dropped.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The test network dropped the connection.");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposed(this);
                inner.Dispose();
                _dropped.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
