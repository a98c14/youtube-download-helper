using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Core.Tests.Queue;

public sealed class QueueActorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Added_items_are_queued_in_order_with_a_creation_event()
    {
        await using var h = await QueueHarness.StartAsync();
        var a = await h.AddAsync("https://a");
        var b = await h.AddAsync("https://b", paused: true);

        Assert.Equal((ItemState.Queued, 1), (a.State, a.Position));
        Assert.Equal((ItemState.Paused, 2), (b.State, b.Position));

        var events = await h.ItemEventsAsync(a.Id);
        Assert.Equal([(null, ItemState.Added), (ItemState.Added, ItemState.Queued)], events.Select(e => (e.FromState, e.ToState)));
        Assert.All(events, e => Assert.Equal(ItemEventReasons.Added, e.Reason));

        var published = h.DrainEvents();
        Assert.Equal([a, b], published.Cast<ItemAddedEvent>().Select(e => e.Item));
    }

    [Fact]
    public async Task A_job_runs_to_completion_and_the_item_leaves_the_queue()
    {
        await using var h = await QueueHarness.StartAsync();
        var item = await h.AddAsync();
        var other = await h.AddAsync();
        var workDir = h.Root.Paths.WorkDirFor(item.Id);
        Directory.CreateDirectory(workDir);
        h.DrainEvents();

        Assert.True((await h.Actor.StartResolvingAsync(item.Id, Ct)).Succeeded);
        Assert.True((await h.Actor.MetadataResolvedAsync(item.Id, new ItemMetadata(Extractor: "youtube", VideoId: "abc", Title: "Başlık"), Ct)).Succeeded);
        Assert.True((await h.Actor.StartPostProcessingAsync(item.Id, Ct)).Succeeded);
        Assert.True((await h.Actor.CompleteAsync(item.Id, @"C:\out\Başlık.mp4", 1234, new JobOutput(0, ["line 1", "line 2"]), Ct)).Succeeded);

        var stored = await h.ItemAsync(item.Id);
        Assert.Equal((ItemState.Completed, "abc", "Başlık", @"C:\out\Başlık.mp4", 1234L), (stored.State, stored.VideoId, stored.Title, stored.FilePath, stored.SizeBytes));
        Assert.False(Directory.Exists(workDir));

        var events = await h.ItemEventsAsync(item.Id);
        Assert.Equal(
            [ItemState.Added, ItemState.Queued, ItemState.Resolving, ItemState.Downloading, ItemState.PostProcessing, ItemState.Completed],
            events.Select(e => e.ToState));
        Assert.Equal((0, "line 1\nline 2"), (events[^1].ExitCode, events[^1].OutputTail));

        var snapshot = await h.Actor.GetSnapshotAsync(Ct);
        Assert.Equal([(other.Id, 1L)], snapshot.Items.Select(i => (i.Id, i.Position)));

        var published = h.DrainEvents();
        Assert.Equal(
            [ItemState.Resolving, ItemState.Downloading, ItemState.PostProcessing, ItemState.Completed],
            published.OfType<ItemUpdatedEvent>().Select(e => e.Item.State));
        Assert.Equal([new ItemPosition(other.Id, 1)], Assert.IsType<QueueReorderedEvent>(published[^1]).Positions);
    }

    [Fact]
    public async Task Disallowed_commands_are_rejected_without_changes()
    {
        await using var h = await QueueHarness.StartAsync();
        var item = await h.AddAsync();
        h.DrainEvents();

        var rejected = await h.Actor.CompleteAsync(item.Id, @"C:\x.mp4", null, cancellationToken: Ct);
        Assert.Equal(QueueCodes.InvalidTransition, rejected.Rejection?.Code);
        Assert.Equal("Queued", rejected.Rejection?.Args["state"]);

        Assert.Equal(QueueCodes.ItemNotFound, (await h.Actor.PauseAsync(9999, Ct)).Rejection?.Code);
        Assert.Equal(QueueCodes.NotRunning, (await h.Actor.ReportProgressAsync(item.Id, new ItemProgress(), Ct)).Rejection?.Code);

        Assert.Equal(2, (await h.ItemEventsAsync(item.Id)).Count);
        Assert.Empty(h.DrainEvents());
    }

    [Fact]
    public async Task Failures_record_the_error_and_a_manual_retry_starts_over()
    {
        await using var h = await QueueHarness.StartAsync();
        var item = await h.AddAsync();
        var next = h.Root.Time.Now.AddSeconds(30);
        var error = new EngineMessage("error.transient.network", new Dictionary<string, string> { ["host"] = "youtube.com" }, "Network error.");

        await h.Actor.StartResolvingAsync(item.Id, Ct);
        var failed = await h.Actor.FailAsync(item.Id, new JobFailure(ItemTrigger.WaitForRetry, "Transient", error, next, new JobOutput(1, ["ERROR: timed out"])), Ct);
        Assert.True(failed.Succeeded);

        var snapshot = (await h.Actor.GetSnapshotAsync(Ct)).Items.Single();
        Assert.Equal((ItemState.WaitingRetry, 1, next, "Transient"), (snapshot.State, snapshot.Attempt, snapshot.NextAttemptAt, snapshot.ErrorCategory));
        Assert.Equal((error.Code, error.Message), (snapshot.Error?.Code, snapshot.Error?.Message));
        Assert.Equal(error.Args, snapshot.Error!.Args);

        var lastEvent = (await h.ItemEventsAsync(item.Id))[^1];
        Assert.Equal(("error.transient.network", "Transient", 1, "ERROR: timed out"), (lastEvent.Reason, lastEvent.ErrorCategory, lastEvent.ExitCode, lastEvent.OutputTail));

        await h.Actor.RetryDueAsync(item.Id, Ct);
        await h.Actor.StartResolvingAsync(item.Id, Ct);
        await h.Actor.FailAsync(item.Id, new JobFailure(ItemTrigger.Fail, "Unavailable", new EngineMessage("error.unavailable.private", "Private video.")), Ct);
        snapshot = (await h.Actor.GetSnapshotAsync(Ct)).Items.Single();
        Assert.Equal((ItemState.Failed, 1, (DateTimeOffset?)null), (snapshot.State, snapshot.Attempt, snapshot.NextAttemptAt));

        Assert.True((await h.Actor.RetryAsync(item.Id, Ct)).Succeeded);
        snapshot = (await h.Actor.GetSnapshotAsync(Ct)).Items.Single();
        Assert.Equal((ItemState.Queued, 0, null, null), (snapshot.State, snapshot.Attempt, snapshot.ErrorCategory, snapshot.Error));
    }

    [Fact]
    public async Task Releasing_waiting_items_requeues_only_the_matching_state()
    {
        await using var h = await QueueHarness.StartAsync();
        var auth = await h.AddAsync();
        var tool = await h.AddAsync();
        foreach (var (id, trigger) in new[] { (auth.Id, ItemTrigger.WaitForAuth), (tool.Id, ItemTrigger.WaitForToolFix) })
        {
            await h.Actor.StartResolvingAsync(id, Ct);
            await h.Actor.FailAsync(id, new JobFailure(trigger, "x", new EngineMessage("error.x", "x")), Ct);
        }

        Assert.Equal(1, await h.Actor.ReleaseWaitingAsync(ItemTrigger.AuthRestored, Ct));

        var states = (await h.Actor.GetSnapshotAsync(Ct)).Items.Select(i => i.State);
        Assert.Equal([ItemState.Queued, ItemState.WaitingToolFix], states);
    }

    [Fact]
    public async Task Move_keeps_relative_order_and_positions_contiguous()
    {
        await using var h = await QueueHarness.StartAsync();
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await h.AddAsync($"https://{i}")).Id);
        }

        async Task<long[]> Order() => [.. (await h.Actor.GetSnapshotAsync(Ct)).Items.Select(i => i.Id)];
        long[] Ids(params int[] indexes) => [.. indexes.Select(i => ids[i])];

        Assert.True((await h.Actor.MoveAsync([ids[3], ids[1]], MoveTarget.Top, cancellationToken: Ct)).Succeeded);
        Assert.Equal(Ids(1, 3, 0, 2, 4), await Order());

        await h.Actor.MoveAsync([ids[1]], MoveTarget.Bottom, cancellationToken: Ct);
        Assert.Equal(Ids(3, 0, 2, 4, 1), await Order());

        await h.Actor.MoveAsync([ids[4], ids[3]], MoveTarget.Before, ids[0], Ct);
        Assert.Equal(Ids(3, 4, 0, 2, 1), await Order());

        Assert.Equal(QueueCodes.InvalidMove, (await h.Actor.MoveAsync([ids[0]], MoveTarget.Before, ids[0], Ct)).Rejection?.Code);
        Assert.Equal(QueueCodes.ItemNotFound, (await h.Actor.MoveAsync([ids[0], 9999], MoveTarget.Top, cancellationToken: Ct)).Rejection?.Code);
        Assert.Equal(Ids(3, 4, 0, 2, 1), await Order());

        await using var db = h.Root.CreateContext();
        var stored = db.Items.OrderBy(i => i.Position).Select(i => new { i.Id, i.Position }).ToList();
        Assert.Equal(Ids(3, 4, 0, 2, 1), stored.Select(s => s.Id));
        Assert.Equal([1L, 2, 3, 4, 5], stored.Select(s => s.Position));
    }

    [Fact]
    public async Task Removing_from_history_with_files_deletes_the_file_and_keeps_the_row()
    {
        await using var h = await QueueHarness.StartAsync();
        var item = await h.AddAsync();
        var file = Path.Combine(h.Root.Paths.Root, "İndirilen ılık.mp4");
        await File.WriteAllTextAsync(file, "video", Ct);
        await h.Actor.StartResolvingAsync(item.Id, Ct);
        await h.Actor.MetadataResolvedAsync(item.Id, new ItemMetadata(), Ct);
        await h.Actor.CompleteAsync(item.Id, file, 5, cancellationToken: Ct);
        h.DrainEvents();

        Assert.True((await h.Actor.RemoveAsync(item.Id, deleteFiles: true, Ct)).Succeeded);

        Assert.False(File.Exists(file));
        var stored = await h.ItemAsync(item.Id);
        Assert.Equal((ItemState.Removed, null, h.Root.Time.Now), (stored.State, stored.FilePath, stored.RemovedAt));
        Assert.Equal(item.Id, Assert.IsType<ItemRemovedEvent>(Assert.Single(h.DrainEvents())).ItemId);
        Assert.Equal(QueueCodes.InvalidTransition, (await h.Actor.RemoveAsync(item.Id, cancellationToken: Ct)).Rejection?.Code);
    }

    [Fact]
    public async Task Removing_without_files_leaves_the_file_untracked()
    {
        await using var h = await QueueHarness.StartAsync();
        var item = await h.AddAsync();
        var file = Path.Combine(h.Root.Paths.Root, "kept.mp4");
        await File.WriteAllTextAsync(file, "video", Ct);
        await h.Actor.StartResolvingAsync(item.Id, Ct);
        await h.Actor.MetadataResolvedAsync(item.Id, new ItemMetadata(), Ct);
        await h.Actor.CompleteAsync(item.Id, file, 5, cancellationToken: Ct);

        await h.Actor.RemoveAsync(item.Id, cancellationToken: Ct);

        Assert.True(File.Exists(file));
        Assert.Null((await h.ItemAsync(item.Id)).FilePath);
    }

    [Fact]
    public async Task Pausing_the_queue_interrupts_running_items_and_blocks_starts()
    {
        await using var h = await QueueHarness.StartAsync();
        var running = await h.AddAsync();
        var queued = await h.AddAsync();
        await h.Actor.StartResolvingAsync(running.Id, Ct);
        h.DrainEvents();

        await h.Actor.PauseAllAsync(Ct);

        Assert.True(h.Actor.IsPaused);
        var published = h.DrainEvents();
        var updated = Assert.IsType<ItemUpdatedEvent>(published[0]);
        Assert.Equal((ItemState.Queued, ItemState.Resolving, ItemEventReasons.QueuePaused), (updated.Item.State, updated.FromState, updated.Reason));
        Assert.True(Assert.IsType<QueuePausedChangedEvent>(published[1]).Paused);

        Assert.Equal(QueueCodes.QueuePaused, (await h.Actor.StartResolvingAsync(queued.Id, Ct)).Rejection?.Code);

        await h.Actor.ResumeAllAsync(Ct);
        Assert.False(h.Actor.IsPaused);
        Assert.True((await h.Actor.StartResolvingAsync(queued.Id, Ct)).Succeeded);
    }

    [Fact]
    public async Task Progress_is_published_and_the_size_is_checkpointed_at_most_every_10_seconds()
    {
        await using var h = await QueueHarness.StartAsync();
        var item = await h.AddAsync();
        await h.Actor.StartResolvingAsync(item.Id, Ct);
        await h.Actor.MetadataResolvedAsync(item.Id, new ItemMetadata(), Ct);
        h.DrainEvents();

        await h.Actor.ReportProgressAsync(item.Id, new ItemProgress("video", 10, 1000, Fraction: 0.01), Ct);
        Assert.Equal(1000, (await h.ItemAsync(item.Id)).SizeBytes);

        h.Root.Time.Now += TimeSpan.FromSeconds(9);
        await h.Actor.ReportProgressAsync(item.Id, new ItemProgress("video", 20, 2000, Fraction: 0.01), Ct);
        Assert.Equal(1000, (await h.ItemAsync(item.Id)).SizeBytes);

        h.Root.Time.Now += TimeSpan.FromSeconds(1);
        await h.Actor.ReportProgressAsync(item.Id, new ItemProgress("video", 30, 3000, Fraction: 0.01), Ct);
        Assert.Equal(3000, (await h.ItemAsync(item.Id)).SizeBytes);

        var snapshot = await h.Actor.GetSnapshotAsync(Ct);
        Assert.Equal(30, snapshot.Progress[item.Id].DownloadedBytes);

        var progress = h.DrainEvents().Cast<ItemProgressEvent>().Select(e => e.Progress.DownloadedBytes);
        Assert.Equal([10L, 20, 30], progress);

        await h.Actor.PauseAsync(item.Id, Ct);
        Assert.Empty((await h.Actor.GetSnapshotAsync(Ct)).Progress);
    }
}
