using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tests.Persistence;

namespace YoutubeDownloadHelper.Core.Tests.Queue;

/// <summary>A migrated <see cref="TestDataRoot"/> with a <see cref="QueueActor"/> and a subscription to its events.</summary>
public sealed class QueueHarness : IAsyncDisposable
{
    private QueueHarness(TestDataRoot root, IDbContextFactory<StateDbContext>? contextFactory)
    {
        Root = root;
        Actor = new QueueActor(contextFactory ?? root.ContextFactory, root.Paths, Bus, root.Time, NullLogger<QueueActor>.Instance);
        Events = Bus.Subscribe();
    }

    public TestDataRoot Root { get; }

    public EventBus Bus { get; } = new();

    public QueueActor Actor { get; }

    public EventSubscription Events { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Creates the harness; <paramref name="seed"/> runs against the migrated database before the actor starts.
    /// </summary>
    public static async Task<QueueHarness> StartAsync(
        Func<TestDataRoot, Task>? seed = null,
        Func<TestDataRoot, IDbContextFactory<StateDbContext>>? contextFactory = null)
    {
        var root = await new TestDataRoot().InitializedAsync();
        if (seed is not null)
        {
            await seed(root);
        }

        var harness = new QueueHarness(root, contextFactory?.Invoke(root));
        await harness.Actor.StartAsync(Ct);
        return harness;
    }

    public Task<QueueItem> AddAsync(string url = "https://www.youtube.com/watch?v=x", bool paused = false) =>
        Actor.AddAsync(new AddItemRequest(url, "best", Paused: paused), Ct);

    /// <summary>Every event published so far that has not been drained yet.</summary>
    public List<EngineEvent> DrainEvents()
    {
        var events = new List<EngineEvent>();
        while (Events.Reader.TryRead(out var e))
        {
            events.Add(e);
        }

        return events;
    }

    public async Task<List<ItemEventEntity>> ItemEventsAsync(long itemId)
    {
        await using var db = Root.CreateContext();
        return await db.ItemEvents.Where(e => e.ItemId == itemId).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    public async Task<ItemEntity> ItemAsync(long itemId)
    {
        await using var db = Root.CreateContext();
        return await db.Items.SingleAsync(i => i.Id == itemId, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        Events.Dispose();
        await Actor.DisposeAsync();
        await Root.DisposeAsync();
    }
}
