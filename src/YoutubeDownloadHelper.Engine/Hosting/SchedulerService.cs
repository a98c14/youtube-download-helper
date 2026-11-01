using YoutubeDownloadHelper.Core.Scheduling;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Runs the <see cref="Scheduler"/> with the host. <see cref="EngineHost.StartAsync"/> starts the queue actor before
/// the host, so the actor is up when this starts; stopping pauses the running jobs (PLAN.md §5.1, graceful stop).
/// </summary>
public sealed class SchedulerService(Scheduler scheduler) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => scheduler.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => scheduler.StopAsync(cancellationToken);
}
