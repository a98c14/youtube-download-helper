using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Tests.Queue;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Core.Tests.Tools;

/// <summary>A <see cref="ToolManager"/> over a <see cref="QueueHarness"/>, with a fake feed and smoke test.</summary>
public sealed class ToolHarness : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private ToolHarness(QueueHarness queue, ToolLocatorOptions overrides, ToolManagerOptions? options)
    {
        Queue = queue;
        Overrides = overrides;
        Options = options ?? new ToolManagerOptions { RetryDelays = [TimeSpan.FromMilliseconds(50)] };
        Installer = new ToolInstaller(queue.Root.Paths, Feed, Smoke, TimeProvider.System, NullLogger<ToolInstaller>.Instance);
        Manager = NewManager();
    }

    public QueueHarness Queue { get; }

    public FakeToolFeed Feed { get; } = new();

    public FakeSmokeTest Smoke { get; } = new();

    public ToolLocatorOptions Overrides { get; }

    public ToolManagerOptions Options { get; }

    public ToolInstaller Installer { get; }

    public ToolManager Manager { get; private set; }

    public DataPaths Paths => Queue.Root.Paths;

    public EventBus Bus => Queue.Bus;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ToolHarness> StartAsync(
        Action<FakeToolFeed>? publish = null, ToolLocatorOptions? overrides = null, ToolManagerOptions? options = null)
    {
        var harness = new ToolHarness(await QueueHarness.StartAsync(), overrides ?? new ToolLocatorOptions(), options);
        publish?.Invoke(harness.Feed);
        await harness.Manager.InitializeAsync(Ct);
        return harness;
    }

    /// <summary>A new manager over the same data folder, as after an engine restart.</summary>
    public async Task<ToolManager> RestartAsync()
    {
        await Manager.DisposeAsync();
        Manager = NewManager();
        await Manager.InitializeAsync(Ct);
        return Manager;
    }

    public string VersionDir(ManagedTool tool, string version) => Installer.VersionDir(tool, version);

    public async Task<ToolEntity?> RowAsync(ManagedTool tool)
    {
        await using var db = Queue.Root.CreateContext();
        return await db.Tools.AsNoTracking().SingleOrDefaultAsync(t => t.Tool == tool.Name, Ct);
    }

    public IEnumerable<string> StagingLeftovers() =>
        Directory.Exists(Installer.StagingDir) ? Directory.EnumerateFileSystemEntries(Installer.StagingDir) : [];

    /// <summary>Reads events until one matches.</summary>
    public async Task<T> WaitForEventAsync<T>(Func<T, bool>? match = null)
        where T : EngineEvent
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);
        while (true)
        {
            var e = await Queue.Events.Reader.ReadAsync(timeout.Token);
            if (e is T typed && (match is null || match(typed)))
            {
                return typed;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        await Queue.DisposeAsync();
    }

    private ToolManager NewManager() => new(
        Queue.Root.ContextFactory, Queue.Root.Paths, Overrides, Installer, Queue.Actor, Queue.Bus, Queue.Root.Time, Options,
        NullLogger<ToolManager>.Instance);
}
