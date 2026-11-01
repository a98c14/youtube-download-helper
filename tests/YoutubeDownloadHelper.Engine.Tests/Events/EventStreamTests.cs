using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Engine.Events;

namespace YoutubeDownloadHelper.Engine.Tests.Events;

/// <summary>The event stream (PLAN.md §6.3) and the engine's end-to-end behaviour seen through it.</summary>
public sealed class EventStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_download_streams_added_progress_and_completed_then_a_notification()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        await using var events = await SseClient.ConnectAsync(engine);
        using var client = engine.CreateClient();
        Assert.Equal("text/event-stream", events.Response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(EventTypes.Resync, (await events.NextAsync())!.Type);

        var id = await AddAsync(client, "https://fake.test/success?id=stream00001&title=Akış&size=1M&speed=1M&ppdelay=0.3");

        var added = await events.WaitForAsync(EventTypes.ItemAdded);
        var progress = await events.WaitForAsync(m => m.Type == EventTypes.ItemProgress, "progress");
        var completed = await events.WaitForAsync(
            m => m.Type == EventTypes.ItemUpdated && TestJson.Read<ItemUpdatedData>(m.Data).Item.State == ItemState.Completed,
            "the item to complete");
        var notify = await events.WaitForAsync(EventTypes.Notify);

        Assert.Equal(id, TestJson.Read<ItemAddedData>(added.Data).Item.Id);
        Assert.Equal(id, TestJson.Read<ItemProgressData>(progress.Data).ItemId);
        var states = events.Received
            .Where(m => m.Type == EventTypes.ItemUpdated)
            .Select(m => TestJson.Read<ItemUpdatedData>(m.Data).Item.State)
            .ToList();
        Assert.Equal([ItemState.Resolving, ItemState.Downloading, ItemState.PostProcessing, ItemState.Completed], states);
        Assert.Contains(
            events.Received.Where(m => m.Type == EventTypes.ItemProgress).Select(m => TestJson.Read<ItemProgressData>(m.Data).Progress),
            p => p.Phase == ProgressPhases.Downloading && p.Fraction is > 0 and < 1 && p.TotalBytes == 1 << 20);
        var item = TestJson.Read<ItemUpdatedData>(completed.Data).Item;
        Assert.Equal((0L, "Akış"), (item.Position, item.Title));
        Assert.True(File.Exists(item.FilePath));
        var message = TestJson.Read<NotifyData>(notify.Data);
        Assert.Equal((NotifyCodes.ItemCompleted, id, "Akış"), (message.Message.Code, message.ItemId, message.Message.Args["title"]));
        Assert.True(events.Received.Zip(events.Received.Skip(1)).All(p => p.First.Id < p.Second.Id), "Event ids increase.");

        // It left the queue for history, with its run recorded.
        Assert.Empty((await client.GetFromJsonAsync<ItemList>(ApiRoutes.Items, TestJson.Options, Ct))!.Items);
        var history = await client.GetFromJsonAsync<HistoryList>(ApiRoutes.History + "?search=fake.test", TestJson.Options, Ct);
        Assert.Equal([id], history!.Items.Select(i => i.Id));
        var detail = await client.GetFromJsonAsync<ItemDetail>(ApiRoutes.ItemPath(ApiRoutes.Item, id), TestJson.Options, Ct);
        var attempt = Assert.Single(detail!.Attempts);
        Assert.Equal((ItemState.Completed, 0), (attempt.EndState, attempt.ExitCode));
        Assert.NotEmpty(detail.OutputTail);
    }

    [Fact]
    public async Task A_downloaded_video_is_a_duplicate_unless_forced()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        await using var events = await SseClient.ConnectAsync(engine);
        using var client = engine.CreateClient();
        var id = await AddAsync(client, "https://youtu.be/archived001");
        await events.WaitForAsync(
            m => m.Type == EventTypes.ItemUpdated && TestJson.Read<ItemUpdatedData>(m.Data).Item.State == ItemState.Completed,
            "the item to complete");

        var again = await AddRawAsync(client, new AddItemsRequest(VideoIds: ["archived001"]));
        var forced = await AddRawAsync(client, new AddItemsRequest(VideoIds: ["archived001"], Force: true));

        Assert.Equal((AddItemOutcome.Duplicate, id, ItemCodes.AlreadyDownloaded), (again.Outcome, again.ItemId, again.Message?.Code));
        Assert.Equal(AddItemOutcome.Added, forced.Outcome);
    }

    [Fact]
    public async Task A_reconnect_with_Last_Event_ID_replays_the_missed_events()
    {
        // Fake tools: no install attempts, so nothing else publishes meanwhile.
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = engine.CreateClient();
        long seen;
        await using (var first = await SseClient.ConnectAsync(engine))
        {
            await AddAsync(client, "https://fake.test/success?id=replay00001", paused: true);
            seen = (await first.WaitForAsync(EventTypes.ItemAdded)).Id;
        }

        var missed1 = await AddAsync(client, "https://fake.test/success?id=replay00002", paused: true);
        var missed2 = await AddAsync(client, "https://fake.test/success?id=replay00003", paused: true);
        await using var second = await SseClient.ConnectAsync(engine, lastEventId: seen);

        var replayed1 = await second.WaitForAsync(EventTypes.ItemAdded);
        var replayed2 = await second.WaitForAsync(EventTypes.ItemAdded);

        Assert.True(second.Received.TakeWhile(m => m != replayed1).All(m => m.Type != EventTypes.Resync), "No resync.");
        Assert.Equal(missed1, TestJson.Read<ItemAddedData>(replayed1.Data).Item.Id);
        Assert.Equal(missed2, TestJson.Read<ItemAddedData>(replayed2.Data).Item.Id);
        Assert.True(replayed1.Id > seen);

        // Live events follow the replay.
        await client.PostAsync(ApiRoutes.QueuePause, null, Ct);
        var blocked = await second.WaitForAsync(EventTypes.QueueBlocked);
        Assert.Equal(QueueBlock.PausedByUser, TestJson.Read<QueueBlockData>(blocked.Data).Block);
    }

    [Fact]
    public async Task A_stream_from_a_snapshot_sequence_starts_after_the_snapshot()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = engine.CreateClient();
        var before = await AddAsync(client, "https://fake.test/success?id=snapshot001", paused: true);
        var snapshot = (await client.GetFromJsonAsync<ItemList>(ApiRoutes.Items, TestJson.Options, Ct))!;

        await using var events = await SseClient.ConnectAsync(engine, lastEventId: snapshot.Sequence);
        var after = await AddAsync(client, "https://fake.test/success?id=snapshot002", paused: true);
        var first = (await events.NextAsync())!;

        Assert.Equal([before], snapshot.Items.Select(i => i.Id));
        Assert.Equal(EventTypes.ItemAdded, first.Type);
        Assert.Equal(after, TestJson.Read<ItemAddedData>(first.Data).Item.Id);
        Assert.True(first.Id > snapshot.Sequence);
    }

    [Fact]
    public async Task A_gap_larger_than_the_buffer_or_an_unknown_id_gets_a_resync()
    {
        await using var engine = await TestEngine.StartAsync(
            fakeTools: true,
            configureServices: s => s.AddSingleton(new EventStreamOptions { BufferSize = 3 }));
        using var client = engine.CreateClient();
        var journal = engine.App.Services.GetRequiredService<EventJournal>();
        var seen = journal.LastSequence;

        for (var i = 0; i < 3; i++)
        {
            await client.PostAsync(ApiRoutes.QueuePause, null, Ct);
            await client.PostAsync(ApiRoutes.QueueResume, null, Ct);
        }

        await WaitUntilAsync(() => journal.LastSequence >= seen + 6);
        var last = journal.LastSequence;
        await using var tooOld = await SseClient.ConnectAsync(engine, lastEventId: seen);
        await using var unknown = await SseClient.ConnectAsync(engine, lastEventId: last + 100);
        await using var recent = await SseClient.ConnectAsync(engine, lastEventId: last - 1);

        var resync = (await tooOld.NextAsync())!;
        Assert.Equal((EventTypes.Resync, last), (resync.Type, resync.Id));
        Assert.Equal(last, TestJson.Read<ResyncData>(resync.Data).Sequence);
        Assert.Equal(EventTypes.Resync, (await unknown.NextAsync())!.Type);
        var replayed = (await recent.NextAsync())!;
        Assert.Equal((EventTypes.QueueUnblocked, last), (replayed.Type, replayed.Id));
    }

    [Fact]
    public async Task An_idle_stream_gets_heartbeats()
    {
        await using var engine = await TestEngine.StartAsync(
            configureServices: s => s.AddSingleton(new EventStreamOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(50) }));
        await using var events = await SseClient.ConnectAsync(engine);

        Assert.Equal(EventTypes.Resync, (await events.NextAsync())!.Type);

        await events.WaitForCommentsAsync(2);

        Assert.True(events.Comments >= 2);
    }

    [Fact]
    public async Task Shutdown_streams_engine_stopping_and_puts_running_jobs_back_in_the_queue()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = engine.CreateClient();
        await using var events = await SseClient.ConnectAsync(engine);
        var id = await AddAsync(client, "https://fake.test/hang?id=shutdown001&size=1M");
        await events.WaitForAsync(
            m => m.Type == EventTypes.ItemProgress && TestJson.Read<ItemProgressData>(m.Data).Progress.DownloadedBytes > 0,
            "the download to start");

        using var response = await client.PostAsync(ApiRoutes.EngineShutdown, null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await events.WaitForAsync(EventTypes.EngineStopping);
        Assert.Null(await events.NextAsync());

        // What Program does when the lifetime says stop.
        await engine.StopAsync();

        await using var db = await engine.App.Services.GetRequiredService<IDbContextFactory<StateDbContext>>().CreateDbContextAsync(Ct);
        var item = await db.Items.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
        var last = await db.ItemEvents.AsNoTracking().Where(e => e.ItemId == id).OrderBy(e => e.Id).LastAsync(Ct);
        Assert.Equal(ItemState.Queued, item.State);
        Assert.Equal((ItemState.Downloading, ItemEventReasons.EngineStopped), (last.FromState, last.Reason));
        Assert.NotEmpty(Directory.EnumerateFiles(engine.Paths.WorkDirFor(id), "*.part", SearchOption.AllDirectories));
    }


    private static async Task<long> AddAsync(HttpClient client, string url, bool paused = false)
    {
        var result = await AddRawAsync(client, new AddItemsRequest(Urls: [url], Paused: paused));
        Assert.Equal(AddItemOutcome.Added, result.Outcome);
        return result.ItemId!.Value;
    }

    private static async Task<AddItemResult> AddRawAsync(HttpClient client, AddItemsRequest request)
    {
        using var response = await client.PostAsJsonAsync(ApiRoutes.Items, request, TestJson.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await TestJson.ReadAsync<AddItemsResponse>(response)).Results);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(SseClient.Timeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
