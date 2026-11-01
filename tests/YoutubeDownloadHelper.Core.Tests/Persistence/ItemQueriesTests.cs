using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Tests.Persistence;

public sealed class ItemQueriesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Active_items_exclude_history_and_come_in_queue_order()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        await SeedAsync(
            root,
            Item("c", ItemState.Downloading, position: 3),
            Item("a", ItemState.Queued, position: 1),
            Item("done", ItemState.Completed),
            Item("b", ItemState.Failed, position: 2),
            Item("skipped", ItemState.Skipped),
            Item("removed", ItemState.Removed, position: 4),
            Item("seen", ItemState.Seen));

        await using var db = root.CreateContext();
        Assert.Equal(["a", "b", "c"], (await db.LoadActiveAsync(Ct)).Select(i => i.Title));
    }

    [Fact]
    public async Task History_pages_newest_first_and_breaks_ties_by_id()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var items = Enumerable.Range(0, 7)
            .Select(i => Item($"h{i}", ItemState.Completed, updatedAt: T0.AddMinutes(i / 2)))
            .ToArray();
        await SeedAsync(root, [.. items, Item("active", ItemState.Queued), Item("removed", ItemState.Removed), Item("seen", ItemState.Seen)]);

        var seen = new List<long>();
        HistoryCursor? cursor = null;
        var pages = 0;
        do
        {
            await using var db = root.CreateContext();
            var page = await db.GetHistoryPageAsync(after: cursor, pageSize: 3, cancellationToken: Ct);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.Next;
            pages++;
        }
        while (cursor is not null);

        var expected = items
            .OrderByDescending(i => i.UpdatedAt)
            .ThenByDescending(i => i.Id)
            .Select(i => i.Id);
        Assert.Equal(expected, seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Timestamps_are_stamped_on_insert_and_update()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var created = root.Time.Now;
        var item = Item("stamped", ItemState.Queued);
        item.CreatedAt = default;
        item.UpdatedAt = default;
        await SeedAsync(root, item);
        Assert.Equal(created, item.CreatedAt);
        Assert.Equal(created, item.UpdatedAt);

        root.Time.Now = created.AddMinutes(5);
        await using (var db = root.CreateContext())
        {
            var tracked = await db.Items.SingleAsync(Ct);
            tracked.State = ItemState.Completed;
            await db.SaveChangesAsync(Ct);
        }

        await using var check = root.CreateContext();
        var loaded = await check.Items.SingleAsync(Ct);
        Assert.Equal(created, loaded.CreatedAt);
        Assert.Equal(root.Time.Now, loaded.UpdatedAt);
    }

    [Theory]
    [InlineData("şarkı", "Türkçe şarkı")]
    [InlineData("KANAL", "kanal adı")]
    [InlineData("50%", "50% off")]
    [InlineData("a_b", "a_b")]
    [InlineData("watch?v=xyz", "by url")]
    public async Task History_search_matches_title_channel_or_url(string search, string expectedTitle)
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var channelItem = Item("kanal adı", ItemState.Completed);
        channelItem.Channel = "Kanal İsmi";
        var urlItem = Item("by url", ItemState.Completed);
        urlItem.Url = "https://www.youtube.com/watch?v=xyz";
        await SeedAsync(
            root,
            Item("Türkçe şarkı", ItemState.Completed),
            channelItem,
            urlItem,
            Item("50% off", ItemState.Completed),
            Item("5000 off", ItemState.Completed),
            Item("a_b", ItemState.Completed),
            Item("axb", ItemState.Completed));

        await using var db = root.CreateContext();
        var page = await db.GetHistoryPageAsync(search, cancellationToken: Ct);

        Assert.Equal(expectedTitle, Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task Item_columns_round_trip()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var watcher = new WatcherEntity { Url = "https://www.youtube.com/@kanal", Kind = "Channel", PresetId = "best" };
        var item = Item("Başlık İı", ItemState.WaitingRetry, position: 1);
        item.Watcher = watcher;
        item.UploadDate = new DateOnly(2026, 2, 28);
        item.NextAttemptAt = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.FromHours(3));
        item.ErrorCategory = "Transient";
        item.ErrorCode = "error.transient.rateLimited";
        item.ErrorArgsJson = """{"retryIn":30}""";
        var runEnded = new ItemEventEntity { FromState = ItemState.Downloading, ToState = ItemState.WaitingRetry, ExitCode = 1 };
        runEnded.SetOutputTail(["ERROR: HTTP Error 429: Too Many Requests", "İndirme başarısız"]);
        item.Events.Add(runEnded);
        await using (var db = root.CreateContext())
        {
            db.Watchers.Add(watcher);
            db.Items.Add(item);
            await db.SaveChangesAsync(Ct);
        }

        await using var check = root.CreateContext();
        var loaded = await check.Items.Include(i => i.Events).SingleAsync(Ct);
        Assert.Equal(item.Title, loaded.Title);
        Assert.Equal(item.UploadDate, loaded.UploadDate);
        Assert.Equal(item.NextAttemptAt, loaded.NextAttemptAt);
        Assert.Equal(TimeSpan.Zero, loaded.NextAttemptAt!.Value.Offset);
        Assert.Equal(watcher.Id, loaded.WatcherId);
        Assert.Equal(Accounts.Default, loaded.AccountId);
        var loadedEvent = Assert.Single(loaded.Events);
        Assert.Equal(ItemState.Downloading, loadedEvent.FromState);
        Assert.Equal(1, loadedEvent.ExitCode);
        Assert.Equal(root.Time.Now, loadedEvent.CreatedAt);
        Assert.Equal(runEnded.OutputTail, loadedEvent.OutputTail);

        // Enums are stored by name, so reordering ItemState never changes stored data.
        Assert.Equal(
            "WaitingRetry",
            await check.Database.SqlQueryRaw<string>("SELECT state AS Value FROM items").SingleAsync(Ct));
    }

    [Fact]
    public async Task A_video_counts_as_downloaded_while_an_item_for_it_is_completed()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        await SeedAsync(
            root,
            Video("completed", ItemState.Completed),
            Video("removed", ItemState.Removed),
            Video("queued", ItemState.Queued),
            Video("failed", ItemState.Failed),
            Video("skipped", ItemState.Skipped),
            Video("seen", ItemState.Seen),
            Video("ABCdef", ItemState.Completed),
            Video("otherExtractor", ItemState.Completed, extractor: "vimeo"));

        await using var db = root.CreateContext();
        var downloaded = await db.GetDownloadedVideoIdsAsync(
            "youtube",
            ["completed", "removed", "queued", "failed", "skipped", "seen", "abcdef", "ABCdef", "otherExtractor",
             "neverSeen", "completed"],
            Ct);

        Assert.Equal(
            ["ABCdef", "completed"],
            downloaded.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_video_is_known_while_any_item_for_it_exists()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        await SeedAsync(
            root,
            Video("completed", ItemState.Completed),
            Video("removed", ItemState.Removed),
            Video("queued", ItemState.Queued),
            Video("seen", ItemState.Seen),
            Video("otherExtractor", ItemState.Seen, extractor: "vimeo"));

        await using var db = root.CreateContext();
        var known = await db.GetKnownVideoIdsAsync(
            "youtube",
            ["completed", "removed", "queued", "seen", "SEEN", "otherExtractor", "neverSeen"],
            Ct);

        Assert.Equal(["completed", "queued", "removed", "seen"], known.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Deleting_a_watcher_keeps_its_items_including_seen_ones()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var watcher = new WatcherEntity { Url = "u", Kind = "Playlist", PresetId = "best" };
        var queued = Item("from watcher", ItemState.Queued);
        var seen = Video("v1", ItemState.Seen);
        queued.Watcher = watcher;
        seen.Watcher = watcher;
        await SeedAsync(root, queued, seen);

        await using (var db = root.CreateContext())
        {
            await db.Watchers.ExecuteDeleteAsync(Ct);
        }

        await using var check = root.CreateContext();
        var items = await check.Items.OrderBy(i => i.Id).ToListAsync(Ct);
        Assert.Equal([ItemState.Queued, ItemState.Seen], items.Select(i => i.State));
        Assert.All(items, i => Assert.Null(i.WatcherId));
    }

    [Fact]
    public void Output_tail_keeps_the_last_200_lines()
    {
        var runEnded = new ItemEventEntity();

        runEnded.SetOutputTail(Enumerable.Range(1, 250).Select(i => $"line {i}"));

        var lines = runEnded.OutputTail!.Split('\n');
        Assert.Equal(ItemEventEntity.MaxOutputLines, lines.Length);
        Assert.Equal("line 51", lines[0]);
        Assert.Equal("line 250", lines[^1]);
    }

    private static ItemEntity Item(string title, ItemState state, long position = 0, DateTimeOffset? updatedAt = null) =>
        new()
        {
            Url = $"https://www.youtube.com/watch?v={Guid.NewGuid():N}",
            Title = title,
            PresetId = "best",
            State = state,
            Position = position,
            CreatedAt = T0,
            UpdatedAt = updatedAt ?? T0,
        };

    private static ItemEntity Video(string videoId, ItemState state, string extractor = "youtube")
    {
        var item = Item(videoId, state);
        item.Extractor = extractor;
        item.VideoId = videoId;
        return item;
    }

    private static async Task SeedAsync(TestDataRoot root, params ItemEntity[] items)
    {
        await using var db = root.CreateContext();
        db.Items.AddRange(items);
        await db.SaveChangesAsync(Ct);
    }
}
