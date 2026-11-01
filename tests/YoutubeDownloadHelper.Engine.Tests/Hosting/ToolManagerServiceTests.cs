using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

public sealed class ToolManagerServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_engine_installs_the_tools_and_repairs_a_deleted_binary()
    {
        await using var engine = await TestEngine.StartAsync(toolFeed: new FakeToolFeed().PublishAll("1.0"));
        var services = engine.App.Services;
        var tools = services.GetRequiredService<ToolManager>();
        var scheduler = services.GetRequiredService<Scheduler>();
        Assert.Same(tools, services.GetRequiredService<IToolLocator>());
        Assert.Same(tools, services.GetRequiredService<IToolHealth>());
        Assert.Same(tools, services.GetRequiredService<IToolStatusProvider>());
        Assert.Same(tools, services.GetRequiredService<IToolUpdateTrigger>());

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            timeout.CancelAfter(Timeout);
            while (!(tools.ToolsReady && scheduler.Passes > 0 && !scheduler.GetBlocks().Contains(QueueBlock.ToolsMissing)))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        Assert.StartsWith(Path.Combine(engine.Paths.ToolsDir, "yt-dlp", "1.0"), tools.Current.YtDlp, StringComparison.Ordinal);
        using var events = services.GetRequiredService<EventBus>().Subscribe();

        File.Delete(tools.Current.YtDlp);
        scheduler.Wake();

        await WaitForBlockAsync(events, active: true);
        await WaitForBlockAsync(events, active: false);
        Assert.True(File.Exists(tools.Current.YtDlp));
    }

    private static async Task WaitForBlockAsync(EventSubscription events, bool active)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);
        while (await events.Reader.ReadAsync(timeout.Token) is not QueueBlockChangedEvent { Block: QueueBlock.ToolsMissing } e || e.Active != active)
        {
        }
    }
}
