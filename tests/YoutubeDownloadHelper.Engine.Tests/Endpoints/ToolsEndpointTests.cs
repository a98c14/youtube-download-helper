using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Engine.Tests.Events;
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Engine.Tests.Endpoints;

public sealed class ToolsEndpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_tools_lists_versions_channels_and_last_checks()
    {
        await using var engine = await StartInstalledAsync("2026.10.26");
        using var client = engine.CreateClient();

        var tools = await client.GetFromJsonAsync<ToolList>(ApiRoutes.Tools, TestJson.Options, Ct);

        Assert.NotNull(tools);
        Assert.True(tools.Ready);
        Assert.Equal(YtDlpChannel.Stable, tools.YtDlpChannel);
        Assert.Equal(["yt-dlp", "ffmpeg", "deno"], tools.Tools.Select(t => t.Tool));
        Assert.All(tools.Tools, t => Assert.Equal(("2026.10.26", true, true, null), (t.Version, t.Installed, t.Managed, t.PreviousVersion)));
        Assert.All(tools.Tools, t => Assert.NotNull(t.LastCheck));
        Assert.Equal([YtDlpChannel.Stable, null, null], tools.Tools.Select(t => t.Channel));
    }

    [Fact]
    public async Task A_check_runs_in_the_background_and_ends_with_tools_changed()
    {
        await using var engine = await StartInstalledAsync("1.0");
        using var client = engine.CreateClient();
        await using var events = await SseClient.ConnectAsync(engine);
        engine.ToolFeed.Publish(ManagedTool.YtDlp, "1.1");

        using var response = await client.PostAsync(ApiRoutes.ToolsCheck, null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var changed = await events.WaitForAsync(
            m => m.Type == EventTypes.ToolsChanged && TestJson.Read<ToolsChangedData>(m.Data).Tools[0].Version == "1.1",
            "yt-dlp 1.1 installed");
        Assert.True(TestJson.Read<ToolsChangedData>(changed.Data).Ready);
        var tools = await client.GetFromJsonAsync<ToolList>(ApiRoutes.Tools, TestJson.Options, Ct);
        Assert.Equal(("1.1", "1.0"), (tools!.Tools[0].Version, tools.Tools[0].PreviousVersion));
    }

    [Fact]
    public async Task A_rollback_goes_back_to_the_previous_version()
    {
        await using var engine = await StartInstalledAsync("1.0");
        var manager = engine.App.Services.GetRequiredService<ToolManager>();
        engine.ToolFeed.Publish(ManagedTool.YtDlp, "2.0");
        await manager.CheckForUpdatesAsync(cancellationToken: Ct);
        using var client = engine.CreateClient();
        await using var events = await SseClient.ConnectAsync(engine);

        using var response = await client.PostAsync(ApiRoutes.ToolPath(ApiRoutes.ToolRollback, ToolNames.YtDlp), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var changed = await events.WaitForAsync(EventTypes.ToolsChanged);
        Assert.Equal("1.0", TestJson.Read<ToolsChangedData>(changed.Data).Tools[0].Version);
        var tools = await client.GetFromJsonAsync<ToolList>(ApiRoutes.Tools, TestJson.Options, Ct);
        Assert.Equal(("1.0", "2.0"), (tools!.Tools[0].Version, tools.Tools[0].PreviousVersion));
    }

    [Fact]
    public async Task A_rollback_without_a_previous_version_or_of_an_unknown_tool_is_rejected()
    {
        await using var engine = await StartInstalledAsync("1.0");
        using var client = engine.CreateClient();

        using var noPrevious = await client.PostAsync(ApiRoutes.ToolPath(ApiRoutes.ToolRollback, ToolNames.Deno), null, Ct);
        using var unknown = await client.PostAsync(ApiRoutes.ToolPath(ApiRoutes.ToolRollback, "python"), null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, noPrevious.StatusCode);
        var error = await TestJson.ReadErrorAsync(noPrevious);
        Assert.Equal((ToolCodes.NoPreviousVersion, "deno"), (error.Code, error.Args["tool"]));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(ToolCodes.UnknownTool, (await TestJson.ReadErrorAsync(unknown)).Code);
    }

    [Fact]
    public async Task An_overridden_tool_cannot_be_rolled_back()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = engine.CreateClient();

        using var response = await client.PostAsync(ApiRoutes.ToolPath(ApiRoutes.ToolRollback, ToolNames.YtDlp), null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ToolCodes.NotManaged, (await TestJson.ReadErrorAsync(response)).Code);
        var tools = await client.GetFromJsonAsync<ToolList>(ApiRoutes.Tools, TestJson.Options, Ct);
        Assert.All(tools!.Tools, t => Assert.Equal((false, true, null), (t.Managed, t.Installed, t.Version)));
    }

    [Fact]
    public async Task Switching_the_yt_dlp_channel_installs_that_channels_build_at_once()
    {
        await using var engine = await StartInstalledAsync("2026.10.26");
        engine.ToolFeed.Publish(ManagedTool.YtDlp, "2026.11.01.233311", channel: ToolChannels.Nightly);
        using var client = engine.CreateClient();

        using (var patch = await client.PatchAsJsonAsync(ApiRoutes.Settings, new SettingsPatch(YtDlpChannel: YtDlpChannel.Nightly), TestJson.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        var nightly = await WaitForYtDlpAsync(client, t => t.Channel == YtDlpChannel.Nightly);
        Assert.Equal("2026.11.01.233311", nightly.Version);

        using (var patch = await client.PatchAsJsonAsync(ApiRoutes.Settings, new SettingsPatch(YtDlpChannel: YtDlpChannel.Stable), TestJson.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        var stable = await WaitForYtDlpAsync(client, t => t.Channel == YtDlpChannel.Stable);
        Assert.Equal(("2026.10.26", "2026.11.01.233311"), (stable.Version, stable.PreviousVersion));
    }

    private static async Task<TestEngine> StartInstalledAsync(string version)
    {
        var engine = await TestEngine.StartAsync(toolFeed: new FakeToolFeed().PublishAll(version));
        try
        {
            // The startup pass: the health check installs everything, then the update check records the check times.
            var manager = engine.App.Services.GetRequiredService<ToolManager>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Timeout);
            while (!manager.ToolsReady || (await manager.GetDetailsAsync(timeout.Token)).Tools.Any(t => t.LastCheck is null))
            {
                await Task.Delay(10, timeout.Token);
            }

            return engine;
        }
        catch
        {
            await engine.DisposeAsync();
            throw;
        }
    }

    private static async Task<ToolDetails> WaitForYtDlpAsync(HttpClient client, Func<ToolDetails, bool> match)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);
        while (true)
        {
            var tools = await client.GetFromJsonAsync<ToolList>(ApiRoutes.Tools, TestJson.Options, timeout.Token);
            if (match(tools!.Tools[0]))
            {
                return tools.Tools[0];
            }

            await Task.Delay(20, timeout.Token);
        }
    }
}
