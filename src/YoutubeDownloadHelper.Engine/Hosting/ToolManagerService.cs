using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Runs the <see cref="ToolManager"/>'s background loop with the host: the startup health check, installs and repairs
/// (PLAN.md §5.6). <see cref="EngineHost.StartAsync"/> has loaded the installed versions before the host starts.
/// Stopping cancels an install in progress, which leaves nothing half-installed.
/// </summary>
public sealed class ToolManagerService(ToolManager tools) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => tools.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => tools.StopAsync(cancellationToken);
}
