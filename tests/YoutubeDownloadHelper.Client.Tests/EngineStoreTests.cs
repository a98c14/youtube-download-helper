using System.Collections.Concurrent;
using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Client.Discovery;
using YoutubeDownloadHelper.Client.Store;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Engine.Events;
using YoutubeDownloadHelper.Tests.Shared;
using static YoutubeDownloadHelper.Client.Tests.TestKit;

namespace YoutubeDownloadHelper.Client.Tests;

/// <summary>
/// The client-side store against the real engine (PLAN.md §6.3): the snapshot, live updates, reconnecting with
/// <c>Last-Event-ID</c>, resync, and moving to a new engine.
/// </summary>
public sealed class EngineStoreTests
{
    [Fact]
    public async Task It_starts_from_the_snapshot_and_follows_live_updates()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = CreateClient(engine);
        var before = await AddAsync(client, "https://fake.test/success?id=storesnap01", paused: true);
        var network = new TestNetwork(engine.Server.CreateHandler());
        await using var store = CreateStore(new DiscoveryFileLocator(engine.Paths.EngineJson), network);
        var notified = new ConcurrentQueue<NotifyData>();
        var progressed = new ConcurrentQueue<long>();
        store.Notified += (_, n) => notified.Enqueue(n);
        store.EventReceived += (_, e) =>
        {
            if (e.Type == EventTypes.ItemProgress)
            {
                progressed.Enqueue(e.GetData<ItemProgressData>().ItemId);
            }
        };

        store.Start();
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);

        Assert.Equal(ConnectionState.Connected, store.State);
        Assert.Equal([before], Ids(store));
        Assert.Equal(engine.Token.Value, store.Engine!.Token);
        Assert.Equal(ApiRoutes.Version, store.Info!.ApiVersion);
        Assert.Empty(store.Blocks);

        // A download: added, progress, then it leaves the queue for history with a notification.
        var live = await AddAsync(client, "https://fake.test/success?id=storelive01&title=Canlı&size=1M&speed=1M&ppdelay=0.3");
        await WaitUntilAsync(store, () => progressed.Contains(live), "progress");
        await WaitUntilAsync(store, () => notified.Any(n => n.ItemId == live), "the completion notification");
        await WaitUntilAsync(store, () => Ids(store).SequenceEqual([before]), "the download to leave the queue");
        var completed = Assert.Single(notified);
        Assert.Equal((NotifyCodes.ItemCompleted, "Canlı"), (completed.Message.Code, completed.Message.Args["title"]));

        // Blocks, reordering and removal.
        await client.PauseQueueAsync(Ct);
        await WaitUntilAsync(store, () => store.Blocks.SequenceEqual([QueueBlock.PausedByUser]), "the pause block");
        await client.ResumeQueueAsync(Ct);
        await WaitUntilAsync(store, () => store.Blocks.Count == 0, "the block to lift");

        var last = await AddAsync(client, "https://fake.test/success?id=storesnap02", paused: true);
        await WaitUntilAsync(store, () => Ids(store).SequenceEqual([before, last]), "the new item");
        await client.MoveItemAsync(last, new MoveItemRequest(MoveTarget.Top), Ct);
        await WaitUntilAsync(store, () => Ids(store).SequenceEqual([last, before]), "the move");
        await client.RemoveItemAsync(before, cancellationToken: Ct);
        await WaitUntilAsync(store, () => Ids(store).SequenceEqual([last]), "the removal");
        await client.ResumeItemAsync(last, Ct);
        await WaitUntilAsync(store, () => store.Items.Count == 0, "the second download to finish");

        // All of it from one snapshot and one stream.
        Assert.Equal(1, store.ResyncCount);
        Assert.Single(network.To(HttpMethod.Get, ApiRoutes.Items));
        Assert.Single(network.To(HttpMethod.Get, ApiRoutes.Events));
    }

    [Fact]
    public async Task A_refresh_request_is_raised_and_a_browser_refresh_through_the_client_recovers_the_login()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = CreateClient(engine);
        await using var store = CreateStore(new DiscoveryFileLocator(engine.Paths.EngineJson), engine.Server.CreateHandler());
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AuthRefreshRequested += (_, _) => requested.TrySetResult();
        store.Start();
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);

        // A session Google no longer accepts: the engine asks the connected client for a browser refresh.
        const long nextYear = 1_830_000_000;
        await client.SetCookiesAsync(
            new SetCookiesRequest(Netscape:
                $".youtube.com\tTRUE\t/\tTRUE\t{nextYear}\tLOGIN_INFO\tlogin-value\n"
                + $".youtube.com\tTRUE\t/\tTRUE\t{nextYear}\t__Secure-3PSIDTS\tpsidts-0-dead\n"),
            Ct);
        await requested.Task.WaitAsync(WaitTimeout, Ct);

        // What the app's hidden browser does (#57): take the lease and jar, let the page rotate *PSIDTS, hand it back.
        var begun = await client.BeginAuthRefreshAsync(Ct);
        var rotated = begun.Cookies.Select(c => c.Name == "__Secure-3PSIDTS" ? c with { Value = "psidts-1" } : c).ToList();
        var completed = await client.CompleteAuthRefreshAsync(new AuthRefreshCompleteRequest(begun.LeaseId, rotated), Ct);

        Assert.Equal((AuthStatus.Valid, 2), (completed.Auth.Status, completed.Stored));
        await WaitUntilAsync(store, () => store.Auth?.Status == AuthStatus.Valid, "the store to see the valid login");
        var again = await Assert.ThrowsAsync<EngineApiException>(
            () => client.CompleteAuthRefreshAsync(new AuthRefreshCompleteRequest(begun.LeaseId, rotated), Ct));
        Assert.Equal(AuthCodes.RefreshNoLease, again.Code);
    }

    [Fact]
    public async Task A_lost_connection_reconnects_with_Last_Event_ID_and_gets_the_missed_events_replayed()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = CreateClient(engine);
        var network = new TestNetwork(engine.Server.CreateHandler());
        await using var store = CreateStore(new FixedEngineLocator(DiscoveryOf(engine)), network);
        var states = new ConcurrentQueue<ConnectionState>();
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineStore.State))
            {
                states.Enqueue(store.State);
            }
        };
        store.Start();
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);
        var first = await AddAsync(client, "https://fake.test/success?id=replay00001", paused: true);
        await WaitUntilAsync(store, () => Ids(store).SequenceEqual([first]), "the first item");
        var seen = store.Sequence!.Value;

        network.Disconnect();
        await WaitUntilAsync(store, () => store.State == ConnectionState.Reconnecting, "the store to notice");
        var second = await AddAsync(client, "https://fake.test/success?id=replay00002", paused: true);
        var third = await AddAsync(client, "https://fake.test/success?id=replay00003", paused: true);
        await client.MoveItemAsync(third, new MoveItemRequest(MoveTarget.Top), Ct);

        // Reconnect attempts fail while the network is down, then one gets through.
        await WaitUntilAsync(store, () => network.To(HttpMethod.Get, ApiRoutes.Events).Count >= 3, "reconnect attempts");
        network.Restore();
        await WaitUntilAsync(store, () => Ids(store).SequenceEqual([third, first, second]), "the replayed events");
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);

        var streams = network.To(HttpMethod.Get, ApiRoutes.Events);
        Assert.Null(streams[0].LastEventId);
        Assert.All(streams.Skip(1), r => Assert.Equal(seen.ToString(System.Globalization.CultureInfo.InvariantCulture), r.LastEventId));
        Assert.Equal(1, store.ResyncCount);
        Assert.Single(network.To(HttpMethod.Get, ApiRoutes.Items));
        Assert.Equal([ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Connected], states);
        Assert.Null(store.LastError);
    }

    [Fact]
    public async Task When_the_missed_events_are_gone_the_store_resyncs_from_a_new_snapshot()
    {
        await using var engine = await TestEngine.StartAsync(
            fakeTools: true,
            configureServices: s => s.AddSingleton(new EventStreamOptions { BufferSize = 2 }));
        using var client = CreateClient(engine);
        var network = new TestNetwork(engine.Server.CreateHandler());
        await using var store = CreateStore(new FixedEngineLocator(DiscoveryOf(engine)), network);
        var resynced = 0;
        store.Resynced += (_, _) => Interlocked.Increment(ref resynced);
        store.Start();
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);
        var first = await AddAsync(client, "https://fake.test/success?id=resync00001", paused: true);
        await WaitUntilAsync(store, () => store.Items.Count == 1, "the first item");

        network.Disconnect();
        await WaitUntilAsync(store, () => store.State == ConnectionState.Reconnecting, "the store to notice");
        List<long> missed = [];
        for (var i = 2; i <= 4; i++)
        {
            missed.Add(await AddAsync(client, $"https://fake.test/success?id=resync0000{i}", paused: true));
        }

        await client.PauseQueueAsync(Ct);
        network.Restore();
        await WaitUntilAsync(store, () => store.ResyncCount == 2, "the resync");
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);

        Assert.Equal([first, .. missed], Ids(store));
        Assert.Equal([QueueBlock.PausedByUser], store.Blocks);
        Assert.Equal(2, resynced);
        Assert.Equal(2, network.To(HttpMethod.Get, ApiRoutes.Items).Count);
        Assert.NotNull(network.To(HttpMethod.Get, ApiRoutes.Events)[^1].LastEventId);

        // Live events keep coming after the resync.
        await client.ResumeQueueAsync(Ct);
        await WaitUntilAsync(store, () => store.Blocks.Count == 0, "the block to lift");
    }

    [Fact]
    public async Task A_new_engine_gets_a_fresh_snapshot()
    {
        await using var oldEngine = await TestEngine.StartAsync(fakeTools: true);
        await using var newEngine = await TestEngine.StartAsync(fakeTools: true);
        using var oldClient = CreateClient(oldEngine);
        using var newClient = CreateClient(newEngine);
        await AddAsync(oldClient, "https://fake.test/success?id=oldengine01", paused: true);
        await AddAsync(newClient, "https://fake.test/success?id=newengine01", paused: true);
        await AddAsync(newClient, "https://fake.test/success?id=newengine02", paused: true);
        var network = new TestNetwork(oldEngine.Server.CreateHandler());
        var locator = new MovableLocator(DiscoveryOf(oldEngine));
        await using var store = CreateStore(locator, network);
        store.Start();
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);
        Assert.Equal(["https://fake.test/success?id=oldengine01"], Urls(store));

        // The engine restarts: a new token in a new discovery file, sequence numbers from the start.
        network.Disconnect();
        await WaitUntilAsync(store, () => store.State == ConnectionState.Reconnecting, "the store to notice");
        locator.Engine = DiscoveryOf(newEngine);
        network.Target = newEngine.Server.CreateHandler();
        network.Restore();
        await WaitUntilAsync(store, () => store.ResyncCount == 2, "the resync");

        Assert.Equal(["https://fake.test/success?id=newengine01", "https://fake.test/success?id=newengine02"], Urls(store));
        Assert.Equal(newEngine.Token.Value, store.Engine!.Token);
        Assert.Null(network.To(HttpMethod.Get, ApiRoutes.Events)[^1].LastEventId);
        Assert.Equal(2, (await store.Client!.GetItemsAsync(cancellationToken: Ct)).Items.Count);
    }

    [Fact]
    public async Task Without_an_engine_it_keeps_trying_until_one_appears()
    {
        await using var engine = await TestEngine.StartAsync();
        var network = new TestNetwork(engine.Server.CreateHandler());
        var locator = new MovableLocator(null);
        await using var store = CreateStore(locator, network);

        store.Start();
        await Task.Delay(100, Ct);
        Assert.Equal(ConnectionState.Connecting, store.State);
        Assert.Null(store.Client);
        locator.Engine = DiscoveryOf(engine);

        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);
        Assert.Equal(ConnectionState.Connected, store.State);
    }

    [Fact]
    public async Task Engine_shutdown_is_reported_as_stopped()
    {
        await using var engine = await TestEngine.StartAsync();
        var network = new TestNetwork(engine.Server.CreateHandler());
        await using var store = CreateStore(new FixedEngineLocator(DiscoveryOf(engine)), network);
        store.Start();
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);

        await store.Client!.ShutdownAsync(Ct);

        await WaitUntilAsync(store, () => store.State == ConnectionState.EngineStopped, "engine.stopping");
    }

    [Fact]
    public async Task With_a_synchronization_context_every_change_is_raised_on_it()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = CreateClient(engine);
        using var context = new SingleThreadContext();
        await using var store = new EngineStore(
            new FixedEngineLocator(DiscoveryOf(engine)),
            new EngineStoreOptions { Client = ClientOptions(engine.Server.CreateHandler()), Reconnect = FastReconnect, SynchronizationContext = context });
        var threads = new ConcurrentQueue<int>();
        ((INotifyCollectionChanged)store.Items).CollectionChanged += (_, _) => threads.Enqueue(Environment.CurrentManagedThreadId);
        store.PropertyChanged += (_, _) => threads.Enqueue(Environment.CurrentManagedThreadId);

        store.Start();
        await store.WaitUntilConnectedAsync(Ct).WaitAsync(WaitTimeout, Ct);
        var id = await AddAsync(client, "https://fake.test/success?id=context0001", paused: true);
        await WaitUntilAsync(store, () => Ids(store).SequenceEqual([id]), "the item");

        Assert.NotEmpty(threads);
        Assert.All(threads, t => Assert.Equal(context.ThreadId, t));
    }

    private static List<long> Ids(EngineStore store) => [.. store.Items.Select(i => i.Id)];

    private static List<string> Urls(EngineStore store) => [.. store.Items.Select(i => i.Url)];

    private static async Task<long> AddAsync(EngineClient client, string url, bool paused = false)
    {
        var result = Assert.Single((await client.AddItemsAsync(new AddItemsRequest(Urls: [url], Paused: paused), Ct)).Results);
        Assert.Equal(AddItemOutcome.Added, result.Outcome);
        return result.ItemId!.Value;
    }

    /// <summary>A UI-thread-like context: one thread runs everything sent or posted to it.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State, ManualResetEventSlim? Done)> _queue = [];
        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Test UI thread" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state, null));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim();
            _queue.Add((d, state, done));
            done.Wait();
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state, done) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                finally
                {
                    done?.Set();
                }
            }
        }
    }
}
