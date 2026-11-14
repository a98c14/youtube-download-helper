using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// The tool manager (PLAN.md §5.6). It knows each tool's current and previous versions from the <c>tools</c> table, and
/// is the engine's <see cref="IToolLocator"/> (new runs get the current versions), <see cref="IToolHealth"/> (the
/// <c>ToolsMissing</c> queue block), <see cref="IToolStatusProvider"/> (<c>GET /info</c>), <see cref="IToolUpdateTrigger"/>
/// and <see cref="IToolUsage"/> (versions running jobs use).
/// <para>
/// A background loop checks the tools at startup: a tool that was never installed or lacks a binary is marked broken (so
/// <see cref="ToolsReady"/> is false) and the latest release is installed with <see cref="ToolInstaller"/>. A current
/// version that fails its smoke test (for yt-dlp also a simulated download while the network is up) is rolled back to
/// the previous version when that one passes; otherwise a broken one is reinstalled. A failed install is retried after
/// <see cref="ToolManagerOptions.RetryDelays"/>. Between checks, <see cref="ToolsReady"/> notices a binary that
/// disappeared (e.g. quarantined by antivirus) and asks the loop for a repair.
/// </para>
/// <para>
/// Update checks run at startup and every <see cref="ToolManagerOptions.UpdateCheckInterval"/>, for yt-dlp also when an
/// item failed on a broken extractor (<see cref="RequestYtDlpUpdateCheck"/>) or the yt-dlp channel changed. yt-dlp comes
/// from the channel in the settings; versions are compared within a channel, and a channel switch installs the other
/// channel's latest whatever its number. After every repair, update and rollback, the items waiting for a tool fix are
/// requeued, and version folders that are neither current, previous nor used by a running process are deleted.
/// </para>
/// <para>
/// A tool with a path override in <see cref="ToolLocatorOptions"/> (development, FakeYtDlp) is not managed: it is never
/// installed, updated or rolled back, and it is ready while its file exists.
/// </para>
/// </summary>
public sealed partial class ToolManager(
    IDbContextFactory<StateDbContext> contextFactory,
    DataPaths paths,
    ToolLocatorOptions overrides,
    ToolInstaller installer,
    SettingsStore settings,
    QueueActor queue,
    EventBus bus,
    TimeProvider time,
    ToolManagerOptions options,
    ILogger<ToolManager> logger)
    : IToolLocator, IToolHealth, IToolStatusProvider, IToolUpdateTrigger, IToolUsage, IAsyncDisposable
{
    /// <summary>
    /// The video yt-dlp's smoke test simulates downloading: "Me at the zoo", YouTube's first upload, public since 2005.
    /// </summary>
    public const string SimulateVideoUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly CancellationTokenSource _stop = new();

    // Guarded by _lock; replaced, never mutated.
    private Dictionary<string, ToolState> _states = new(StringComparer.Ordinal);
    private bool? _reportedReady;
    private bool _smokeRequested;
    private UpdateScope _updateRequested;

    // Guarded by _lock: version folder → how many running processes use it.
    private readonly Dictionary<string, int> _inUse = new(StringComparer.OrdinalIgnoreCase);

    private Task _loop = Task.CompletedTask;
    private bool _started;
    private int _failedChecks;

    private enum UpdateScope
    {
        None,
        YtDlp,
        All,
    }

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
                        RequestCheck(smoke: false, UpdateScope.None);
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

    /// <summary>Every tool with its versions, channel and last check (<c>GET /tools</c>).</summary>
    public async Task<ToolList> GetDetailsAsync(CancellationToken cancellationToken = default)
    {
        List<ToolEntity> rows;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            rows = await db.Tools.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var channel = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).YtDlpChannel;
        var states = States;
        var tools = ManagedTool.All.Select(tool =>
        {
            var installed = IsUsable(tool, out _);
            if (Override(tool) is not null)
            {
                return new ToolDetails(tool.Name, null, installed, Managed: false, null, null, null);
            }

            var row = rows.FirstOrDefault(r => string.Equals(r.Tool, tool.Name, StringComparison.Ordinal));
            return new ToolDetails(
                tool.Name,
                StateOf(tool, states).Version,
                installed,
                Managed: true,
                ToolChannels.Parse(row?.Channel),
                StateOf(tool, states).Previous,
                row?.LastCheck);
        });
        return new ToolList([.. tools], ToolsReady, channel);
    }

    /// <summary>
    /// A forced check because an item failed on a broken extractor (the scheduler asks at most once per hour): a health
    /// check with smoke tests (which can roll yt-dlp back), then a yt-dlp update check. Runs in the background.
    /// </summary>
    public void RequestYtDlpUpdateCheck() => RequestCheck(smoke: true, UpdateScope.YtDlp);

    /// <summary>
    /// An update check in the background: of every tool, with smoke tests (<c>POST /tools/check</c>), or of yt-dlp only,
    /// without (the yt-dlp channel changed, so its latest build is installed right away).
    /// </summary>
    public void RequestUpdateCheck(bool ytDlpOnly = false) =>
        RequestCheck(smoke: !ytDlpOnly, ytDlpOnly ? UpdateScope.YtDlp : UpdateScope.All);

    public IDisposable Use(ToolPaths tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        string[] dirs = [.. new[] { tools.YtDlp, tools.Ffmpeg, tools.Deno }
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(p)))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        lock (_lock)
        {
            foreach (var dir in dirs)
            {
                _inUse[dir] = _inUse.GetValueOrDefault(dir) + 1;
            }
        }

        return new UsageLease(this, dirs);
    }

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
                var previous = ToolInstaller.IsValidVersion(row.Previous) ? row.Previous : null;
                states[tool.Name] = new ToolState(version, installer.VersionDir(tool, version), Broken: false, previous);
            }
        }

        lock (_lock)
        {
            _states = states;
        }
    }

    /// <summary>
    /// Starts the background loop, whose first pass is the startup health check (with smoke tests) and update check.
    /// </summary>
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
    /// One health check: every managed tool that is not installed or lacks a binary gets the latest release. With
    /// <paramref name="smoke"/>, a current version that fails its smoke test is rolled back to a previous version that
    /// passes, or else (when it does not even run) reinstalled. Returns whether all tools are ready afterwards.
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
                if (state is { Version: not null, Directory: { } dir })
                {
                    var check = await installer.CheckAsync(tool, dir, smoke ? SmokeFor(tool) : null, cancellationToken).ConfigureAwait(false);
                    if (check == ToolCheck.Healthy)
                    {
                        if (state.Broken)
                        {
                            // It was reported missing, but it is back (e.g. restored from quarantine).
                            SetState(tool, state with { Broken = false });
                            repaired = true;
                        }

                        continue;
                    }

                    if (check is ToolCheck.Broken or ToolCheck.SimulationFailed)
                    {
                        if (await RollbackCoreAsync(tool, SmokeFor(tool), automatic: true, cancellationToken).ConfigureAwait(false)
                            == ToolRollbackResult.RolledBack)
                        {
                            repaired = true;
                            continue;
                        }

                        if (check == ToolCheck.SimulationFailed)
                        {
                            // It runs; the previous version (if any) cannot download either. An update may fix it.
                            LogSimulationFailed(logger, tool.Name, state.Version);
                            if (state.Broken)
                            {
                                SetState(tool, state with { Broken = false });
                                repaired = true;
                            }

                            continue;
                        }
                    }
                }

                LogNeedsInstall(logger, tool.Name, state.Version ?? "none");
                MarkBroken(tool);
                repaired |= await InstallCoreAsync(tool, null, cancellationToken).ConfigureAwait(false);
            }

            await AfterChangeAsync(repaired).ConfigureAwait(false);
            return ToolsReady;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// One update check of <paramref name="toolNames"/> (every tool when null): a managed, usable tool gets the feed's
    /// latest release when it is newer than the current version on the same channel, or when yt-dlp's channel changed. A
    /// version that was rolled back from is skipped. Returns false when the feed could not be reached for some tool.
    /// </summary>
    public async Task<bool> CheckForUpdatesAsync(IReadOnlyCollection<string>? toolNames = null, CancellationToken cancellationToken = default)
    {
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reached = true;
            var updated = false;
            foreach (var tool in ManagedTool.All)
            {
                if ((toolNames is not null && !toolNames.Contains(tool.Name, StringComparer.Ordinal))
                    || Override(tool) is not null
                    || !IsUsable(tool, out _))
                {
                    // A tool that is missing or broken is the health check's to repair.
                    continue;
                }

                var channel = await ChannelForAsync(tool, cancellationToken).ConfigureAwait(false);
                var (release, error) = await installer.GetLatestAsync(tool, channel, cancellationToken).ConfigureAwait(false);
                if (release is null)
                {
                    reached = false;
                    bus.Publish(new ToolInstallFailedEvent(tool.Name, error!));
                    continue;
                }

                var row = await RecordCheckAsync(tool).ConfigureAwait(false);
                if (!NeedsUpdate(row, release, channel))
                {
                    continue;
                }

                LogUpdating(logger, tool.Name, row.Current ?? "none", release.Version, release.Channel ?? "-");
                updated |= await InstallCoreAsync(tool, release, cancellationToken).ConfigureAwait(false);
            }

            await AfterChangeAsync(updated).ConfigureAwait(false);
            bus.Publish(new ToolsCheckedEvent());
            return reached;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// Installs the latest release of <paramref name="toolName"/> (on the configured channel) even when the current one
    /// is healthy (the first-run wizard). On failure the current version stays; on success items waiting for a tool fix
    /// are requeued.
    /// </summary>
    public async Task<bool> InstallLatestAsync(string toolName, CancellationToken cancellationToken = default)
    {
        var tool = ManagedTool.All.Single(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installed = await InstallCoreAsync(tool, null, cancellationToken).ConfigureAwait(false);
            await AfterChangeAsync(installed).ConfigureAwait(false);
            return installed;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// Makes the previous version of <paramref name="toolName"/> current again (<c>POST /tools/{tool}/rollback</c>), when
    /// it is still there and runs. The version it leaves becomes the previous one, and update checks skip it until a newer
    /// release comes out. Items waiting for a tool fix are requeued.
    /// </summary>
    public async Task<ToolRollbackResult> RollbackAsync(string toolName, CancellationToken cancellationToken = default)
    {
        if (ManagedTool.All.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)) is not { } tool)
        {
            return ToolRollbackResult.UnknownTool;
        }

        if (Override(tool) is not null)
        {
            return ToolRollbackResult.NotManaged;
        }

        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await RollbackCoreAsync(tool, ToolSmoke.VersionOnly, automatic: false, cancellationToken).ConfigureAwait(false);
            await AfterChangeAsync(result == ToolRollbackResult.RolledBack).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private static bool NeedsUpdate(ToolEntity row, ToolRelease release, string? channel)
    {
        if (row.Current is not { } current)
        {
            return true;
        }

        if (string.Equals(release.Version, row.SkippedVersion, StringComparison.Ordinal))
        {
            return false;
        }

        // A channel switch installs the other channel's latest, even when its number is lower (nightly → stable).
        if (!string.Equals(row.Channel, release.Channel ?? channel, StringComparison.Ordinal))
        {
            return true;
        }

        return ToolVersions.IsNewer(release.Version, current);
    }

    /// <summary>Installs <paramref name="release"/> and makes it current; the version it replaces becomes the previous one.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="release">The release to install; null asks the feed for the latest.</param>
    /// <param name="cancellationToken">Cancels the install (nothing is switched then).</param>
    private async Task<bool> InstallCoreAsync(ManagedTool tool, ToolRelease? release, CancellationToken cancellationToken)
    {
        var smoke = SmokeFor(tool);
        var result = release is null
            ? await installer.InstallLatestAsync(tool, await ChannelForAsync(tool, cancellationToken).ConfigureAwait(false), smoke, e => bus.Publish(e), cancellationToken).ConfigureAwait(false)
            : await installer.InstallAsync(tool, release, smoke, e => bus.Publish(e), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            bus.Publish(new ToolInstallFailedEvent(tool.Name, result.Error!));
            return false;
        }

        // Switching is one database write; until it commits, the current version (if any) stays.
        var version = result.Version!;
        string? previous;
        string? kept;
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
                row.PreviousChannel = row.Channel;
                row.Current = version;
            }

            if (string.Equals(row.SkippedVersion, version, StringComparison.Ordinal))
            {
                // Reinstalled on purpose (it was the only way to repair the tool).
                row.SkippedVersion = null;
            }

            row.Channel = result.Channel;
            row.LastCheck = time.GetUtcNow();
            kept = row.Previous;
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        SetState(tool, new ToolState(version, result.Directory, Broken: false, ToolInstaller.IsValidVersion(kept) ? kept : null), report: false);
        bus.Publish(new ToolInstalledEvent(tool.Name, version, string.Equals(previous, version, StringComparison.Ordinal) ? null : previous));
        ReportHealth();
        return true;
    }

    /// <summary>Swaps current and previous when the previous version passes <paramref name="smoke"/>.</summary>
    private async Task<ToolRollbackResult> RollbackCoreAsync(ManagedTool tool, ToolSmoke smoke, bool automatic, CancellationToken cancellationToken)
    {
        var state = StateOf(tool, States);
        if (state is not { Version: { } current, Previous: { } previous })
        {
            return ToolRollbackResult.NoPreviousVersion;
        }

        var dir = installer.VersionDir(tool, previous);
        if (!await installer.IsHealthyAsync(tool, dir, smoke, cancellationToken).ConfigureAwait(false))
        {
            LogPreviousBroken(logger, tool.Name, previous);
            return ToolRollbackResult.PreviousVersionBroken;
        }

        await using (var db = await contextFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var row = await db.Tools.SingleAsync(t => t.Tool == tool.Name, CancellationToken.None).ConfigureAwait(false);
            (row.Current, row.Previous) = (previous, current);
            (row.Channel, row.PreviousChannel) = (row.PreviousChannel, row.Channel);
            row.SkippedVersion = current;
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        LogRolledBack(logger, tool.Name, current, previous, automatic);
        SetState(tool, new ToolState(previous, dir, Broken: false, current), report: false);
        bus.Publish(new ToolRolledBackEvent(tool.Name, previous, current, automatic));
        ReportHealth();
        return ToolRollbackResult.RolledBack;
    }

    /// <summary>After a check: requeues the items waiting for a tool fix when something changed, and collects garbage.</summary>
    private async Task AfterChangeAsync(bool changed)
    {
        if (changed && ToolsReady)
        {
            await queue.ReleaseWaitingAsync(ItemTrigger.ToolFixed, CancellationToken.None).ConfigureAwait(false);
        }

        CollectGarbage();
        ReportHealth();
    }

    /// <summary>Deletes the version folders that are neither current, previous, nor used by a running process.</summary>
    private void CollectGarbage()
    {
        var states = States;
        foreach (var tool in ManagedTool.All)
        {
            var state = StateOf(tool, states);
            if (Override(tool) is not null || state.Version is null)
            {
                continue;
            }

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state.Version };
            if (state.Previous is { } previous)
            {
                keep.Add(previous);
            }

            var root = Path.GetFullPath(Path.Combine(paths.ToolsDir, tool.Name));
            lock (_lock)
            {
                foreach (var (dir, count) in _inUse)
                {
                    if (count > 0 && string.Equals(Path.GetDirectoryName(dir), root, StringComparison.OrdinalIgnoreCase))
                    {
                        keep.Add(Path.GetFileName(dir));
                    }
                }
            }

            installer.DeleteVersionsExcept(tool, keep);
        }
    }

    private async Task<ToolEntity> RecordCheckAsync(ManagedTool tool)
    {
        await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        var row = await db.Tools.SingleOrDefaultAsync(t => t.Tool == tool.Name, CancellationToken.None).ConfigureAwait(false);
        if (row is null)
        {
            row = new ToolEntity { Tool = tool.Name };
            db.Tools.Add(row);
        }

        row.LastCheck = time.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        return row;
    }

    /// <summary>The channel to install <paramref name="tool"/> from: the setting for yt-dlp, none for the others.</summary>
    private async Task<string?> ChannelForAsync(ManagedTool tool, CancellationToken cancellationToken) =>
        string.Equals(tool.Name, ToolNames.YtDlp, StringComparison.Ordinal)
            ? ToolChannels.Name((await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).YtDlpChannel)
            : null;

    /// <summary>
    /// The smoke test of <paramref name="tool"/>: <c>--version</c>, and for yt-dlp a simulated download of
    /// <see cref="SimulateVideoUrl"/> with the current ffmpeg and Deno (skipped while those are not usable).
    /// </summary>
    private ToolSmoke SmokeFor(ManagedTool tool)
    {
        if (!string.Equals(tool.Name, ToolNames.YtDlp, StringComparison.Ordinal)
            || !IsUsable(ManagedTool.Ffmpeg, out _)
            || !IsUsable(ManagedTool.Deno, out _))
        {
            return ToolSmoke.VersionOnly;
        }

        var current = Current;
        return new ToolSmoke(
        [
            "--ignore-config",
            "--encoding", "utf-8",
            "--cache-dir", paths.YtDlpCacheDir,
            "--ffmpeg-location", current.Ffmpeg,
            "--js-runtimes", "deno:" + current.Deno,
            "--simulate",
            "--no-playlist",
            "--quiet",
            "--no-warnings",
            "--",
            SimulateVideoUrl,
        ]);
    }

    private async Task LoopAsync(CancellationToken stopping)
    {
        var smoke = true;
        var update = UpdateScope.All;
        var updateDelay = TimeSpan.Zero;
        var updateScheduledAt = time.GetTimestamp();
        while (!stopping.IsCancellationRequested)
        {
            bool ready;
            try
            {
                ready = await CheckAsync(smoke, stopping).ConfigureAwait(false);
                if (update != UpdateScope.None)
                {
                    var reached = await CheckForUpdatesAsync(update == UpdateScope.YtDlp ? [ToolNames.YtDlp] : null, stopping).ConfigureAwait(false);
                    if (update == UpdateScope.All)
                    {
                        updateDelay = reached ? options.UpdateCheckInterval : options.UpdateRetryInterval;
                        updateScheduledAt = time.GetTimestamp();
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCheckFailed(logger, ex);
                ready = false;
                if (update == UpdateScope.All)
                {
                    updateDelay = options.UpdateRetryInterval;
                    updateScheduledAt = time.GetTimestamp();
                }
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

            var untilUpdate = updateDelay - time.GetElapsedTime(updateScheduledAt);
            try
            {
                await WaitAsync(retry is { } r && r < untilUpdate ? r : untilUpdate, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }

            lock (_lock)
            {
                smoke = _smokeRequested;
                update = _updateRequested;
                _smokeRequested = false;
                _updateRequested = UpdateScope.None;
            }

            if (time.GetElapsedTime(updateScheduledAt) >= updateDelay)
            {
                update = UpdateScope.All;
            }
        }
    }

    /// <summary>Until a check is requested, or <paramref name="delay"/> is over.</summary>
    private async Task WaitAsync(TimeSpan delay, CancellationToken stopping)
    {
        if (_requests.Reader.TryRead(out _))
        {
            return;
        }

        if (delay > TimeSpan.Zero)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            var requested = _requests.Reader.WaitToReadAsync(cts.Token).AsTask();
            var timer = Task.Delay(delay, time, cts.Token);
            await Task.WhenAny(requested, timer).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
        }

        stopping.ThrowIfCancellationRequested();
        _requests.Reader.TryRead(out _);
    }

    private void RequestCheck(bool smoke, UpdateScope update)
    {
        lock (_lock)
        {
            _smokeRequested |= smoke;
            if (update > _updateRequested)
            {
                _updateRequested = update;
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

    private void Release(string[] dirs)
    {
        lock (_lock)
        {
            foreach (var dir in dirs)
            {
                if (_inUse.TryGetValue(dir, out var count))
                {
                    if (count <= 1)
                    {
                        _inUse.Remove(dir);
                    }
                    else
                    {
                        _inUse[dir] = count - 1;
                    }
                }
            }
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

    /// <param name="Version">The current version, if any.</param>
    /// <param name="Directory">Its version folder.</param>
    /// <param name="Broken">A binary went missing; a repair is due.</param>
    /// <param name="Previous">The previous version, kept for a rollback.</param>
    private sealed record ToolState(string? Version, string? Directory, bool Broken, string? Previous)
    {
        public static ToolState None { get; } = new(null, null, Broken: false, null);
    }

    private sealed class UsageLease(ToolManager manager, string[] dirs) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                manager.Release(dirs);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tool} (current: {Version}) is missing or broken; installing the latest release")]
    private static partial void LogNeedsInstall(ILogger logger, string tool, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating {Tool} from {Current} to {Version} ({Channel})")]
    private static partial void LogUpdating(ILogger logger, string tool, string current, string version, string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rolled {Tool} back from {From} to {Version} (automatic: {Automatic})")]
    private static partial void LogRolledBack(ILogger logger, string tool, string from, string version, bool automatic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tool}'s previous version {Version} is missing or fails its smoke test; not rolling back")]
    private static partial void LogPreviousBroken(ILogger logger, string tool, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tool} {Version} cannot download the test video, and there is no working previous version")]
    private static partial void LogSimulationFailed(ILogger logger, string tool, string? version);

    [LoggerMessage(Level = LogLevel.Information, Message = "The tools are not ready; checking again in {Delay}")]
    private static partial void LogRetrying(ILogger logger, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "The tool check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}

/// <summary>The outcome of <see cref="ToolManager.RollbackAsync"/>.</summary>
public enum ToolRollbackResult
{
    RolledBack,

    /// <summary>Not one of <c>ToolNames</c>.</summary>
    UnknownTool,

    /// <summary>The tool has a path override, so the tool manager does not manage it.</summary>
    NotManaged,

    /// <summary>There is no previous version to go back to.</summary>
    NoPreviousVersion,

    /// <summary>The previous version's folder is incomplete or its binary does not run.</summary>
    PreviousVersionBroken,
}
