using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Engine.Events;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>Settings of the idle exit (PLAN.md §7.4).</summary>
public sealed class IdleExitOptions
{
    /// <summary>How long the engine waits with no client and an idle queue before it exits.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often it looks.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The idle exit (PLAN.md §7.4): unless "keep downloading after Exit" is on, the engine stops itself once it has had no
/// event-stream client (<see cref="IClientPresence"/>) and an idle queue for <see cref="IdleExitOptions.IdleTimeout"/>.
/// It covers a crashed app; an app that exits normally shuts the engine down itself. The queue is idle when nothing
/// runs and nothing will start on its own: no item is resolving, downloading, post-processing or waiting for a retry,
/// and no item is queued unless the user paused the queue.
/// </summary>
public sealed partial class IdleExitService(
    IClientPresence clients,
    QueueActor queue,
    SettingsStore settingsStore,
    IdleExitOptions options,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<IdleExitService> logger) : BackgroundService
{
    public static bool IsIdle(QueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return !snapshot.Items.Any(i =>
            ItemStateMachine.IsRunning(i.State)
            || i.State == ItemState.WaitingRetry
            || (i.State == ItemState.Queued && !snapshot.Paused));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? idleSince = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CheckInterval, time, stoppingToken);
                idleSince = await CheckAsync(idleSince, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogCheckFailed(logger, ex);
            }
        }
    }

    /// <summary>One look. Returns since when the engine has been idle, or null when it is not.</summary>
    private async Task<DateTimeOffset?> CheckAsync(DateTimeOffset? idleSince, CancellationToken cancellationToken)
    {
        if (clients.ConnectedClients > 0)
        {
            return null;
        }

        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (settings.KeepDownloadingAfterExit || !IsIdle(await queue.GetSnapshotAsync(cancellationToken)))
        {
            return null;
        }

        // A client that came and went between two looks also counts.
        var now = time.GetUtcNow();
        var since = idleSince ?? now;
        if (clients.LastDisconnectedAt is { } left && left > since)
        {
            since = left;
        }

        if (now - since >= options.IdleTimeout)
        {
            LogIdleExit(logger, options.IdleTimeout);
            lifetime.StopApplication();
        }

        return since;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The idle exit check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exiting: no client and an idle queue for {Timeout}")]
    private static partial void LogIdleExit(ILogger logger, TimeSpan timeout);
}
