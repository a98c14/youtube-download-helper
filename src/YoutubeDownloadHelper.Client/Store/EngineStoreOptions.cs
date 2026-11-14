using YoutubeDownloadHelper.Client.Events;

namespace YoutubeDownloadHelper.Client.Store;

/// <summary>Where an <see cref="EngineStore"/> stands with the engine.</summary>
public enum ConnectionState
{
    /// <summary>Not started, or disposed.</summary>
    Disconnected,

    /// <summary>Started; no snapshot yet.</summary>
    Connecting,

    /// <summary>The event stream is open and the store is up to date.</summary>
    Connected,

    /// <summary>The connection was lost; the store shows the last state it knew and retries with backoff.</summary>
    Reconnecting,

    /// <summary>The engine said it is stopping (<c>engine.stopping</c>); the store retries, in case it comes back.</summary>
    EngineStopped,
}

/// <summary>How an <see cref="EngineStore"/> connects and where it raises its notifications.</summary>
public sealed class EngineStoreOptions
{
    public EngineClientOptions Client { get; init; } = new();

    public ReconnectPolicy Reconnect { get; init; } = ReconnectPolicy.Default;

    /// <summary>
    /// Where the store changes its state and raises its events, e.g. the UI thread's context so bound collections can
    /// be changed safely. Null: on a background thread, while holding <see cref="EngineStore.SyncRoot"/> (WPF:
    /// <c>BindingOperations.EnableCollectionSynchronization(collection, store.SyncRoot)</c>).
    /// </summary>
    public SynchronizationContext? SynchronizationContext { get; init; }
}
