using YoutubeDownloadHelper.Client.Discovery;
using YoutubeDownloadHelper.Client.Events;
using YoutubeDownloadHelper.Client.Store;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Client.Tests;

/// <summary>Shared helpers: clients and stores wired to a <see cref="TestEngine"/> through a <see cref="TestNetwork"/>.</summary>
public static class TestKit
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Reconnect fast, so the tests don't wait.</summary>
    public static readonly ReconnectPolicy FastReconnect = new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The engine's own discovery file, as it wrote it.</summary>
    public static EngineDiscovery DiscoveryOf(TestEngine engine) =>
        EngineDiscoveryFile.TryRead(engine.Paths.EngineJson) ?? throw new InvalidOperationException("The engine wrote no discovery file.");

    public static EngineClientOptions ClientOptions(HttpMessageHandler handler) => new() { Handler = handler };

    public static EngineClient CreateClient(TestEngine engine, HttpMessageHandler? handler = null) =>
        new(DiscoveryOf(engine), ClientOptions(handler ?? engine.Server.CreateHandler()));

    public static EngineStore CreateStore(IEngineLocator locator, HttpMessageHandler handler) =>
        new(locator, new EngineStoreOptions { Client = ClientOptions(handler), Reconnect = FastReconnect });

    /// <summary>Waits until <paramref name="condition"/> holds, checked while the store does not change.</summary>
    public static async Task WaitUntilAsync(EngineStore store, Func<bool> condition, string what)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(WaitTimeout);
        while (true)
        {
            lock (store.SyncRoot)
            {
                if (condition())
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {what} (store state {store.State}).");
            }
        }
    }

    /// <summary>A locator the test points at an engine, or at none.</summary>
    public sealed class MovableLocator(EngineDiscovery? engine) : IEngineLocator
    {
        public EngineDiscovery? Engine { get; set; } = engine;

        public ValueTask<EngineDiscovery?> LocateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Engine);
    }
}
