using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Errors;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Core.Tests.FakeYtDlp;
using YoutubeDownloadHelper.Core.Tests.Persistence;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Scheduling;

/// <summary>
/// A queue actor, the real runner on <c>YoutubeDownloadHelper.FakeYtDlp</c> and a <see cref="Scheduler"/> on a
/// <see cref="FakeTimeProvider"/>, over a migrated <see cref="TestDataRoot"/>. Disk space, the network probe, tool
/// health and the triggers are fakes.
/// </summary>
public sealed class SchedulerHarness : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly bool _ownsRoot;
    private bool _disposed;

    private SchedulerHarness(TestDataRoot root, bool ownsRoot, SchedulerOptions? options)
    {
        Root = root;
        _ownsRoot = ownsRoot;
        Actor = new QueueActor(root.ContextFactory, root.Paths, Bus, root.Time, NullLogger<QueueActor>.Instance);
        Events = Bus.Subscribe();
        var builder = new YtDlpCommandBuilder(
            root.Paths,
            new ConfiguredToolLocator(root.Paths, new ToolLocatorOptions { YtDlp = FakeYtDlpProcess.ExePath }),
            new PresetRegistry());
        Scheduler = new Scheduler(
            Actor,
            Bus,
            root.ContextFactory,
            root.Settings,
            root.Paths,
            builder,
            new YtDlpRunner(TimeProvider.System, NullLogger<YtDlpRunner>.Instance),
            new ErrorClassifier(),
            new RetryPolicy(Time, new Random(42)),
            new SchedulerSeams(ToolHealth, ToolUpdates, AuthVerification, new NoJobCookieProvider(), Disk, Network),
            options ?? new SchedulerOptions { MinYouTubeStartDelay = TimeSpan.Zero, MaxYouTubeStartDelay = TimeSpan.Zero },
            Time,
            NullLogger<Scheduler>.Instance);
    }

    public TestDataRoot Root { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public EventBus Bus { get; } = new();

    public QueueActor Actor { get; }

    public Scheduler Scheduler { get; }

    public EventSubscription Events { get; }

    public FakeDiskSpace Disk { get; } = new();

    public FakeNetworkProbe Network { get; } = new();

    public FakeToolHealth ToolHealth { get; } = new();

    public CountingTriggers ToolUpdates { get; } = new();

    public CountingTriggers AuthVerification { get; } = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="root">An existing data folder (an engine restart); the harness then leaves it in place.</param>
    /// <param name="options">The scheduler options; by default no delay between YouTube starts.</param>
    /// <param name="configure">Runs before the scheduler starts, e.g. to set the fakes.</param>
    public static async Task<SchedulerHarness> StartAsync(
        TestDataRoot? root = null, SchedulerOptions? options = null, Action<SchedulerHarness>? configure = null)
    {
        var owns = root is null;
        root ??= await new TestDataRoot().InitializedAsync();
        var harness = new SchedulerHarness(root, owns, options);
        configure?.Invoke(harness);
        await harness.Actor.StartAsync(Ct);
        await harness.Scheduler.StartAsync(Ct);
        return harness;
    }

    public async Task<long> AddAsync(string url, bool force = false, bool paused = false) =>
        (await Actor.AddAsync(new AddItemRequest(url, "best", Force: force, Paused: paused), Ct)).Id;

    public async Task<ItemEntity> ItemAsync(long itemId)
    {
        await using var db = Root.CreateContext();
        return await db.Items.AsNoTracking().SingleAsync(i => i.Id == itemId, Ct);
    }

    public async Task<List<ItemEventEntity>> ItemEventsAsync(long itemId)
    {
        await using var db = Root.CreateContext();
        return await db.ItemEvents.AsNoTracking().Where(e => e.ItemId == itemId).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    /// <summary>Waits until the item is in <paramref name="state"/> (and matches <paramref name="also"/>), as stored.</summary>
    public async Task<ItemEntity> WaitForAsync(long itemId, ItemState state, Func<ItemEntity, bool>? also = null)
    {
        ItemEntity? item = null;
        await WaitUntilAsync(
            async () =>
            {
                item = await ItemAsync(itemId);
                return item.State == state && (also is null || also(item));
            },
            () => $"item {itemId} to be {state}, but it is {item?.State} (attempt {item?.Attempt}, error {item?.ErrorCode})");
        return item!;
    }

    /// <summary>Waits until the scheduler waits for a timer with no job running and every event handled.</summary>
    public async Task<SchedulerIdle> WaitIdleAsync()
    {
        SchedulerIdle? idle = null;
        await WaitUntilAsync(
            () =>
            {
                idle = Scheduler.Idle;
                return Task.FromResult(idle is { } i && i.Sequence >= Bus.LastSequence);
            },
            () => $"the scheduler to be idle (jobs: {string.Join(", ", Scheduler.RunningItemIds)})");
        return idle!;
    }

    /// <summary>
    /// Moves the fake clock once the scheduler has armed its timer, so no timer is missed. When that fires the timer,
    /// also waits for the pass it causes to settle, so the caller sees its effect.
    /// </summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        var before = await WaitIdleAsync();
        Time.Advance(by);
        if (Time.GetUtcNow() >= before.WakeAt)
        {
            await WaitUntilAsync(
                () => Task.FromResult(Scheduler.Passes > before.Pass),
                () => "the scheduler to wake up");
            await WaitIdleAsync();
        }
    }

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

    /// <summary>
    /// An engine that dies: the actor goes first, so nothing is recorded, then the jobs' processes are killed. The data
    /// folder is left as it is for the next <see cref="StartAsync"/>.
    /// </summary>
    public async Task CrashAsync()
    {
        _disposed = true;
        await Actor.DisposeAsync();
        await Scheduler.DisposeAsync();
        Events.Dispose();
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, Func<string> what)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);
        while (!await condition())
        {
            try
            {
                await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {what()}.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await Scheduler.DisposeAsync();
            await Actor.DisposeAsync();
            Events.Dispose();
        }

        if (_ownsRoot)
        {
            await Root.DisposeAsync();
        }
    }
}

public sealed class FakeDiskSpace : IDiskSpace
{
    private long _free = 1L << 40;

    public long Free
    {
        get => Interlocked.Read(ref _free);
        set => Interlocked.Exchange(ref _free, value);
    }

    public long? GetAvailableBytes(string path) => Free;
}

public sealed class FakeNetworkProbe : INetworkProbe
{
    private int _probes;
    private bool _online;

    public bool Online
    {
        get => Volatile.Read(ref _online);
        set => Volatile.Write(ref _online, value);
    }

    public int Probes => Volatile.Read(ref _probes);

    public Task<bool> IsOnlineAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _probes);
        return Task.FromResult(Online);
    }
}

public sealed class FakeToolHealth : IToolHealth
{
    private bool _ready = true;

    public bool Ready
    {
        get => Volatile.Read(ref _ready);
        set => Volatile.Write(ref _ready, value);
    }

    public bool ToolsReady => Ready;
}

public sealed class CountingTriggers : IToolUpdateTrigger, IAuthVerificationTrigger
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void RequestYtDlpUpdateCheck() => Interlocked.Increment(ref _count);

    public void RequestVerification() => Interlocked.Increment(ref _count);
}
