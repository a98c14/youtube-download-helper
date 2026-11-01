using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Engine.Events;
using YoutubeDownloadHelper.Engine.Tests.Events;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

/// <summary>
/// The idle exit (PLAN.md §7.4) on a fake clock. The clock moves a minute at a time with a short real pause in between, so
/// the service's timer is usually armed again before the next step. A step that finds it unarmed only delays the exit,
/// so the tests check the lower bound exactly and the upper bound loosely.
/// </summary>
public sealed class IdleExitTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_engine_exits_ten_minutes_after_the_last_client_left_an_idle_queue()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        await using var engine = await TestEngine.StartAsync(configureServices: s => s.AddSingleton<TimeProvider>(clock));
        var presence = engine.App.Services.GetRequiredService<IClientPresence>();

        await using (var events = await SseClient.ConnectAsync(engine))
        {
            await EventStreamTests.WaitUntilAsync(() => presence.ConnectedClients == 1);
            await AdvanceAsync(clock, engine, minutes: 15);
            Assert.False(Stopping(engine), "A connected client keeps the engine running.");
        }

        await EventStreamTests.WaitUntilAsync(() => presence.ConnectedClients == 0);
        var left = clock.GetUtcNow();
        var minutes = await AdvanceAsync(clock, engine, minutes: 40);

        Assert.True(Stopping(engine), "The engine did not exit.");
        Assert.InRange(minutes, 10, 39);
        Assert.Equal(left, presence.LastDisconnectedAt);
    }

    [Fact]
    public async Task A_queued_item_or_keep_downloading_after_exit_keeps_the_engine_running()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        await using var engine = await TestEngine.StartAsync(configureServices: s => s.AddSingleton<TimeProvider>(clock));
        using var client = engine.CreateClient();

        // The tools are missing, so the item waits in the queue: it would start once they are installed.
        using (await client.PostAsJsonAsync(ApiRoutes.Items, new AddItemsRequest(Urls: ["https://fake.test/success"]), TestJson.Options, Ct))
        {
        }

        await AdvanceAsync(clock, engine, minutes: 15);
        Assert.False(Stopping(engine), "A queued item keeps the engine running.");

        using (await client.PatchAsJsonAsync(ApiRoutes.Settings, new SettingsPatch(KeepDownloadingAfterExit: true), TestJson.Options, Ct))
        using (await client.PostAsync(ApiRoutes.QueuePause, null, Ct))
        {
        }

        await AdvanceAsync(clock, engine, minutes: 15);
        Assert.False(Stopping(engine), "Keep downloading after Exit keeps the engine running.");

        using (await client.PatchAsJsonAsync(ApiRoutes.Settings, new SettingsPatch(KeepDownloadingAfterExit: false), TestJson.Options, Ct))
        {
        }

        Assert.InRange(await AdvanceAsync(clock, engine, minutes: 40), 10, 39);
    }

    private static bool Stopping(TestEngine engine) => engine.App.Lifetime.ApplicationStopping.IsCancellationRequested;

    /// <summary>Moves the clock a minute at a time until the engine stops or <paramref name="minutes"/> passed; returns the minutes moved.</summary>
    private static async Task<int> AdvanceAsync(FakeTimeProvider clock, TestEngine engine, int minutes)
    {
        for (var i = 1; i <= minutes; i++)
        {
            clock.Advance(Step);
            await Task.Delay(50, Ct);
            if (Stopping(engine))
            {
                return i;
            }
        }

        return minutes;
    }
}
