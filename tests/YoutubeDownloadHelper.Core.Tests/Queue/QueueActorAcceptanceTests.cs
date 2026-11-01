using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tests.Persistence;

namespace YoutubeDownloadHelper.Core.Tests.Queue;

/// <summary>The acceptance criteria of #38.</summary>
public sealed class QueueActorAcceptanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_commands_from_many_threads_leave_a_consistent_queue()
    {
        await using var h = await QueueHarness.StartAsync();

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);
            var mine = new List<long>();
            for (var i = 0; i < 30; i++)
            {
                mine.Add((await h.AddAsync($"https://w{worker}/{i}", paused: random.Next(4) == 0)).Id);
                var pick = mine[random.Next(mine.Count)];
                switch (random.Next(6))
                {
                    case 0:
                        await h.Actor.MoveAsync([pick], MoveTarget.Top, cancellationToken: Ct);
                        break;
                    case 1:
                        await h.Actor.MoveAsync([pick, mine[random.Next(mine.Count)]], MoveTarget.Bottom, cancellationToken: Ct);
                        break;
                    case 2:
                        // The anchor may belong to another worker and be gone already: a rejection is fine.
                        await h.Actor.MoveAsync([pick], MoveTarget.Before, random.Next(1, 240), Ct);
                        break;
                    case 3:
                        await h.Actor.RemoveAsync(pick, cancellationToken: Ct);
                        mine.Remove(pick);
                        break;
                    case 4:
                        await h.Actor.PauseAsync(pick, Ct);
                        break;
                    default:
                        await h.Actor.StartResolvingAsync(pick, Ct);
                        break;
                }
            }

            return mine.Count;
        }, Ct));
        var remaining = (await Task.WhenAll(workers)).Sum();

        var snapshot = await h.Actor.GetSnapshotAsync(Ct);
        Assert.Equal(remaining, snapshot.Items.Count);
        Assert.Equal(Enumerable.Range(1, remaining).Select(i => (long)i), snapshot.Items.Select(i => i.Position));

        await using var db = h.Root.CreateContext();
        var stored = await db.LoadActiveAsync(Ct);
        Assert.Equal(snapshot.Items.Select(i => (i.Id, i.Position, i.State)), stored.Select(i => (i.Id, i.Position, i.State)));

        var sequences = h.DrainEvents().Select(e => e.Sequence).ToList();
        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(i => (long)i), sequences);
        Assert.Equal(sequences[^1], snapshot.Sequence);
    }

    [Fact]
    public async Task Events_are_published_only_after_the_transaction_commits()
    {
        EventBus? bus = null;
        var saves = new SaveRecorder(() => bus?.LastSequence ?? 0);
        await using var h = await QueueHarness.StartAsync(contextFactory: root => new RecordingContextFactory(root, saves));
        bus = h.Bus;

        // Check each event as it arrives, while later commands are still running.
        var checker = Task.Run(async () =>
        {
            var checkedEvents = 0;
            await foreach (var e in h.Events.Reader.ReadAllAsync(Ct))
            {
                await using var db = h.Root.CreateContext();
                switch (e)
                {
                    case ItemAddedEvent added:
                        Assert.True(await db.Items.AnyAsync(i => i.Id == added.Item.Id, Ct));
                        break;
                    case ItemUpdatedEvent updated:
                        Assert.True(await db.ItemEvents.AnyAsync(
                            ev => ev.ItemId == updated.Item.Id && ev.FromState == updated.FromState && ev.ToState == updated.Item.State,
                            Ct));
                        break;
                    case ItemRemovedEvent removed:
                        Assert.True(await db.ItemEvents.AnyAsync(ev => ev.ItemId == removed.ItemId && ev.ToState == ItemState.Removed, Ct));
                        break;
                }

                if (++checkedEvents == 40)
                {
                    return checkedEvents;
                }
            }

            return checkedEvents;
        }, Ct);

        for (var i = 0; i < 10; i++)
        {
            var item = await h.AddAsync($"https://{i}");
            await h.Actor.StartResolvingAsync(item.Id, Ct);
            await h.Actor.PauseAsync(item.Id, Ct);
            await h.Actor.RemoveAsync(item.Id, cancellationToken: Ct);
        }

        Assert.Equal(40, await checker);
        Assert.True(saves.Count >= 40);
        Assert.Empty(saves.PublishedDuringSave);
    }

    [Fact]
    public async Task Startup_requeues_interrupted_items_and_deletes_orphaned_work_folders()
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        string[] orphans = [];

        await using var h = await QueueHarness.StartAsync(async root =>
        {
            await using var db = root.CreateContext();
            ItemEntity Item(string name, ItemState state, long position) =>
                new() { Url = $"https://{name}", Title = name, PresetId = "best", State = state, Position = position };

            var items = new[]
            {
                Item("resolving", ItemState.Resolving, 3),
                Item("downloading", ItemState.Downloading, 7),
                Item("postprocessing", ItemState.PostProcessing, 8),
                Item("queued", ItemState.Queued, 12),
                Item("paused", ItemState.Paused, 13),
                Item("completed", ItemState.Completed, 0),
            };
            db.Items.AddRange(items);
            await db.SaveChangesAsync(Ct);
            foreach (var item in items)
            {
                ids[item.Title!] = item.Id;
                Directory.CreateDirectory(root.Paths.WorkDirFor(item.Id));
                await File.WriteAllTextAsync(Path.Combine(root.Paths.WorkDirFor(item.Id), "video.part"), "partial", Ct);
            }

            orphans = [root.Paths.WorkDirFor(99999), Path.Combine(root.Paths.WorkDir, "not-an-id"), root.Paths.WorkDirFor(ids["completed"])];
            foreach (var orphan in orphans)
            {
                Directory.CreateDirectory(orphan);
            }
        });

        var snapshot = await h.Actor.GetSnapshotAsync(Ct);
        Assert.Equal(
            [
                ("resolving", ItemState.Queued, 1L),
                ("downloading", ItemState.Queued, 2L),
                ("postprocessing", ItemState.Queued, 3L),
                ("queued", ItemState.Queued, 4L),
                ("paused", ItemState.Paused, 5L),
            ],
            snapshot.Items.Select(i => (i.Title!, i.State, i.Position)));

        foreach (var (name, from) in new[] { ("resolving", ItemState.Resolving), ("downloading", ItemState.Downloading), ("postprocessing", ItemState.PostProcessing) })
        {
            var last = (await h.ItemEventsAsync(ids[name]))[^1];
            Assert.Equal((from, ItemState.Queued, ItemEventReasons.Interrupted), (last.FromState, last.ToState, last.Reason));
            Assert.True(File.Exists(Path.Combine(h.Root.Paths.WorkDirFor(ids[name]), "video.part")), $"{name} keeps its partial file");
        }

        Assert.All(orphans, orphan => Assert.False(Directory.Exists(orphan), orphan));
        Assert.True(Directory.Exists(h.Root.Paths.WorkDirFor(ids["queued"])));
        Assert.True(Directory.Exists(h.Root.Paths.WorkDirFor(ids["paused"])));
    }

    /// <summary>Records the bus sequence when each save starts and flags saves during which something was published.</summary>
    private sealed class SaveRecorder(Func<long> lastSequence) : SaveChangesInterceptor
    {
        private long _atStart;

        public int Count { get; private set; }

        public List<(long Before, long After)> PublishedDuringSave { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _atStart = lastSequence();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Count++;
            var now = lastSequence();
            if (now != _atStart)
            {
                PublishedDuringSave.Add((_atStart, now));
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingContextFactory(TestDataRoot root, SaveRecorder recorder) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<StateDbContext>()
                .UseSqlite(StateDatabase.ConnectionString(root.Paths.StateDb))
                .AddInterceptors(recorder)
                .Options);
    }
}
