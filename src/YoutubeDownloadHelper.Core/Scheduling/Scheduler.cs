using System.ComponentModel;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Errors;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Scheduling;

/// <summary>
/// The scheduler (PLAN.md §4.3): starts the next <see cref="ItemState.Queued"/> item by position while fewer than the
/// <c>Concurrency</c> setting run, honours the queue blocks, fires retry timers and tool-fix deadlines, and runs each
/// job (resolve, archive check, collision check, download) against the <see cref="QueueActor"/>, which owns every
/// state change. A job is stopped (its yt-dlp tree killed, partial files kept) when its item leaves a running state,
/// which the scheduler learns from the <see cref="EventBus"/>.
/// <para>
/// One loop decides; it runs after every queue event, when a job ends, and when its next timer is due, but at least
/// every <see cref="SchedulerOptions.MaxIdleWait"/>. Job workers run in parallel and only post to the actor.
/// </para>
/// </summary>
public sealed partial class Scheduler(
    QueueActor queue,
    EventBus bus,
    IDbContextFactory<StateDbContext> contextFactory,
    SettingsStore settingsStore,
    DataPaths paths,
    YtDlpCommandBuilder builder,
    YtDlpRunner runner,
    ErrorClassifier classifier,
    RetryPolicy policy,
    SchedulerSeams seams,
    SchedulerOptions options,
    TimeProvider time,
    ILogger<Scheduler> logger) : IQueueBlockProvider, IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, Job> _jobs = [];
    private readonly SortedSet<QueueBlock> _blocks = [];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly CancellationTokenSource _stop = new();
    private EventSubscription? _subscription;
    private Task _loop = Task.CompletedTask;
    private Task _events = Task.CompletedTask;
    private bool _started;
    private bool _stopped;

    // Guarded by _lock.
    private DiskRequirement? _diskRequirement;
    private DateTimeOffset _nextDiskCheck;
    private int _networkFailures;
    private DateTimeOffset _nextNetworkProbe;
    private DateTimeOffset? _lastToolUpdateRequest;

    // Loop only.
    private DateTimeOffset? _nextYouTubeStart;

    private SchedulerIdle? _idle;
    private long _passes;

    /// <summary>
    /// For tests and diagnostics: while the loop waits with its timer armed and no job runs, what its last pass saw;
    /// otherwise <see langword="null"/>. A <see cref="SchedulerIdle.Sequence"/> equal to <see cref="EventBus.LastSequence"/>
    /// means nothing is left to do before <see cref="SchedulerIdle.WakeAt"/>.
    /// </summary>
    public SchedulerIdle? Idle
    {
        get
        {
            var idle = Volatile.Read(ref _idle);
            lock (_lock)
            {
                return _jobs.Count == 0 ? idle : null;
            }
        }
    }

    /// <summary>How many passes the loop has made, for tests and diagnostics.</summary>
    public long Passes => Interlocked.Read(ref _passes);

    /// <summary>The ids of the items with a job, for tests and diagnostics.</summary>
    public IReadOnlyCollection<long> RunningItemIds
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobs.Keys];
            }
        }
    }

    /// <summary>The active queue blocks: <see cref="QueueBlock.PausedByUser"/> from the actor, the others from here.</summary>
    public IReadOnlyList<QueueBlock> GetBlocks()
    {
        lock (_lock)
        {
            return queue.IsPaused ? [QueueBlock.PausedByUser, .. _blocks] : [.. _blocks];
        }
    }

    /// <summary>Starts the loop. Call once, after <see cref="QueueActor.StartAsync"/>.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            throw new InvalidOperationException("The scheduler is already started.");
        }

        _started = true;
        _subscription = bus.Subscribe();
        var subscription = _subscription;
        _events = Task.Run(() => ReadEventsAsync(subscription, _stop.Token), CancellationToken.None);
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Graceful stop: no new job starts, running jobs are stopped (partial files kept) and their items go back to
    /// <see cref="ItemState.Queued"/> with reason <see cref="ItemEventReasons.EngineStopped"/>, so they continue at
    /// the next start.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started || _stopped)
        {
            return;
        }

        _stopped = true;
        await _stop.CancelAsync();
        await _loop.ConfigureAwait(false);

        List<Job> jobs;
        lock (_lock)
        {
            jobs = [.. _jobs.Values];
        }

        foreach (var job in jobs)
        {
            job.Stop(StopReason.Shutdown);
        }

        await Task.WhenAll(jobs.Select(j => j.Task)).WaitAsync(cancellationToken).ConfigureAwait(false);
        _subscription?.Dispose();
        await _events.ConfigureAwait(false);
    }

    /// <summary>Makes the loop look at the queue now, e.g. after the tool health changed.</summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    // ---- The loop -----------------------------------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            DateTimeOffset? next;
            long sequence;
            try
            {
                (next, sequence) = await TickAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogTickFailed(logger, ex);
                (next, sequence) = (null, -1);
            }

            var delay = next is { } at ? at - time.GetUtcNow() : options.MaxIdleWait;
            if (delay > options.MaxIdleWait)
            {
                delay = options.MaxIdleWait;
            }

            Interlocked.Increment(ref _passes);
            try
            {
                await WaitAsync(delay, sequence, stopping).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _idle, null);
            }
        }
    }

    /// <summary>
    /// Until <paramref name="delay"/> is over, <see cref="Wake"/> is called, or the scheduler stops. Counts as idle
    /// (<see cref="Idle"/>) only once the timer is armed, so a test that then moves the clock fires it.
    /// </summary>
    private async Task WaitAsync(TimeSpan delay, long sequence, CancellationToken stopping)
    {
        if (delay > TimeSpan.Zero && !_wake.Reader.TryRead(out _))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            var timer = Task.Delay(delay, time, cts.Token);
            var woken = _wake.Reader.WaitToReadAsync(cts.Token).AsTask();
            Volatile.Write(ref _idle, new SchedulerIdle(sequence, time.GetUtcNow() + delay, Passes));
            await Task.WhenAny(timer, woken).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
        }

        _wake.Reader.TryRead(out _);
    }

    /// <summary>One pass: timers, blocks, then starts. Returns when the next timer is due and the sequence it saw.</summary>
    private async Task<(DateTimeOffset? Next, long Sequence)> TickAsync(CancellationToken stopping)
    {
        var snapshot = await queue.GetSnapshotAsync(stopping).ConfigureAwait(false);
        var now = time.GetUtcNow();
        DateTimeOffset? next = null;
        void Later(DateTimeOffset at) => next = next is { } n && n <= at ? n : at;

        // Commands below use no token: once sent, a command runs anyway, and the loop must see its result.
        foreach (var item in snapshot.Items)
        {
            if (item.NextAttemptAt is not { } due)
            {
                continue;
            }

            if (due > now)
            {
                if (item.State is ItemState.WaitingRetry or ItemState.WaitingToolFix)
                {
                    Later(due);
                }
            }
            else if (item.State == ItemState.WaitingRetry)
            {
                await queue.RetryDueAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
            }
            else if (item.State == ItemState.WaitingToolFix)
            {
                await queue.FailAsync(item.Id, ToolFixTimedOut(item), CancellationToken.None).ConfigureAwait(false);
            }
        }

        SetBlock(QueueBlock.ToolsMissing, !seams.ToolHealth.ToolsReady, Messages.ToolsMissing);
        if (CheckDiskBlock(now) is { } diskCheck)
        {
            Later(diskCheck);
        }

        if (await CheckNetworkBlockAsync(now, stopping).ConfigureAwait(false) is { } probe)
        {
            Later(probe);
        }

        if (snapshot.Paused || GetBlocks().Count > 0)
        {
            return (next, snapshot.Sequence);
        }

        EngineSettings? settings = null;
        foreach (var item in snapshot.Items)
        {
            if (item.State != ItemState.Queued || stopping.IsCancellationRequested)
            {
                continue;
            }

            settings ??= await settingsStore.LoadAsync(stopping).ConfigureAwait(false);
            lock (_lock)
            {
                if (_jobs.Count >= Math.Max(1, settings.Concurrency))
                {
                    break;
                }

                if (_jobs.ContainsKey(item.Id))
                {
                    continue;
                }
            }

            var youTube = YouTubeUrls.IsYouTube(item.Url, item.Extractor);
            if (youTube && _nextYouTubeStart is { } notBefore && now < notBefore)
            {
                Later(notBefore);
                break;
            }

            var folders = Folders(settings, item);
            if (!HasRoom(folders, item.SizeBytes, minFreeAtFailure: null))
            {
                RaiseDiskBlock(folders, item.SizeBytes, fromError: false);
                break;
            }

            if (!await TryStartAsync(item, settings).ConfigureAwait(false))
            {
                continue;
            }

            if (youTube)
            {
                _nextYouTubeStart = now + YouTubeStartDelay();
            }
        }

        return (next, snapshot.Sequence);
    }

    private async Task<bool> TryStartAsync(QueueItem item, EngineSettings settings)
    {
        // Registered first, so a pause right after the start finds the job. Events up to here are older than it.
        var job = new Job(item, bus.LastSequence);
        lock (_lock)
        {
            _jobs[item.Id] = job;
        }

        QueueResult result;
        try
        {
            result = await queue.StartResolvingAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Forget(job);
            throw;
        }

        if (!result.Succeeded)
        {
            Forget(job);
            return false;
        }

        LogJobStarted(logger, item.Id, item.Attempt);
        job.Task = Task.Run(() => RunJobAsync(job, settings), CancellationToken.None);
        return true;
    }

    private void Forget(Job job)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(job.Item.Id, out var current) && current == job)
            {
                _jobs.Remove(job.Item.Id);
            }
        }
    }

    private TimeSpan YouTubeStartDelay()
    {
        var min = options.MinYouTubeStartDelay;
        var max = options.MaxYouTubeStartDelay;
        return max <= min ? min : min + ((max - min) * options.Random.NextDouble());
    }

    private async Task ReadEventsAsync(EventSubscription subscription, CancellationToken stopping)
    {
        try
        {
            await foreach (var engineEvent in subscription.Reader.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                switch (engineEvent)
                {
                    case ItemProgressEvent:
                        continue;
                    case ItemUpdatedEvent updated when !ItemStateMachine.IsRunning(updated.Item.State):
                        StopJob(updated.Item.Id, updated.Sequence, StopReason.LeftRunning);
                        break;
                    case ItemRemovedEvent removed:
                        StopJob(removed.ItemId, removed.Sequence, StopReason.Removed);
                        break;
                }

                Wake();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>Stops the item's job, unless the event is older than the job (it is about an earlier run).</summary>
    private void StopJob(long itemId, long sequence, StopReason reason)
    {
        Job? job;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(itemId, out job) || sequence <= job.AfterSequence)
            {
                return;
            }
        }

        job.Stop(reason);
    }

    // ---- Jobs ---------------------------------------------------------------------------------------------------

    private async Task RunJobAsync(Job job, EngineSettings settings)
    {
        var item = job.Item;
        var token = job.Token;
        string? cookiesFile = null;
        var outcome = JobOutcome.Stopped;
        try
        {
            cookiesFile = await seams.Cookies.BeforeJobAsync(item, paths.WorkDirFor(item.Id), token).ConfigureAwait(false);
            outcome = await RunStepsAsync(job, settings, cookiesFile).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await OnStoppedAsync(job).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // E.g. the actor is gone because the engine is going down; the next start recovers the item.
            LogJobFailed(logger, item.Id, ex);
        }
        finally
        {
            try
            {
                await seams.Cookies.AfterJobAsync(item, cookiesFile, outcome, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCookieHookFailed(logger, item.Id, ex);
            }

            Forget(job);
            Wake();
        }
    }

    /// <summary>Resolving → archive check → disk and collision checks → Downloading → PostProcessing → Completed.</summary>
    private async Task<JobOutcome> RunStepsAsync(Job job, EngineSettings settings, string? cookiesFile)
    {
        var item = job.Item;
        var request = new DownloadRequest
        {
            ItemId = item.Id,
            Url = item.Url,
            PresetId = item.PresetId,
            DownloadFolder = settings.DownloadFolder,
            FolderOverride = item.Folder,
            CookiesFile = cookiesFile,
        };
        var log = paths.JobLogFor(item.Id, item.Attempt);

        YtDlpResolveResult resolve;
        try
        {
            resolve = await runner.ResolveAsync(builder.Resolve(request), new YtDlpRunOptions { LogFile = log }, job.Token)
                .ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return await OnFailedAsync(job, CannotStart(ex), output: null, sizeEstimate: null, settings).ConfigureAwait(false);
        }

        if (resolve.Run.Cancelled)
        {
            await OnStoppedAsync(job).ConfigureAwait(false);
            return JobOutcome.Stopped;
        }

        if (!resolve.Run.Succeeded || resolve.Video is not { } video)
        {
            return await OnRunFailedAsync(job, resolve.Run, item.SizeBytes, settings).ConfigureAwait(false);
        }

        var metadata = new ItemMetadata(
            video.Extractor,
            video.VideoId,
            video.Title,
            video.Channel,
            video.ChannelId,
            video.UploadDate,
            video.Duration?.TotalSeconds,
            video.Availability,
            video.LiveStatus,
            video.SizeEstimate);

        if (!item.Force && await IsArchivedAsync(video).ConfigureAwait(false))
        {
            await queue.SkipAsync(item.Id, metadata, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return new JobOutcome(true, false, null);
        }

        var folders = Folders(settings, item);
        if (!HasRoom(folders, video.SizeEstimate, minFreeAtFailure: null))
        {
            RaiseDiskBlock(folders, video.SizeEstimate, fromError: false);
            await queue.InterruptAsync(item.Id, ErrorCodes.DiskFull, nameof(ErrorCategory.LocalIo), cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return JobOutcome.Stopped;
        }

        // Null when yt-dlp predicted no name: then no collision is known (PLAN.md §5.4).
        if (video.Filename is { Length: > 0 } predicted && DestinationCheck.NameIsTaken(predicted))
        {
            request = request with { AppendVideoId = true };
        }

        if (!(await queue.MetadataResolvedAsync(item.Id, metadata, CancellationToken.None).ConfigureAwait(false)).Succeeded)
        {
            return JobOutcome.Stopped;
        }

        YtDlpRunResult run;
        try
        {
            run = await DownloadAsync(job, request, log).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return await OnFailedAsync(job, CannotStart(ex), output: null, video.SizeEstimate, settings).ConfigureAwait(false);
        }

        if (run.Cancelled)
        {
            await OnStoppedAsync(job).ConfigureAwait(false);
            return JobOutcome.Stopped;
        }

        if (!run.Succeeded || run.FinalPath is not { } finalPath)
        {
            return await OnRunFailedAsync(job, run, video.SizeEstimate, settings).ConfigureAwait(false);
        }

        NoteSuccess();
        await queue.CompleteAsync(item.Id, finalPath, SizeOf(finalPath) ?? video.SizeEstimate, Output(run), CancellationToken.None)
            .ConfigureAwait(false);
        LogJobCompleted(logger, item.Id);
        return new JobOutcome(true, false, null);
    }

    /// <summary>The download run; progress goes to the actor in order, and post-processing is reported once.</summary>
    private async Task<YtDlpRunResult> DownloadAsync(Job job, DownloadRequest request, string log)
    {
        var itemId = job.Item.Id;
        var progress = Channel.CreateBounded<DownloadProgress>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var pump = PumpProgressAsync(itemId, progress.Reader);
        try
        {
            return await runner.RunAsync(
                builder.Download(request),
                new YtDlpRunOptions { LogFile = log, Progress = p => progress.Writer.TryWrite(p) },
                job.Token).ConfigureAwait(false);
        }
        finally
        {
            progress.Writer.TryComplete();
            await pump.ConfigureAwait(false);
        }
    }

    private async Task PumpProgressAsync(long itemId, ChannelReader<DownloadProgress> reader)
    {
        var postProcessing = false;
        try
        {
            await foreach (var p in reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!postProcessing && p.Phase is DownloadPhase.Processing or DownloadPhase.Moving or DownloadPhase.Done)
                {
                    postProcessing = true;
                    await queue.StartPostProcessingAsync(itemId, CancellationToken.None).ConfigureAwait(false);
                }

                await queue.ReportProgressAsync(
                    itemId,
                    new ItemProgress(
                        PhaseName(p.Phase),
                        p.DownloadedBytes,
                        p.TotalBytes,
                        p.BytesPerSecond,
                        p.Eta?.TotalSeconds,
                        p.Fraction),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogProgressFailed(logger, itemId, ex);
        }
    }

    private Task<JobOutcome> OnRunFailedAsync(Job job, YtDlpRunResult run, long? sizeEstimate, EngineSettings settings) =>
        OnFailedAsync(job, classifier.Classify(run.ExitCode, run.OutputTail), Output(run), sizeEstimate, settings);

    /// <summary>The action for a failure (PLAN.md §5.2), with the queue blocks it can raise.</summary>
    private async Task<JobOutcome> OnFailedAsync(
        Job job, ErrorClassification error, JobOutput? output, long? sizeEstimate, EngineSettings settings)
    {
        var item = job.Item;
        var outcome = new JobOutcome(false, false, error.Category);
        var category = error.Category.ToString();
        LogJobFailedWith(logger, item.Id, error.Error.Code);

        if (NoteFailure(error))
        {
            // Offline: the item waits for the network without using up a retry.
            await queue.InterruptAsync(item.Id, error.Error.Code, category, output, CancellationToken.None).ConfigureAwait(false);
            return outcome;
        }

        var decision = policy.Decide(error, item.Attempt);
        if (decision.Action == RetryAction.BlockDiskFull)
        {
            RaiseDiskBlock(Folders(settings, item), sizeEstimate, fromError: true);
            await queue.InterruptAsync(item.Id, error.Error.Code, category, output, CancellationToken.None).ConfigureAwait(false);
            return outcome;
        }

        var failure = decision.ToJobFailure(error, output)!;
        var result = await queue.FailAsync(item.Id, failure, CancellationToken.None).ConfigureAwait(false);
        if (result.Succeeded)
        {
            if (decision.Action == RetryAction.WaitForAuth)
            {
                seams.AuthVerification.RequestVerification();
            }
            else if (decision.Action == RetryAction.WaitForToolFix)
            {
                RequestToolUpdate();
            }
        }

        return outcome;
    }

    /// <summary>The job was stopped. Only a shutdown leaves its item running; it goes back to the queue.</summary>
    private async Task OnStoppedAsync(Job job)
    {
        switch (job.Reason)
        {
            case StopReason.Shutdown:
                await queue.InterruptAsync(job.Item.Id, ItemEventReasons.EngineStopped, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                break;
            case StopReason.Removed:
                // The actor could not delete the folder while yt-dlp held files in it.
                TryDeleteDirectory(paths.WorkDirFor(job.Item.Id));
                break;
        }
    }

    private async Task<bool> IsArchivedAsync(ResolvedVideo video)
    {
        var db = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var ids = await db.GetDownloadedVideoIdsAsync(video.Extractor, [video.VideoId]).ConfigureAwait(false);
            return ids.Contains(video.VideoId);
        }
    }

    // ---- Blocks -------------------------------------------------------------------------------------------------

    private void SetBlock(QueueBlock block, bool active, EngineMessage reason)
    {
        lock (_lock)
        {
            if (active ? _blocks.Add(block) : _blocks.Remove(block))
            {
                bus.Publish(new QueueBlockChangedEvent(block, active, active ? reason : null));
            }
        }
    }

    /// <summary>The work folder and the item's destination: partial files go to one, the finished file to the other.</summary>
    private string[] Folders(EngineSettings settings, QueueItem item) =>
    [
        paths.WorkDir,
        string.IsNullOrWhiteSpace(item.Folder)
            ? settings.DownloadFolder
            : Path.GetFullPath(Path.Combine(settings.DownloadFolder, item.Folder)),
    ];

    private bool HasRoom(IReadOnlyList<string> folders, long? sizeEstimate, long? minFreeAtFailure)
    {
        var required = Math.Max(0, sizeEstimate ?? 0) + options.DiskMarginBytes;
        long? leastFree = null;
        foreach (var folder in folders)
        {
            if (seams.DiskSpace.GetAvailableBytes(folder) is not { } free)
            {
                continue;
            }

            if (free < required)
            {
                return false;
            }

            leastFree = leastFree is { } l && l <= free ? l : free;
        }

        // After a disk-full error the fullest drive must have gained a margin, or the item would fail again at once.
        return minFreeAtFailure is not { } atFailure || leastFree is not { } least || least >= atFailure + options.DiskMarginBytes;
    }

    private void RaiseDiskBlock(string[] folders, long? sizeEstimate, bool fromError)
    {
        long? leastFree = null;
        if (fromError)
        {
            foreach (var folder in folders)
            {
                if (seams.DiskSpace.GetAvailableBytes(folder) is { } free)
                {
                    leastFree = leastFree is { } l && l <= free ? l : free;
                }
            }
        }

        lock (_lock)
        {
            _diskRequirement = new DiskRequirement(folders, sizeEstimate, leastFree);
            _nextDiskCheck = time.GetUtcNow() + options.DiskRecheckInterval;
            SetBlock(QueueBlock.DiskFull, true, Messages.DiskFull);
        }

        LogDiskFull(logger, sizeEstimate ?? 0);
    }

    /// <summary>Lifts <c>DiskFull</c> once there is room again. Returns the next recheck while it stays raised.</summary>
    private DateTimeOffset? CheckDiskBlock(DateTimeOffset now)
    {
        DiskRequirement requirement;
        lock (_lock)
        {
            if (_diskRequirement is null)
            {
                return null;
            }

            if (now < _nextDiskCheck)
            {
                return _nextDiskCheck;
            }

            requirement = _diskRequirement;
        }

        var room = HasRoom(requirement.Folders, requirement.SizeEstimate, requirement.MinFreeAtFailure);
        lock (_lock)
        {
            if (room)
            {
                _diskRequirement = null;
                SetBlock(QueueBlock.DiskFull, false, Messages.DiskFull);
                return null;
            }

            _nextDiskCheck = now + options.DiskRecheckInterval;
            return _nextDiskCheck;
        }
    }

    /// <summary>Counts network failures in a row. Returns whether <c>WaitingForNetwork</c> is (now) raised for this one.</summary>
    private bool NoteFailure(ErrorClassification error)
    {
        lock (_lock)
        {
            if (error.Error.Code != ErrorCodes.Network)
            {
                // Any other answer means the network works.
                _networkFailures = 0;
                return false;
            }

            _networkFailures++;
            if (_blocks.Contains(QueueBlock.WaitingForNetwork))
            {
                return true;
            }

            if (_networkFailures < options.NetworkFailureThreshold)
            {
                return false;
            }

            _nextNetworkProbe = time.GetUtcNow() + options.NetworkProbeInterval;
            SetBlock(QueueBlock.WaitingForNetwork, true, Messages.Offline);
        }

        LogOffline(logger);
        Wake();
        return true;
    }

    /// <summary>A job got through, so the network works: the failure count starts over and the block is lifted.</summary>
    private void NoteSuccess()
    {
        lock (_lock)
        {
            _networkFailures = 0;
            SetBlock(QueueBlock.WaitingForNetwork, false, Messages.Offline);
        }
    }

    /// <summary>Probes connectivity while <c>WaitingForNetwork</c> is raised. Returns the next probe while it stays raised.</summary>
    private async Task<DateTimeOffset?> CheckNetworkBlockAsync(DateTimeOffset now, CancellationToken stopping)
    {
        lock (_lock)
        {
            if (!_blocks.Contains(QueueBlock.WaitingForNetwork))
            {
                return null;
            }

            if (now < _nextNetworkProbe)
            {
                return _nextNetworkProbe;
            }
        }

        bool online;
        try
        {
            online = await seams.Network.IsOnlineAsync(stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProbeFailed(logger, ex);
            online = false;
        }

        lock (_lock)
        {
            if (online)
            {
                _networkFailures = 0;
                SetBlock(QueueBlock.WaitingForNetwork, false, Messages.Offline);
                return null;
            }

            _nextNetworkProbe = time.GetUtcNow() + options.NetworkProbeInterval;
            return _nextNetworkProbe;
        }
    }

    private void RequestToolUpdate()
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_lastToolUpdateRequest is { } last && now - last < options.ToolUpdateRequestInterval)
            {
                return;
            }

            _lastToolUpdateRequest = now;
        }

        seams.ToolUpdates.RequestYtDlpUpdateCheck();
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static JobFailure ToolFixTimedOut(QueueItem item) => new(
        ItemTrigger.Fail,
        item.ErrorCategory ?? ErrorCategory.ToolBroken,
        item.Error ?? Messages.ToolFixTimedOut);

    /// <summary>yt-dlp could not be started at all (missing or blocked): a broken tool install.</summary>
    private static ErrorClassification CannotStart(Win32Exception ex)
    {
        var hex = "0x" + ex.NativeErrorCode.ToString("X8", CultureInfo.InvariantCulture);
        return new ErrorClassification(
            ErrorCategory.ToolBroken,
            new EngineMessage(
                ErrorCodes.Crashed,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["exitCode"] = hex },
                $"yt-dlp could not be started ({hex}). Repairing the tools may fix this."));
    }

    private static JobOutput Output(YtDlpRunResult run) => new(run.ExitCode, run.OutputTail);

    private static string PhaseName(DownloadPhase phase) => phase switch
    {
        DownloadPhase.Preparing => "preparing",
        DownloadPhase.Downloading => "downloading",
        DownloadPhase.Processing => "processing",
        DownloadPhase.Moving => "moving",
        DownloadPhase.Done => "done",
        _ => phase.ToString(),
    };

    private static long? SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWorkDirNotDeleted(logger, path, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A scheduler pass failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started a job for item {ItemId} (retry {Attempt})")]
    private static partial void LogJobStarted(ILogger logger, long itemId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Item {ItemId} completed")]
    private static partial void LogJobCompleted(ILogger logger, long itemId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Item {ItemId} failed: {Code}")]
    private static partial void LogJobFailedWith(ILogger logger, long itemId, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "The job for item {ItemId} failed")]
    private static partial void LogJobFailed(ILogger logger, long itemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The cookie hook failed after the job for item {ItemId}")]
    private static partial void LogCookieHookFailed(ILogger logger, long itemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not report the progress of item {ItemId}")]
    private static partial void LogProgressFailed(ILogger logger, long itemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The disk is full ({SizeEstimate} bytes expected); new jobs wait")]
    private static partial void LogDiskFull(ILogger logger, long sizeEstimate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Several jobs failed with network errors; new jobs wait for the network")]
    private static partial void LogOffline(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The connectivity probe failed")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the work folder {Path}")]
    private static partial void LogWorkDirNotDeleted(ILogger logger, string path, Exception exception);

    private enum StopReason
    {
        None,

        /// <summary>The item left a running state (paused, interrupted, or the job's own last transition).</summary>
        LeftRunning,

        Removed,

        Shutdown,
    }

    private sealed class Job(QueueItem item, long afterSequence)
    {
        private readonly CancellationTokenSource _cts = new();
        private int _reason;

        public QueueItem Item { get; } = item;

        /// <summary>Events up to this sequence number were published before the job started.</summary>
        public long AfterSequence { get; } = afterSequence;

        public CancellationToken Token => _cts.Token;

        public StopReason Reason => (StopReason)Volatile.Read(ref _reason);

        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>The first reason wins. The source is never disposed, so a late stop cannot throw.</summary>
        public void Stop(StopReason reason)
        {
            Interlocked.CompareExchange(ref _reason, (int)reason, (int)StopReason.None);
            _cts.Cancel();
        }
    }

    /// <summary>What lifts <c>DiskFull</c>: room for the estimate on every folder, and after an error a margin more.</summary>
    private sealed record DiskRequirement(string[] Folders, long? SizeEstimate, long? MinFreeAtFailure);

    /// <summary>The messages of the blocks the scheduler raises (codes in <see cref="SchedulerCodes"/>).</summary>
    private static class Messages
    {
        public static readonly EngineMessage DiskFull = new(SchedulerCodes.DiskFull, "The disk is full. Downloads continue when there is room again.");

        public static readonly EngineMessage Offline = new(SchedulerCodes.Offline, "There is no connection to YouTube. Downloads continue when it is back.");

        public static readonly EngineMessage ToolsMissing = new(SchedulerCodes.ToolsMissing, "The download tools are missing or being repaired.");

        public static readonly EngineMessage ToolFixTimedOut = new(
            ErrorCodes.Extractor, "yt-dlp could not download this video, and no update fixed it within 24 hours.");
    }
}

/// <summary>What the scheduler's loop waits for (<see cref="Scheduler.Idle"/>).</summary>
/// <param name="Sequence">The last event sequence number its last pass saw (-1 when that pass failed).</param>
/// <param name="WakeAt">When its timer fires, if nothing wakes it earlier.</param>
/// <param name="Pass">The number of that pass (<see cref="Scheduler.Passes"/>).</param>
public sealed record SchedulerIdle(long Sequence, DateTimeOffset WakeAt, long Pass);

/// <summary>Message codes of the queue blocks the scheduler raises (<see cref="QueueBlockChangedEvent.Reason"/>).</summary>
public static class SchedulerCodes
{
    public const string DiskFull = "queue.block.diskFull";
    public const string Offline = "queue.block.waitingForNetwork";
    public const string ToolsMissing = "queue.block.toolsMissing";
}

/// <summary>
/// The scheduler's seams to later features, grouped so its constructor stays readable. Each has a no-op or simple
/// default (see <c>AddTools()</c>, <c>AddAuth()</c>, <c>AddScheduling()</c>).
/// </summary>
public sealed record SchedulerSeams(
    IToolHealth ToolHealth,
    IToolUpdateTrigger ToolUpdates,
    IAuthVerificationTrigger AuthVerification,
    IJobCookieProvider Cookies,
    IDiskSpace DiskSpace,
    INetworkProbe Network);
