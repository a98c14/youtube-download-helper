using System.Collections.ObjectModel;
using System.ComponentModel;
using YoutubeDownloadHelper.Client.Discovery;
using YoutubeDownloadHelper.Client.Events;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Contracts.Info;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Tools;

namespace YoutubeDownloadHelper.Client.Store;

/// <summary>
/// The client-side picture of the engine that view models bind to (PLAN.md §6.3): the queue, the queue blocks, the
/// tools and the login, kept up to date from the event stream, plus the connection state.
/// <para>
/// It connects without <c>Last-Event-ID</c>, so the stream starts with <c>resync</c>; on every <c>resync</c> it fetches
/// the snapshots (<c>GET /items</c>, then <c>GET /info</c>) and ignores events whose id is not above the snapshot's
/// sequence. A lost connection is reopened with backoff and <c>Last-Event-ID</c>, so the engine replays what was
/// missed or sends <c>resync</c>; another engine (a new token or PID from the <see cref="IEngineLocator"/>) always
/// gets a fresh snapshot.
/// </para>
/// <para>
/// Every change and every event is raised on <see cref="EngineStoreOptions.SynchronizationContext"/>, or, without one,
/// on a background thread while <see cref="SyncRoot"/> is held. Handlers must not block on that context's thread.
/// </para>
/// </summary>
public sealed class EngineStore : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly ReconnectingEventStream _stream;
    private readonly EngineStoreOptions _options;
    private readonly ItemCollection _items = [];
    private readonly ObservableCollection<QueueBlock> _blocks = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private TaskCompletionSource _synced = NewSignal();

    // Changed only inside Apply (on the context, holding SyncRoot).
    private ConnectionState _state;
    private EngineClient? _client;
    private EngineInfo? _info;
    private IReadOnlyList<ToolInfo> _tools = [];
    private bool _toolsReady;
    private AuthInfo? _auth;
    private long? _snapshotSequence;
    private long? _lastSequence;
    private Exception? _lastError;
    private int _resyncs;
    private int _disposed;

    public EngineStore(IEngineLocator locator, EngineStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _options = options ?? new EngineStoreOptions();
        _stream = new ReconnectingEventStream(locator, _options.Client, _options.Reconnect);
        Items = new ReadOnlyObservableCollection<ItemInfo>(_items);
        Blocks = new ReadOnlyObservableCollection<QueueBlock>(_blocks);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A <c>notify</c> event: something to tell the user (a toast).</summary>
    public event EventHandler<NotifyData>? Notified;

    /// <summary>
    /// An <c>auth.refreshRequested</c> event: the stored cookies failed verification and the engine waits for a browser
    /// refresh (<see cref="EngineClient.BeginAuthRefreshAsync"/>) before it reports the login as expired.
    /// </summary>
    public event EventHandler? AuthRefreshRequested;

    /// <summary>Every event of the stream that was applied (after the store changed), including ones it does not use.</summary>
    public event EventHandler<ServerEvent>? EventReceived;

    /// <summary>The store was refilled from fresh snapshots.</summary>
    public event EventHandler? Resynced;

    /// <summary>Held while the store changes, when there is no <see cref="EngineStoreOptions.SynchronizationContext"/>.</summary>
    public object SyncRoot { get; } = new();

    /// <summary>The queue in order, with live progress.</summary>
    public ReadOnlyObservableCollection<ItemInfo> Items { get; }

    /// <summary>The conditions stopping new downloads; empty when the queue runs.</summary>
    public ReadOnlyObservableCollection<QueueBlock> Blocks { get; }

    public ConnectionState State => _state;

    /// <summary>The client of the engine connected to, for commands; null before the first connection.</summary>
    public EngineClient? Client => _client;

    /// <summary>The engine connected to.</summary>
    public EngineDiscovery? Engine => _client?.Engine;

    /// <summary>The last <c>GET /info</c> snapshot (versions; tools, login and blocks as they were then).</summary>
    public EngineInfo? Info => _info;

    /// <summary>The managed tools as they are now.</summary>
    public IReadOnlyList<ToolInfo> Tools => _tools;

    /// <summary>Whether every tool is installed and works, so downloads can run.</summary>
    public bool ToolsReady => _toolsReady;

    /// <summary>The YouTube login as it is now.</summary>
    public AuthInfo? Auth => _auth;

    /// <summary>The id of the last event applied (or the snapshot's sequence).</summary>
    public long? Sequence
    {
        get
        {
            lock (SyncRoot)
            {
                return _lastSequence;
            }
        }
    }

    /// <summary>Why the last connection or snapshot failed; null while connected.</summary>
    public Exception? LastError => _lastError;

    /// <summary>How many times the store was refilled from snapshots.</summary>
    public int ResyncCount => _resyncs;

    /// <summary>Starts connecting in the background. Call once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (!_loop.IsCompleted || _stop.IsCancellationRequested)
        {
            throw new InvalidOperationException("The store is already started.");
        }

        Apply(() => SetState(ConnectionState.Connecting));
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Completes once the store is <see cref="ConnectionState.Connected"/> (at once if it is).</summary>
    public Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _synced).Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        _stream.Dispose();
        _stop.Dispose();
        Apply(() =>
        {
            SetClient(null);
            SetState(ConnectionState.Disconnected);
        });
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var signal in _stream.ReadAsync(cancellationToken))
                {
                    switch (signal)
                    {
                        case StreamConnected connected:
                            OnConnected(connected);
                            break;
                        case StreamEventReceived received:
                            await OnEventAsync(received.Event, cancellationToken);
                            failures = 0;
                            break;
                        case StreamDisconnected disconnected:
                            OnDisconnected(disconnected.Error);
                            break;
                    }
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException or HttpRequestException)
            {
                // Stopping aborts the stream; the transport may report that as an I/O error instead of a cancel.
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A snapshot could not be fetched, an event did not parse, or a handler threw. The loop must not die:
                // start over from a fresh snapshot.
                _stream.ForgetPosition();
                OnDisconnected(ex);
                await Task.Delay(_options.Reconnect.DelayFor(failures++), _options.Client.TimeProvider, cancellationToken);
            }
        }
    }

    private void OnConnected(StreamConnected connected) => Apply(() =>
    {
        SetClient(connected.Client);
        if (connected.EngineChanged)
        {
            _snapshotSequence = null;
        }

        _lastError = null;
        OnPropertyChanged(nameof(LastError));

        // A resumed stream continues where the store is; otherwise it waits for the resync's snapshot. An engine that
        // said it is stopping may still take connections until it has stopped: it stays stopped.
        if (connected.ResumedAfter is not null && _snapshotSequence is not null && _state != ConnectionState.EngineStopped)
        {
            SetConnected();
        }
    });

    private void OnDisconnected(Exception? error) => Apply(() =>
    {
        _lastError = error;
        OnPropertyChanged(nameof(LastError));
        if (_state == ConnectionState.Connected)
        {
            SetState(ConnectionState.Reconnecting);
        }
    });

    private async Task OnEventAsync(ServerEvent e, CancellationToken cancellationToken)
    {
        if (e.Type == EventTypes.Resync)
        {
            await ResyncAsync(cancellationToken);
            return;
        }

        if (_snapshotSequence is not { } snapshot || e.Id <= snapshot)
        {
            // Before the first snapshot, or already in it.
            return;
        }

        var data = e.GetData();
        var consistent = true;
        Apply(() =>
        {
            consistent = ApplyEvent(e.Type, data);
            _lastSequence = e.Id;
            OnPropertyChanged(nameof(Sequence));
            EventReceived?.Invoke(this, e);
            if (data is NotifyData notify)
            {
                Notified?.Invoke(this, notify);
            }
            else if (data is AuthRefreshRequestedData)
            {
                AuthRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
        });

        if (!consistent)
        {
            // An event about an item the store does not have: the snapshot missed something. Refetch.
            await ResyncAsync(cancellationToken);
        }
    }

    /// <summary>Applies one event; false if it does not fit the store (then the store refetches its snapshot).</summary>
    private bool ApplyEvent(string type, object? data)
    {
        switch (data)
        {
            case ItemAddedData added:
                Upsert(added.Item);
                return true;
            case ItemUpdatedData updated:
                Upsert(updated.Item);
                return true;
            case ItemProgressData progress:
                if (_items.Find(progress.ItemId) is { } running)
                {
                    _items.Upsert(running with { Progress = progress.Progress });
                }

                return true;
            case ItemRemovedData removed:
                _items.RemoveId(removed.ItemId);
                return true;
            case QueueReorderedData reordered:
                return _items.SetPositions([.. reordered.Positions.Select(p => (p.ItemId, p.Position))]);
            case QueueBlockData block when type == EventTypes.QueueBlocked:
                if (!_blocks.Contains(block.Block))
                {
                    _blocks.Add(block.Block);
                }

                return true;
            case QueueBlockData block:
                _blocks.Remove(block.Block);
                return true;
            case ToolsChangedData tools:
                _tools = tools.Tools;
                _toolsReady = tools.Ready;
                OnPropertyChanged(nameof(Tools));
                OnPropertyChanged(nameof(ToolsReady));
                return true;
            case AuthChangedData auth:
                _auth = auth.Auth;
                OnPropertyChanged(nameof(Auth));
                return true;
            case EngineStoppingData:
                SetState(ConnectionState.EngineStopped);
                return true;
            default:
                return true;
        }
    }

    /// <summary>An item in the queue is added or replaced; one that left it (to history, or removed) is dropped.</summary>
    private void Upsert(ItemInfo item)
    {
        if (item.Position > 0 && item.State != ItemState.Removed)
        {
            _items.Upsert(item);
        }
        else
        {
            _items.RemoveId(item.Id);
        }
    }

    private async Task ResyncAsync(CancellationToken cancellationToken)
    {
        var client = _stream.Client ?? throw new InvalidOperationException("Not connected.");

        // Items first: info is newer than the items' sequence, and every later event is applied over both.
        var snapshot = await client.GetQueueSnapshotAsync(cancellationToken: cancellationToken);
        var info = await client.GetInfoAsync(cancellationToken);
        Apply(() =>
        {
            _items.ReplaceAll(snapshot.Items);
            _blocks.Clear();
            foreach (var block in info.Blocks.Distinct())
            {
                _blocks.Add(block);
            }

            _info = info;
            _tools = info.Tools;
            _auth = info.Auth;
            _toolsReady = !info.Blocks.Contains(QueueBlock.ToolsMissing);
            _snapshotSequence = snapshot.Sequence;
            _lastSequence = snapshot.Sequence;
            _lastError = null;
            _resyncs++;
            foreach (var name in (string[])[nameof(Info), nameof(Tools), nameof(ToolsReady), nameof(Auth), nameof(Sequence), nameof(LastError), nameof(ResyncCount)])
            {
                OnPropertyChanged(name);
            }

            SetConnected();
            Resynced?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SetConnected()
    {
        SetState(ConnectionState.Connected);
        _synced.TrySetResult();
    }

    private void SetState(ConnectionState state)
    {
        if (_state != state)
        {
            if (_state == ConnectionState.Connected)
            {
                Volatile.Write(ref _synced, NewSignal());
            }

            _state = state;
            OnPropertyChanged(nameof(State));
        }
    }

    private void SetClient(EngineClient? client)
    {
        if (!ReferenceEquals(_client, client))
        {
            _client = client;
            OnPropertyChanged(nameof(Client));
            OnPropertyChanged(nameof(Engine));
        }
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Runs a change on the context (waiting for it), holding <see cref="SyncRoot"/>.</summary>
    private void Apply(Action change)
    {
        var context = _options.SynchronizationContext;
        if (context is null || SynchronizationContext.Current == context)
        {
            lock (SyncRoot)
            {
                change();
            }

            return;
        }

        context.Send(
            _ =>
            {
                lock (SyncRoot)
                {
                    change();
                }
            },
            null);
    }
}
