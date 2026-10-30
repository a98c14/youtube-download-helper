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
            Item("done", ItemState.Completed, finishedAt: T0),
            Item("b", ItemState.Failed, position: 2),
            Item("skipped", ItemState.Skipped, finishedAt: T0),
            Removed(Item("removed", ItemState.Queued, position: 4)));

        await using var db = root.CreateContext();
        Assert.Equal(["a", "b", "c"], (await db.LoadActiveAsync(Ct)).Select(i => i.Title));
    }

    [Fact]
    public async Task History_pages_newest_first_and_breaks_ties_by_id()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var items = Enumerable.Range(0, 7)
            .Select(i => Item($"h{i}", ItemState.Completed, finishedAt: T0.AddMinutes(i / 2)))
            .ToArray();
        await SeedAsync(root, [.. items, Item("active", ItemState.Queued), Removed(Item("removed", ItemState.Completed, finishedAt: T0))]);

        var seen = new List<Guid>();
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
            .OrderByDescending(i => i.FinishedAt)
            .ThenByDescending(i => i.Id.ToString("D"), StringComparer.OrdinalIgnoreCase)
            .Select(i => i.Id);
        Assert.Equal(expected, seen);
        Assert.Equal(3, pages);
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
        var channelItem = Item("kanal adı", ItemState.Completed, finishedAt: T0);
        channelItem.Channel = "Kanal İsmi";
        var urlItem = Item("by url", ItemState.Completed, finishedAt: T0);
        urlItem.Url = "https://www.youtube.com/watch?v=xyz";
        await SeedAsync(
            root,
            Item("Türkçe şarkı", ItemState.Completed, finishedAt: T0),
            channelItem,
            urlItem,
            Item("50% off", ItemState.Completed, finishedAt: T0),
            Item("5000 off", ItemState.Completed, finishedAt: T0),
            Item("a_b", ItemState.Completed, finishedAt: T0),
            Item("axb", ItemState.Completed, finishedAt: T0));

        await using var db = root.CreateContext();
        var page = await db.GetHistoryPageAsync(search, cancellationToken: Ct);

        Assert.Equal(expectedTitle, Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task Item_columns_round_trip()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var watcher = new WatcherEntity { Id = Guid.CreateVersion7(), Url = "https://www.youtube.com/@kanal", Kind = "Channel", PresetId = "best" };
        var item = Item("Başlık İı", ItemState.WaitingRetry, position: 1);
        item.WatcherId = watcher.Id;
        item.UploadDate = new DateOnly(2026, 2, 28);
        item.NextAttemptAt = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.FromHours(3));
        item.ErrorCategory = "Transient";
        item.ErrorCode = "error.transient.rateLimited";
        item.ErrorArgsJson = """{"retryIn":30}""";
        var runEnded = new ItemEventEntity { At = T0, FromState = ItemState.Downloading, ToState = ItemState.WaitingRetry, ExitCode = 1 };
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
        Assert.Equal(runEnded.OutputTail, loadedEvent.OutputTail);

        // Enums are stored by name, so reordering ItemState never changes stored data.
        Assert.Equal(
            "WaitingRetry",
            await check.Database.SqlQueryRaw<string>("SELECT state AS Value FROM items").SingleAsync(Ct));
    }

    [Fact]
    public async Task A_video_counts_as_downloaded_once_an_item_completed_it()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        await SeedAsync(
            root,
            Video("completed", ItemState.Completed, downloaded: true),
            Removed(Video("removedAfterCompleting", ItemState.Completed, downloaded: true)),
            Video("requeuedAfterCompleting", ItemState.Queued, downloaded: true),
            Video("failed", ItemState.Failed),
            Removed(Video("removedBeforeCompleting", ItemState.Failed)),
            Video("skipped", ItemState.Skipped),
            Video("ABCdef", ItemState.Completed, downloaded: true),
            Video("otherExtractor", ItemState.Completed, downloaded: true, extractor: "vimeo"));

        await using var db = root.CreateContext();
        var downloaded = await db.GetDownloadedVideoIdsAsync(
            "youtube",
            ["completed", "removedAfterCompleting", "requeuedAfterCompleting", "failed", "removedBeforeCompleting",
             "skipped", "abcdef", "ABCdef", "otherExtractor", "neverSeen", "completed"],
            Ct);

        Assert.Equal(
            ["ABCdef", "completed", "removedAfterCompleting", "requeuedAfterCompleting"],
            downloaded.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Deleting_a_watcher_keeps_its_items_and_drops_its_seen_list()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var watcher = new WatcherEntity { Id = Guid.CreateVersion7(), Url = "u", Kind = "Playlist", PresetId = "best" };
        watcher.Seen.Add(new WatcherSeenEntity { Extractor = "youtube", VideoId = "v1", SeenAt = T0 });
        var item = Item("from watcher", ItemState.Queued);
        item.WatcherId = watcher.Id;
        await using (var db = root.CreateContext())
        {
            db.Watchers.Add(watcher);
            db.Items.Add(item);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = root.CreateContext())
        {
            await db.Watchers.ExecuteDeleteAsync(Ct);
        }

        await using var check = root.CreateContext();
        Assert.Null((await check.Items.SingleAsync(Ct)).WatcherId);
        Assert.Empty(await check.WatcherSeen.ToListAsync(Ct));
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

    private static ItemEntity Item(string title, ItemState state, long position = 0, DateTimeOffset? finishedAt = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Url = $"https://www.youtube.com/watch?v={Guid.NewGuid():N}",
            Title = title,
            PresetId = "best",
            State = state,
            Position = position,
            CreatedAt = T0,
            UpdatedAt = T0,
            FinishedAt = finishedAt,
        };

    /// <summary>An item for <paramref name="videoId"/>, with a file when it was <paramref name="downloaded"/>.</summary>
    private static ItemEntity Video(string videoId, ItemState state, bool downloaded = false, string extractor = "youtube")
    {
        var item = Item(videoId, state, finishedAt: ItemQueries.HistoryStates.Contains(state) ? T0 : null);
        item.Extractor = extractor;
        item.VideoId = videoId;
        item.FilePath = downloaded ? $@"C:\Downloads\{videoId}.mkv" : null;
        return item;
    }

    private static ItemEntity Removed(ItemEntity item)
    {
        item.RemovedAt = T0;
        return item;
    }

    private static async Task SeedAsync(TestDataRoot root, params ItemEntity[] items)
    {
        await using var db = root.CreateContext();
        db.Items.AddRange(items);
        await db.SaveChangesAsync(Ct);
    }
}
