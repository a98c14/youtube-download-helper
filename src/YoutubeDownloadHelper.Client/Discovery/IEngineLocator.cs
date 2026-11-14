using YoutubeDownloadHelper.Contracts.Discovery;

namespace YoutubeDownloadHelper.Client.Discovery;

/// <summary>
/// Finds the engine to connect to. The event stream asks before every (re)connect, so a locator that returns a new
/// engine (a new token or PID) moves the client over to it, with a fresh snapshot. The app's engine supervisor
/// (PLAN.md §7.4) implements this to start the engine when none answers; <see cref="DiscoveryFileLocator"/> only reads
/// the discovery file.
/// </summary>
public interface IEngineLocator
{
    /// <summary>The engine to connect to, or null when there is none right now (the caller retries later).</summary>
    ValueTask<EngineDiscovery?> LocateAsync(CancellationToken cancellationToken);
}

/// <summary>Always the same engine.</summary>
public sealed class FixedEngineLocator(EngineDiscovery engine) : IEngineLocator
{
    public ValueTask<EngineDiscovery?> LocateAsync(CancellationToken cancellationToken) => ValueTask.FromResult<EngineDiscovery?>(engine);
}

/// <summary>
/// The engine named by a discovery file (by default <see cref="EngineDiscoveryFile.DefaultPath"/>), unless the file is
/// missing, unreadable, of another API version, or names a process that is gone. Whether the engine answers is left
/// to the connection itself.
/// </summary>
public sealed class DiscoveryFileLocator(string? path = null) : IEngineLocator
{
    public string Path { get; } = path ?? EngineDiscoveryFile.DefaultPath;

    public ValueTask<EngineDiscovery?> LocateAsync(CancellationToken cancellationToken)
    {
        var discovery = EngineDiscoveryFile.TryRead(Path);
        return ValueTask.FromResult(
            discovery is not null
            && discovery.ApiVersion == Contracts.ApiRoutes.Version
            && EngineDiscoveryFile.IsProcessRunning(discovery.Pid)
                ? discovery
                : null);
    }
}
