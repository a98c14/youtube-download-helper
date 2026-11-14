using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
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
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Core.Tests.Scheduling;

/// <summary>
/// A queue actor, the real runner on <c>YoutubeDownloadHelper.FakeYtDlp</c> and a <see cref="Scheduler"/> on a
/// <see cref="FakeTimeProvider"/>, over a migrated <see cref="TestDataRoot"/>. Disk space, the network probe, tool
/// health, the tool update trigger and the client presence are fakes. The auth stack is the engine's (verifications
/// run FakeYtDlp through a <see cref="ScriptedAuthProbe"/>), but the <see cref="AuthVerifier"/> runs only when a test
/// starts it (<c>verifier: true</c>).
/// </summary>
public sealed class SchedulerHarness : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly bool _ownsRoot;
    private bool _disposed;

    private SchedulerHarness(TestDataRoot root, bool ownsRoot, SchedulerOptions? options, AuthOptions? authOptions)
    {
        Root = root;
        _ownsRoot = ownsRoot;
        AuthOptions = authOptions ?? new AuthOptions();
        AuthVerification = new CountingTriggers(AuthRequests);
        Actor = new QueueActor(root.ContextFactory, root.Paths, Bus, root.Time, NullLogger<QueueActor>.Instance);
        CookieStore = new CookieJarStore(root.Paths, new DpapiSecretProtector(), Time, NullLogger<CookieJarStore>.Instance);
        Auth = new AuthManager(CookieStore, Actor, AuthRequests, Bus, Time, NullLogger<AuthManager>.Instance);
        Cookies = new CookieFileProvider(Auth, root.Paths, NullLogger<CookieFileProvider>.Instance);
        Events = Bus.Subscribe();
        var builder = new YtDlpCommandBuilder(
            root.Paths,
            new ConfiguredToolLocator(root.Paths, new ToolLocatorOptions { YtDlp = FakeYtDlpProcess.ExePath }),
            new PresetRegistry());
        var runner = new YtDlpRunner(TimeProvider.System, new NoToolUsage(), NullLogger<YtDlpRunner>.Instance);
        Probe = new ScriptedAuthProbe(new YtDlpAuthProbe(
            builder, runner, new ErrorClassifier(), ToolHealth, root.Paths, AuthOptions, NullLogger<YtDlpAuthProbe>.Instance));
        Lease = new BrowserRefreshLease(Auth, Probe, Bus, AuthOptions, Time, NullLogger<BrowserRefreshLease>.Instance);
        Verifier = new AuthVerifier(Auth, Probe, Lease, Presence, AuthRequests, Bus, AuthOptions, Time, NullLogger<AuthVerifier>.Instance);
        Scheduler = new Scheduler(
            Actor,
            Bus,
            root.ContextFactory,
            root.Settings,
            root.Paths,
            builder,
            runner,
            new ErrorClassifier(),
            new RetryPolicy(Time, new Random(42)),
            new SchedulerSeams(ToolHealth, ToolUpdates, AuthVerification, Cookies, Disk, Network, Lease),
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

    /// <summary>The scheduler's verification requests: counted, then passed on to <see cref="AuthRequests"/>.</summary>
    public CountingTriggers AuthVerification { get; }

    /// <summary>What the <see cref="Verifier"/> reads: the scheduler's requests and the <see cref="Auth"/> manager's cookie changes.</summary>
    public AuthVerificationRequests AuthRequests { get; } = new();

    public AuthOptions AuthOptions { get; }

    /// <summary>The cookie jar. Empty (not logged in, so jobs run without cookies) until a test logs in through <see cref="Auth"/>.</summary>
    public CookieJarStore CookieStore { get; }

    public AuthManager Auth { get; }

    public CookieFileProvider Cookies { get; }

    /// <summary>Verifications: FakeYtDlp, unless a test scripts the next outcomes.</summary>
    public ScriptedAuthProbe Probe { get; }

    public BrowserRefreshLease Lease { get; }

    /// <summary>Whether a client is connected for the browser-refresh step; none by default.</summary>
    public FakeUiClientPresence Presence { get; } = new();

    /// <summary>Runs only after <c>StartAsync(verifier: true)</c>.</summary>
    public AuthVerifier Verifier { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="root">An existing data folder (an engine restart); the harness then leaves it in place.</param>
    /// <param name="options">The scheduler options; by default no delay between YouTube starts.</param>
    /// <param name="configure">Runs before the scheduler starts, e.g. to set the fakes.</param>
    /// <param name="verifier">Also starts the <see cref="Verifier"/>.</param>
    /// <param name="authOptions">The auth timings; the product's by default.</param>
    public static async Task<SchedulerHarness> StartAsync(
        TestDataRoot? root = null,
        SchedulerOptions? options = null,
        Action<SchedulerHarness>? configure = null,
        bool verifier = false,
        AuthOptions? authOptions = null)
    {
        var owns = root is null;
        root ??= await new TestDataRoot().InitializedAsync();
        var harness = new SchedulerHarness(root, owns, options, authOptions);
        configure?.Invoke(harness);
        await harness.Actor.StartAsync(Ct);
        await harness.Scheduler.StartAsync(Ct);
        if (verifier)
        {
            await harness.Verifier.StartAsync(Ct);
        }

        return harness;
    }

    /// <summary>Waits until the verifier has finished <paramref name="runs"/> passes and waits again (for <paramref name="wakeAt"/>, if given).</summary>
    public async Task<VerifierIdle> WaitVerifierIdleAsync(long runs, DateTimeOffset? wakeAt = null)
    {
        VerifierIdle? idle = null;
        await WaitUntilAsync(
            () =>
            {
                idle = Verifier.Idle;
                return Task.FromResult(idle is { } i && i.Runs >= runs && (wakeAt is null || i.WakeAt == wakeAt));
            },
            () => $"the verifier to be idle after {runs} runs (runs: {Verifier.Runs}, idle: {Verifier.Idle})");
        return idle!;
    }

    /// <summary>Moves the fake clock once the verifier waits after <paramref name="runs"/> passes.</summary>
    public async Task AdvanceVerifierAsync(long runs, TimeSpan by)
    {
        await WaitVerifierIdleAsync(runs);
        Time.Advance(by);
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
        await Verifier.DisposeAsync();
        await Scheduler.DisposeAsync();
        Lease.Dispose();
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
            await Verifier.DisposeAsync();
            await Scheduler.DisposeAsync();
            Lease.Dispose();
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

/// <summary>Counts the requests, and passes verification requests on to <paramref name="next"/> if given.</summary>
public sealed class CountingTriggers(IAuthVerificationTrigger? next = null) : IToolUpdateTrigger, IAuthVerificationTrigger
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void RequestYtDlpUpdateCheck() => Interlocked.Increment(ref _count);

    public void RequestVerification()
    {
        Interlocked.Increment(ref _count);
        next?.RequestVerification();
    }
}

public sealed class FakeUiClientPresence : IUiClientPresence
{
    private bool _connected;

    public bool Connected
    {
        get => Volatile.Read(ref _connected);
        set => Volatile.Write(ref _connected, value);
    }

    public bool AnyClientConnected => Connected;
}

/// <summary>
/// The real probe (FakeYtDlp), except for the outcomes a test queues with <see cref="Next"/>: each is the answer to one
/// call, without running anything. <see cref="Calls"/> counts every call.
/// </summary>
public sealed class ScriptedAuthProbe(IAuthProbe inner) : IAuthProbe
{
    private readonly ConcurrentQueue<AuthProbeOutcome> _script = new();
    private readonly ConcurrentQueue<CookieJar> _probed = new();
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Every jar probed, in order.</summary>
    public IReadOnlyList<CookieJar> Probed => [.. _probed];

    public void Next(params AuthProbeOutcome[] outcomes)
    {
        foreach (var outcome in outcomes)
        {
            _script.Enqueue(outcome);
        }
    }

    public Task<AuthProbeResult> ProbeAsync(CookieJar jar, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        _probed.Enqueue(jar);
        return _script.TryDequeue(out var outcome)
            ? Task.FromResult(new AuthProbeResult(
                outcome, null, null, outcome == AuthProbeOutcome.Valid ? null : new EngineMessage(ErrorCodes.LoginRequired, "scripted")))
            : inner.ProbeAsync(jar, cancellationToken);
    }
}
