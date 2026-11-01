using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Errors;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Core.Tests.Persistence;

namespace YoutubeDownloadHelper.Core.Tests.Scheduling;

/// <summary>The scheduler end to end against <c>YoutubeDownloadHelper.FakeYtDlp</c> (PLAN.md §4.3, §5.1, §5.2, §5.4).</summary>
public sealed class SchedulerTests
{
    private const string Title = "İzmir ılık 😀";
    private const string TitleQuery = "title=%C4%B0zmir%20%C4%B1l%C4%B1k%20%F0%9F%98%80";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_video_goes_from_queued_to_completed_with_its_file_metadata_and_archive_entry()
    {
        await using var h = await SchedulerHarness.StartAsync();

        var id = await h.AddAsync($"https://fake.test/success?id=aaaaaaaaaaa&{TitleQuery}&ppdelay=0.3");
        var item = await h.WaitForAsync(id, ItemState.Completed);

        var expected = Path.Combine(h.Root.Defaults.DownloadFolder, "Fake Channel", $"2026-01-02 - {Title}.mp4");
        Assert.Equal(expected, item.FilePath);
        Assert.True(File.Exists(expected));
        Assert.Equal(new FileInfo(expected).Length, item.SizeBytes);
        Assert.Equal(Title, item.Title);
        Assert.Equal("aaaaaaaaaaa", item.VideoId);
        Assert.Equal("Fake Channel", item.Channel);
        Assert.Equal(new DateOnly(2026, 1, 2), item.UploadDate);
        Assert.Null(item.ErrorCode);

        var states = (await h.ItemEventsAsync(id)).Select(e => e.ToState);
        Assert.Equal(
            [ItemState.Added, ItemState.Queued, ItemState.Resolving, ItemState.Downloading, ItemState.PostProcessing, ItemState.Completed],
            states);

        await using var db = h.Root.CreateContext();
        Assert.Contains("aaaaaaaaaaa", await db.GetDownloadedVideoIdsAsync(item.Extractor!, ["aaaaaaaaaaa"], Ct));
        Assert.False(Directory.Exists(h.Root.Paths.WorkDirFor(id)));
        Assert.True(File.Exists(h.Root.Paths.JobLogFor(id, 0)));
    }

    [Fact]
    public async Task No_more_jobs_run_than_the_concurrency_setting_allows()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var first = await h.AddAsync("https://fake.test/hang?id=hang0000001");
        var second = await h.AddAsync("https://fake.test/hang?id=hang0000002");
        var third = await h.AddAsync("https://fake.test/hang?id=hang0000003");

        await h.WaitForAsync(first, ItemState.Downloading);
        await h.WaitForAsync(second, ItemState.Downloading);
        await Task.Delay(500, Ct);
        Assert.Equal(ItemState.Queued, (await h.ItemAsync(third)).State);
        Assert.Equal(2, h.Scheduler.RunningItemIds.Count);

        // A freed slot goes to the next queued item.
        Assert.True((await h.Actor.PauseAsync(first, Ct)).Succeeded);
        await h.WaitForAsync(third, ItemState.Downloading);
        Assert.Equal(ItemState.Downloading, (await h.ItemAsync(second)).State);
        Assert.Equal([second, third], h.Scheduler.RunningItemIds.Order());
    }

    [Fact]
    public async Task A_video_already_downloaded_is_skipped_unless_forced()
    {
        await using var h = await SchedulerHarness.StartAsync();
        const string url = $"https://fake.test/success?id=dupdupdupdu&{TitleQuery}";
        var original = await h.WaitForAsync(await h.AddAsync(url), ItemState.Completed);

        var duplicate = await h.WaitForAsync(await h.AddAsync(url), ItemState.Skipped);
        Assert.Equal(Title, duplicate.Title);
        Assert.Null(duplicate.FilePath);
        Assert.Equal(ItemEventReasons.InArchive, (await h.ItemEventsAsync(duplicate.Id))[^1].Reason);

        // Forced, it downloads again, and the name collision adds the video id.
        var forced = await h.WaitForAsync(await h.AddAsync(url, force: true), ItemState.Completed);
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(original.FilePath)!, $"2026-01-02 - {Title} [dupdupdupdu].mp4"),
            forced.FilePath);
        Assert.True(File.Exists(original.FilePath));
        Assert.True(File.Exists(forced.FilePath));
    }

    [Fact]
    public async Task Pause_kills_the_job_and_keeps_the_partial_file_and_resume_continues_from_it()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var id = await h.AddAsync("https://fake.test/resume?id=resume00001&size=1M");

        await h.WaitForAsync(id, ItemState.Downloading);
        var part = await WaitForPartialAsync(h, id);

        Assert.True((await h.Actor.PauseAsync(id, Ct)).Succeeded);
        await h.WaitIdleAsync();
        Assert.Equal(ItemState.Paused, (await h.ItemAsync(id)).State);
        Assert.True(File.Exists(part.Path));
        Assert.True(new FileInfo(part.Path).Length >= part.Length);

        // The fake hangs on a fresh start, so completing proves the second run resumed.
        Assert.True((await h.Actor.ResumeAsync(id, Ct)).Succeeded);
        var item = await h.WaitForAsync(id, ItemState.Completed);
        Assert.True(File.Exists(item.FilePath));
        Assert.Equal(1L << 20, item.SizeBytes);
    }

    [Fact]
    public async Task An_engine_killed_mid_download_resumes_the_item_after_a_restart()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        long id;
        await using (var first = await SchedulerHarness.StartAsync(root))
        {
            id = await first.AddAsync("https://fake.test/resume?id=resume00002&size=1M");
            await first.WaitForAsync(id, ItemState.Downloading);
            await WaitForPartialAsync(first, id);
            await first.CrashAsync();
        }

        Assert.Equal(ItemState.Downloading, (await ReadItemAsync(root, id)).State);

        await using var second = await SchedulerHarness.StartAsync(root);
        var item = await second.WaitForAsync(id, ItemState.Completed);
        Assert.True(File.Exists(item.FilePath));
        Assert.Contains(await second.ItemEventsAsync(id), e => e.Reason == ItemEventReasons.Interrupted && e.ToState == ItemState.Queued);
    }

    [Fact]
    public async Task Http_429_backs_off_30s_2m_10m_30m_and_then_fails()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var id = await h.AddAsync("https://fake.test/http-429?id=ratelimit01");

        for (var retry = 0; retry < RetryPolicy.Backoff.Count; retry++)
        {
            var expectedAttempt = retry + 1;
            var waiting = await h.WaitForAsync(id, ItemState.WaitingRetry, i => i.Attempt == expectedAttempt);
            Assert.Equal(ErrorCodes.RateLimited, waiting.ErrorCode);
            var delay = waiting.NextAttemptAt!.Value - h.Time.GetUtcNow();
            var backoff = RetryPolicy.Backoff[retry];
            Assert.InRange(delay, backoff * 0.79, backoff * 1.21);

            // Just before the timer nothing happens; at it the item runs again.
            await h.AdvanceAsync(delay - TimeSpan.FromSeconds(1));
            await h.WaitIdleAsync();
            Assert.Equal(ItemState.WaitingRetry, (await h.ItemAsync(id)).State);
            await h.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        var failed = await h.WaitForAsync(id, ItemState.Failed);
        Assert.Equal(ErrorCodes.RateLimited, failed.ErrorCode);
        Assert.Equal(nameof(ErrorCategory.Transient), failed.ErrorCategory);
        Assert.Equal(RetryPolicy.Backoff.Count, failed.Attempt);
        Assert.Equal(5, (await h.ItemEventsAsync(id)).Count(e => e.ToState == ItemState.Resolving));
    }

    [Fact]
    public async Task A_private_video_fails_at_once()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var id = await h.AddAsync("https://fake.test/private?id=private0001");

        var item = await h.WaitForAsync(id, ItemState.Failed);
        Assert.Equal(ErrorCodes.Private, item.ErrorCode);
        Assert.Equal(nameof(ErrorCategory.Unavailable), item.ErrorCategory);
        Assert.Equal(0, item.Attempt);
        Assert.Null(item.NextAttemptAt);
        var events = await h.ItemEventsAsync(id);
        Assert.Equal([ItemState.Added, ItemState.Queued, ItemState.Resolving, ItemState.Failed], events.Select(e => e.ToState));
        Assert.Equal(1, events[^1].ExitCode);
        Assert.Contains("Private video", events[^1].OutputTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_disk_requeues_the_item_and_blocks_the_queue_until_there_is_room()
    {
        await using var h = await SchedulerHarness.StartAsync(configure: s => s.Disk.Free = 10L << 30);
        var full = await h.AddAsync("https://fake.test/disk-full?id=diskfull001");

        await SchedulerHarness.WaitUntilAsync(
            () => Task.FromResult(h.Scheduler.GetBlocks().Contains(QueueBlock.DiskFull)), () => "the DiskFull block");
        var item = await h.WaitForAsync(full, ItemState.Queued, i => i.Attempt == 0);
        Assert.Null(item.ErrorCode);
        var interrupted = (await h.ItemEventsAsync(full))[^1];
        Assert.Equal(ErrorCodes.DiskFull, interrupted.Reason);
        Assert.Equal(nameof(ErrorCategory.LocalIo), interrupted.ErrorCategory);

        // Blocked: nothing starts, and the same free space does not lift it.
        Assert.True((await h.Actor.PauseAsync(full, Ct)).Succeeded);
        var next = await h.AddAsync("https://fake.test/success?id=afterfull01");
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        await h.WaitIdleAsync();
        Assert.Equal(ItemState.Queued, (await h.ItemAsync(next)).State);
        Assert.Equal([QueueBlock.DiskFull], h.Scheduler.GetBlocks());

        h.Disk.Free = 100L << 30;
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        await h.WaitForAsync(next, ItemState.Completed);
        Assert.Empty(h.Scheduler.GetBlocks());

        var blockEvents = h.DrainEvents().OfType<QueueBlockChangedEvent>().ToList();
        Assert.Equal([(QueueBlock.DiskFull, true), (QueueBlock.DiskFull, false)], blockEvents.Select(e => (e.Block, e.Active)));
        Assert.Equal(SchedulerCodes.DiskFull, blockEvents[0].Reason?.Code);
    }

    [Fact]
    public async Task Too_little_free_space_for_the_margin_blocks_before_a_job_starts()
    {
        await using var h = await SchedulerHarness.StartAsync(configure: s => s.Disk.Free = 10L << 20);
        var id = await h.AddAsync("https://fake.test/success?id=nospace0001");

        await h.WaitIdleAsync();
        Assert.Equal([QueueBlock.DiskFull], h.Scheduler.GetBlocks());
        Assert.Equal([ItemState.Added, ItemState.Queued], (await h.ItemEventsAsync(id)).Select(e => e.ToState));

        h.Disk.Free = 10L << 30;
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        await h.WaitForAsync(id, ItemState.Completed);
    }

    [Fact]
    public async Task Repeated_network_failures_wait_for_the_network_and_a_probe_lifts_the_block()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var ids = new List<long>();
        for (var i = 1; i <= 3; i++)
        {
            ids.Add(await h.AddAsync($"https://fake.test/connection-reset?id=netfail000{i}"));
        }

        await SchedulerHarness.WaitUntilAsync(
            () => Task.FromResult(h.Scheduler.GetBlocks().Contains(QueueBlock.WaitingForNetwork)), () => "the network block");
        await h.WaitIdleAsync();
        var items = await Task.WhenAll(ids.Select(h.ItemAsync));

        // Two failures back off as usual; the one that raises the block waits without using a retry.
        Assert.Equal(2, items.Count(i => i.State == ItemState.WaitingRetry && i.ErrorCode == ErrorCodes.Network));
        var requeued = Assert.Single(items, i => i.State == ItemState.Queued);
        Assert.Equal(0, requeued.Attempt);

        await h.AdvanceAsync(TimeSpan.FromSeconds(16));
        await h.WaitIdleAsync();
        Assert.Equal(1, h.Network.Probes);
        Assert.Contains(QueueBlock.WaitingForNetwork, h.Scheduler.GetBlocks());

        // Paused, so the requeued items don't fail again once the block is lifted.
        await h.Actor.PauseAllAsync(Ct);
        h.Network.Online = true;
        await h.AdvanceAsync(TimeSpan.FromSeconds(16));
        await h.WaitIdleAsync();
        Assert.Equal(2, h.Network.Probes);
        Assert.Equal([QueueBlock.PausedByUser], h.Scheduler.GetBlocks());
        Assert.Equal(
            [(QueueBlock.WaitingForNetwork, true), (QueueBlock.WaitingForNetwork, false)],
            h.DrainEvents().OfType<QueueBlockChangedEvent>().Select(e => (e.Block, e.Active)));
    }

    [Fact]
    public async Task A_broken_extractor_waits_for_a_tool_fix_asks_for_an_update_once_and_fails_after_24_hours()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var first = await h.AddAsync("https://fake.test/extractor-failure?id=extract0001");
        var second = await h.AddAsync("https://fake.test/extractor-failure?id=extract0002");

        var waiting = await h.WaitForAsync(first, ItemState.WaitingToolFix);
        await h.WaitForAsync(second, ItemState.WaitingToolFix);
        Assert.Equal(ErrorCodes.Extractor, waiting.ErrorCode);
        Assert.Equal(h.Time.GetUtcNow() + RetryPolicy.ToolFixTimeout, waiting.NextAttemptAt);
        Assert.Equal(1, h.ToolUpdates.Count);

        await h.AdvanceAsync(RetryPolicy.ToolFixTimeout - TimeSpan.FromMinutes(1));
        await h.WaitIdleAsync();
        Assert.Equal(ItemState.WaitingToolFix, (await h.ItemAsync(first)).State);

        await h.AdvanceAsync(TimeSpan.FromMinutes(2));
        var failed = await h.WaitForAsync(first, ItemState.Failed);
        await h.WaitForAsync(second, ItemState.Failed);
        Assert.Equal(ErrorCodes.Extractor, failed.ErrorCode);
        Assert.Null(failed.NextAttemptAt);
    }

    [Fact]
    public async Task An_auth_failure_waits_for_auth_and_asks_for_a_verification()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var id = await h.AddAsync("https://fake.test/members-only?id=members0001");

        var item = await h.WaitForAsync(id, ItemState.WaitingAuth);
        Assert.Equal(ErrorCodes.MembersOnly, item.ErrorCode);
        await h.WaitIdleAsync();
        Assert.Equal(1, h.AuthVerification.Count);

        // Released, it runs again; still not logged in, it waits again.
        Assert.Equal(1, await h.Actor.ReleaseWaitingAsync(ItemTrigger.AuthRestored, Ct));
        await SchedulerHarness.WaitUntilAsync(
            async () => (await h.ItemEventsAsync(id)).Count(e => e.ToState == ItemState.WaitingAuth) == 2,
            () => "a second WaitingAuth");
        await h.WaitIdleAsync();
        Assert.Equal(2, h.AuthVerification.Count);
    }

    [Fact]
    public async Task Consecutive_YouTube_starts_are_spaced_by_the_start_delay()
    {
        var options = new SchedulerOptions { MinYouTubeStartDelay = TimeSpan.FromSeconds(10), MaxYouTubeStartDelay = TimeSpan.FromSeconds(10) };
        await using var h = await SchedulerHarness.StartAsync(options: options);
        var first = await h.AddAsync("https://www.youtube.com/watch?v=yt000000001");
        var second = await h.AddAsync("https://www.youtube.com/watch?v=yt000000002");

        await h.WaitForAsync(first, ItemState.Completed);
        await h.WaitIdleAsync();
        Assert.Equal(ItemState.Queued, (await h.ItemAsync(second)).State);

        await h.AdvanceAsync(TimeSpan.FromSeconds(9));
        await h.WaitIdleAsync();
        Assert.Equal(ItemState.Queued, (await h.ItemAsync(second)).State);

        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        await h.WaitForAsync(second, ItemState.Completed);
    }

    [Fact]
    public async Task Missing_tools_block_the_queue_until_they_are_ready()
    {
        await using var h = await SchedulerHarness.StartAsync(configure: s => s.ToolHealth.Ready = false);
        var id = await h.AddAsync("https://fake.test/success?id=toolsmiss01");

        await h.WaitIdleAsync();
        Assert.Equal([QueueBlock.ToolsMissing], h.Scheduler.GetBlocks());
        Assert.Equal(ItemState.Queued, (await h.ItemAsync(id)).State);

        h.ToolHealth.Ready = true;
        h.Scheduler.Wake();
        await h.WaitForAsync(id, ItemState.Completed);
        Assert.Empty(h.Scheduler.GetBlocks());
    }

    [Fact]
    public async Task A_graceful_stop_requeues_running_items_and_keeps_their_partial_files()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var id = await h.AddAsync("https://fake.test/hang?id=stopping001");
        await h.WaitForAsync(id, ItemState.Downloading);
        var part = await WaitForPartialAsync(h, id);

        await h.Scheduler.StopAsync(Ct);

        var item = await h.ItemAsync(id);
        Assert.Equal(ItemState.Queued, item.State);
        Assert.Equal(ItemEventReasons.EngineStopped, (await h.ItemEventsAsync(id))[^1].Reason);
        Assert.True(File.Exists(part.Path));
        Assert.Empty(h.Scheduler.RunningItemIds);
    }

    [Fact]
    public async Task Removing_a_running_item_stops_its_job_and_deletes_its_work_folder()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var id = await h.AddAsync("https://fake.test/hang?id=removing001");
        await h.WaitForAsync(id, ItemState.Downloading);
        await WaitForPartialAsync(h, id);

        Assert.True((await h.Actor.RemoveAsync(id, cancellationToken: Ct)).Succeeded);
        await h.WaitIdleAsync();
        Assert.Equal(ItemState.Removed, (await h.ItemAsync(id)).State);
        Assert.False(Directory.Exists(h.Root.Paths.WorkDirFor(id)));
    }

    private static async Task<(string Path, long Length)> WaitForPartialAsync(SchedulerHarness h, long itemId)
    {
        var dir = h.Root.Paths.WorkDirFor(itemId);
        (string, long) found = default;
        await SchedulerHarness.WaitUntilAsync(
            () =>
            {
                var part = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*.part", SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (part is not null && new FileInfo(part).Length > 0)
                {
                    found = (part, new FileInfo(part).Length);
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            },
            () => $"a partial file in {dir}");
        return found;
    }

    private static async Task<ItemEntity> ReadItemAsync(TestDataRoot root, long id)
    {
        await using var db = root.CreateContext();
        return await db.Items.FindAsync([id], Ct) ?? throw new InvalidOperationException($"No item {id}.");
    }
}
