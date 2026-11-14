using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

/// <summary>The in-process test engine has nothing that reaches the real network (CLAUDE.md rule 6).</summary>
public sealed class TestEngineIsolationTests
{
    [Fact]
    public async Task Every_network_seam_is_a_fake()
    {
        await using var engine = await TestEngine.StartAsync();
        var services = engine.App.Services;

        Assert.Same(engine.Network, services.GetRequiredService<INetworkProbe>());
        Assert.Same(engine.ToolFeed, services.GetRequiredService<IToolReleaseFeed>());
        Assert.IsType<FakeSmokeTest>(services.GetRequiredService<IToolSmokeTest>());
        Assert.DoesNotContain(services.GetServices<INetworkProbe>(), p => p is HttpNetworkProbe);
    }
}
