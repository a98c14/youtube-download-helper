using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// The tool manager (PLAN.md §5.6). It knows each tool's current version from the <c>tools</c> table, and is the
/// engine's <see cref="IToolLocator"/> (new runs get the current versions), <see cref="IToolHealth"/> (the
/// <c>ToolsMissing</c> queue block), <see cref="IToolStatusProvider"/> (<c>GET /info</c>) and
/// <see cref="IToolUpdateTrigger"/>.
/// <para>
/// A background loop checks the tools at startup: a tool that was never installed, or whose binary is missing or fails
/// its smoke test, is marked broken (so <see cref="ToolsReady"/> is false) and the latest release is installed with
/// <see cref="ToolInstaller"/>. A failed install is retried after <see cref="ToolManagerOptions.RetryDelays"/>. After a
/// repair, items waiting for a tool fix are requeued. Between checks, <see cref="ToolsReady"/> notices a binary that
/// disappeared (e.g. quarantined by antivirus) and asks the loop for a repair.
/// </para>
/// <para>
/// A tool with a path override in <see cref="ToolLocatorOptions"/> (development, FakeYtDlp) is not managed: it is never
/// installed, and it is ready while its file exists.
/// </para>
/// </summary>
public sealed partial class ToolManager(
    IDbContextFactory<StateDbContext> contextFactory,
    DataPaths paths,
    ToolLocatorOptions overrides,
    ToolInstaller installer,
    QueueActor queue,
    EventBus bus,
    TimeProvider time,
    ToolManagerOptions options,
    ILogger<ToolManager> logger)
    : IToolLocator, IToolHealth, IToolStatusProvider, IToolUpdateTrigger, IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly CancellationTokenSource _stop = new();

    // Guarded by _lock; replaced, never mutated.
    private Dictionary<string, ToolState> _states = new(StringComparer.Ordinal);
    private bool? _reportedReady;
    private bool _smokeRequested;

    private Task _loop = Task.CompletedTask;
    private bool _started;
    private int _failedChecks;

    public ToolPaths Current
    {
        get
        {
            var states = States;
            return new ToolPaths(
                ExecutablePath(ManagedTool.YtDlp, states),
                ExecutablePath(ManagedTool.Ffmpeg, states),
                ExecutablePath(ManagedTool.Deno, states));
        }
    }

    /// <summary>
    /// Whether every tool is installed, not known to be broken, and has its binaries. A binary that went missing marks
    /// its tool broken and asks the background loop for a repair.
    /// </summary>
    public bool ToolsReady
    {
        get
        {
            var ready = true;
            foreach (var tool in ManagedTool.All)
            {
                if (!IsUsable(tool, out var vanished))
                {
                    ready = false;
                    if (vanished)
                    {
                        MarkBroken(tool);
                        RequestCheck(smoke: false);
                    }
                }
            }

            return ready;
        }
    }

    private Dictionary<string, ToolState> States
    {
        get
        {
            lock (_lock)
            {
                return _states;
            }
        }
    }

    public IReadOnlyList<ToolInfo> GetTools() =>
        [.. ManagedTool.All.Select(t => new ToolInfo(t.Name, Override(t) is null ? StateOf(t, States).Version : null, IsUsable(t, out _)))];

    /// <summary>
    /// For now a health check with smoke tests, so a broken binary is reinstalled and the waiting items requeued. The
    /// periodic update check (#60) adds the actual "is there a newer yt-dlp" check here.
    /// </summary>
    public void RequestYtDlpUpdateCheck() => RequestCheck(smoke: true);

    /// <summary>Loads the current versions from the database and deletes what interrupted installs left. Call once, before the scheduler starts.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        installer.CleanStaging();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Tools.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var states = new Dictionary<string, ToolState>(StringComparer.Ordinal);
        foreach (var tool in ManagedTool.All)
        {
            var row = rows.FirstOrDefault(r => string.Equals(r.Tool, tool.Name, StringComparison.Ordinal));
            if (row?.Current is { } version && ToolInstaller.IsValidVersion(version))
            {
                states[tool.Name] = new ToolState(version, installer.VersionDir(tool, version), Broken: false);
            }
        }

        lock (_lock)
        {
            _states = states;
        }
    }

    /// <summary>Starts the background loop, whose first pass is the startup health check (with smoke tests).</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            throw new InvalidOperationException("The tool manager is already started.");
        }

        _started = true;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops the loop; an install in progress is cancelled and leaves nothing behind.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started || _stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _checkGate.Dispose();
    }

    /// <summary>
    /// One check: every managed tool that is not installed, lacks a binary or (with <paramref name="smoke"/>) fails its
    /// smoke test gets the latest release. Returns whether all tools are ready afterwards.
    /// </summary>
    public async Task<bool> CheckAsync(bool smoke, CancellationToken cancellationToken = default)
    {
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repaired = false;
            foreach (var tool in ManagedTool.All)
            {
                if (Override(tool) is not null)
                {
                    continue;
                }

                var state = StateOf(tool, States);
                if (state is { Version: not null, Directory: { } dir }
                    && await installer.IsHealthyAsync(tool, dir, smoke, cancellationToken).ConfigureAwait(false))
                {
                    if (state.Broken)
                    {
                        // It was reported missing, but it is back (e.g. restored from quarantine).
                        SetState(tool, state with { Broken = false });
                        repaired = true;
                    }

                    continue;
                }

                LogNeedsInstall(logger, tool.Name, state.Version ?? "none");
                MarkBroken(tool);
                repaired |= await InstallCoreAsync(tool, cancellationToken).ConfigureAwait(false);
            }

            var ready = ToolsReady;
            if (repaired && ready)
            {
                await queue.ReleaseWaitingAsync(ItemTrigger.ToolFixed, CancellationToken.None).ConfigureAwait(false);
            }

            ReportHealth();
            return ready;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// Installs the latest release of <paramref name="toolName"/> even when the current one is healthy (the update
    /// check of #60 and the first-run wizard). On failure the current version stays; on success items waiting for a tool
    /// fix are requeued.
    /// </summary>
    public async Task<bool> InstallLatestAsync(string toolName, CancellationToken cancellationToken = default)
    {
        var tool = ManagedTool.All.Single(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installed = await InstallCoreAsync(tool, cancellationToken).ConfigureAwait(false);
            if (installed && ToolsReady)
            {
                await queue.ReleaseWaitingAsync(ItemTrigger.ToolFixed, CancellationToken.None).ConfigureAwait(false);
            }

            ReportHealth();
            return installed;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<bool> InstallCoreAsync(ManagedTool tool, CancellationToken cancellationToken)
    {
        var result = await installer.InstallLatestAsync(tool, e => bus.Publish(e), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            bus.Publish(new ToolInstallFailedEvent(tool.Name, result.Error!));
            return false;
        }

        // Switching is one database write; until it commits, the current version (if any) stays.
        var version = result.Version!;
        string? previous;
        await using (var db = await contextFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var row = await db.Tools.SingleOrDefaultAsync(t => t.Tool == tool.Name, CancellationToken.None).ConfigureAwait(false);
            if (row is null)
            {
                row = new ToolEntity { Tool = tool.Name };
                db.Tools.Add(row);
            }

            previous = row.Current;
            if (!string.Equals(row.Current, version, StringComparison.Ordinal))
            {
                row.Previous = row.Current;
                row.Current = version;
            }

            row.Channel = result.Channel;
            row.LastCheck = time.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        SetState(tool, new ToolState(version, result.Directory, Broken: false), report: false);
        bus.Publish(new ToolInstalledEvent(tool.Name, version, string.Equals(previous, version, StringComparison.Ordinal) ? null : previous));
        ReportHealth();
        return true;
    }

    private async Task LoopAsync(CancellationToken stopping)
    {
        var smoke = true;
        while (!stopping.IsCancellationRequested)
        {
            bool ready;
            try
            {
                ready = await CheckAsync(smoke, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCheckFailed(logger, ex);
                ready = false;
            }

            TimeSpan? retry = null;
            if (ready)
            {
                _failedChecks = 0;
            }
            else
            {
                var delays = options.RetryDelays;
                retry = delays[Math.Min(_failedChecks, delays.Count - 1)];
                _failedChecks++;
                LogRetrying(logger, retry.Value);
            }

            try
            {
                await WaitAsync(retry, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }

            lock (_lock)
            {
                smoke = _smokeRequested;
                _smokeRequested = false;
            }
        }
    }

    /// <summary>Until a check is requested, or <paramref name="retry"/> is over.</summary>
    private async Task WaitAsync(TimeSpan? retry, CancellationToken stopping)
    {
        if (_requests.Reader.TryRead(out _))
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var requested = _requests.Reader.WaitToReadAsync(cts.Token).AsTask();
        var timer = retry is { } delay ? Task.Delay(delay, time, cts.Token) : Task.Delay(Timeout.Infinite, cts.Token);
        await Task.WhenAny(requested, timer).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        stopping.ThrowIfCancellationRequested();
        _requests.Reader.TryRead(out _);
    }

    private void RequestCheck(bool smoke)
    {
        if (smoke)
        {
            lock (_lock)
            {
                _smokeRequested = true;
            }
        }

        _requests.Writer.TryWrite(true);
    }

    /// <summary>Whether <paramref name="tool"/> can run; <paramref name="vanished"/> when it was believed installed but a binary is gone.</summary>
    private bool IsUsable(ManagedTool tool, out bool vanished)
    {
        vanished = false;
        if (Override(tool) is { } path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        var state = StateOf(tool, States);
        if (state.Version is null || state.Directory is null || state.Broken)
        {
            return false;
        }

        vanished = !ToolInstaller.HasFiles(tool, state.Directory);
        return !vanished;
    }

    private void MarkBroken(ManagedTool tool)
    {
        lock (_lock)
        {
            var state = StateOf(tool, _states);
            if (state.Broken)
            {
                return;
            }

            _states = new Dictionary<string, ToolState>(_states, StringComparer.Ordinal) { [tool.Name] = state with { Broken = true } };
        }

        ReportHealth();
    }

    private void SetState(ManagedTool tool, ToolState state, bool report = true)
    {
        lock (_lock)
        {
            _states = new Dictionary<string, ToolState>(_states, StringComparer.Ordinal) { [tool.Name] = state };
        }

        if (report)
        {
            ReportHealth();
        }
    }

    /// <summary>Publishes <see cref="ToolHealthChangedEvent"/> when <see cref="ToolsReady"/> differs from what was last published.</summary>
    private void ReportHealth()
    {
        var ready = ManagedTool.All.All(t => IsUsable(t, out _));
        lock (_lock)
        {
            if (_reportedReady == ready)
            {
                return;
            }

            _reportedReady = ready;

            // Under the lock, so two reports cannot reach the bus in the wrong order.
            bus.Publish(new ToolHealthChangedEvent(ready));
        }
    }

    private string ExecutablePath(ManagedTool tool, Dictionary<string, ToolState> states) =>
        Override(tool) ?? (StateOf(tool, states).Directory is { } dir
            ? Path.Combine(dir, tool.MainExecutable)
            : Path.Combine(paths.ToolsDir, tool.Name, tool.MainExecutable));

    private string? Override(ManagedTool tool)
    {
        var configured = tool.Name switch
        {
            ToolNames.YtDlp => overrides.YtDlp,
            ToolNames.Ffmpeg => overrides.Ffmpeg,
            ToolNames.Deno => overrides.Deno,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }

    private static ToolState StateOf(ManagedTool tool, Dictionary<string, ToolState> states) =>
        states.TryGetValue(tool.Name, out var state) ? state : ToolState.None;

    private sealed record ToolState(string? Version, string? Directory, bool Broken)
    {
        public static ToolState None { get; } = new(null, null, Broken: false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tool} (current: {Version}) is missing or broken; installing the latest release")]
    private static partial void LogNeedsInstall(ILogger logger, string tool, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "The tools are not ready; checking again in {Delay}")]
    private static partial void LogRetrying(ILogger logger, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "The tool check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
