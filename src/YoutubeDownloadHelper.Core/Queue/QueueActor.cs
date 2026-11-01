using System.Globalization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Queue;

/// <summary>Everything the queue holds, as of event <see cref="Sequence"/>.</summary>
public sealed record QueueSnapshot(
    IReadOnlyList<QueueItem> Items,
    IReadOnlyDictionary<long, ItemProgress> Progress,
    bool Paused,
    long Sequence);

/// <summary>
/// The queue actor (PLAN.md §4.3): the only component that mutates items. Commands from the API, job workers,
/// watchers and the tool manager go through one single-reader channel and run one at a time against the in-memory
/// queue. Each command that changes an item commits its rows (the item and an <c>item_events</c> row per transition)
/// in one transaction, and only then publishes to the <see cref="EventBus"/>.
/// <para>
/// The actor owns a long-lived <see cref="StateDbContext"/> that tracks the active items. If a save fails, it reloads
/// the queue from the database and the command throws.
/// </para>
/// <para>
/// The actor does not run jobs. The scheduler (#42) starts them with <see cref="StartResolvingAsync"/> and stops a
/// job when its item leaves a running state (e.g. paused or removed), which it learns from the event bus.
/// </para>
/// </summary>
public sealed class QueueActor(
    IDbContextFactory<StateDbContext> contextFactory,
    DataPaths paths,
    EventBus bus,
    TimeProvider time,
    ILogger<QueueActor> logger) : IAsyncDisposable
{
    /// <summary>How often, at most, an item's progress is written to the database.</summary>
    public static readonly TimeSpan ProgressCheckpointInterval = TimeSpan.FromSeconds(10);

    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });

    // In queue order; _active[i].Position == i + 1 after every commit.
    private readonly List<ItemEntity> _active = [];
    private readonly Dictionary<long, ProgressState> _progress = [];
    private StateDbContext? _db;
    private Task? _loop;
    private volatile bool _paused;

    /// <summary>Whether the user paused the whole queue. Not persisted: the queue runs again after an engine restart.</summary>
    public bool IsPaused => _paused;

    private StateDbContext Db => _db ?? throw new InvalidOperationException("The queue actor has not been started.");

    /// <summary>
    /// Loads the queue and recovers from an unclean stop (PLAN.md §5.1): running items go back to
    /// <see cref="ItemState.Queued"/> with reason <see cref="ItemEventReasons.Interrupted"/>, positions are made
    /// contiguous, and work folders that belong to no active item are deleted. Then starts processing commands.
    /// Call once, after <see cref="StateDatabase.InitializeAsync"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_db is not null)
        {
            throw new InvalidOperationException("The queue actor is already started.");
        }

        _db = await contextFactory.CreateDbContextAsync(cancellationToken);
        _active.AddRange(await _db.LoadActiveAsync(cancellationToken));

        var changes = new Changes();
        foreach (var item in _active.Where(i => ItemStateMachine.IsRunning(i.State)).ToList())
        {
            Fire(item, ItemTrigger.Interrupt, ItemEventReasons.Interrupted, changes);
        }

        await CommitAsync(changes);
        DeleteOrphanWorkDirs();

        _loop = Task.Run(RunAsync, CancellationToken.None);
    }

    /// <summary>The queue in order, with live progress, consistent with the event sequence number.</summary>
    public Task<QueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Send(
            () => Task.FromResult(new QueueSnapshot(
                [.. _active.Select(QueueItem.From)],
                _progress.ToDictionary(p => p.Key, p => p.Value.Latest),
                _paused,
                bus.LastSequence)),
            cancellationToken);

    // ---- User commands ------------------------------------------------------------------------------------------

    /// <summary>Adds an item at the end of the queue, <see cref="ItemState.Queued"/> or, if asked, <see cref="ItemState.Paused"/>.</summary>
    public Task<QueueItem> AddAsync(AddItemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PresetId);

        return Send(
            async () =>
            {
                var item = new ItemEntity
                {
                    Url = request.Url,
                    PresetId = request.PresetId,
                    Folder = request.Folder,
                    Force = request.Force,
                    WatcherId = request.WatcherId,
                    State = ItemState.Added,
                    Position = _active.Count + 1,
                };
                ApplyMetadata(item, request.Metadata);
                item.Events.Add(new ItemEventEntity { FromState = null, ToState = ItemState.Added, Reason = ItemEventReasons.Added });

                Db.Items.Add(item);
                _active.Add(item);

                var changes = new Changes { Added = item };
                Fire(item, request.Paused ? ItemTrigger.Pause : ItemTrigger.Enqueue, ItemEventReasons.Added, changes);
                await CommitAsync(changes);
                return QueueItem.From(item);
            },
            cancellationToken);
    }

    public Task<QueueResult> PauseAsync(long itemId, CancellationToken cancellationToken = default) =>
        TransitionAsync(itemId, ItemTrigger.Pause, ItemEventReasons.UserPause, cancellationToken: cancellationToken);

    public Task<QueueResult> ResumeAsync(long itemId, CancellationToken cancellationToken = default) =>
        TransitionAsync(itemId, ItemTrigger.Resume, ItemEventReasons.UserResume, cancellationToken: cancellationToken);

    /// <summary>Requeues a failed or waiting item; its retry count starts over and its error is cleared.</summary>
    public Task<QueueResult> RetryAsync(long itemId, CancellationToken cancellationToken = default) =>
        TransitionAsync(
            itemId,
            ItemTrigger.Retry,
            ItemEventReasons.UserRetry,
            item =>
            {
                item.Attempt = 0;
                ClearError(item);
            },
            cancellationToken: cancellationToken);

    /// <summary>
    /// Moves items, keeping their relative order, to the top, the bottom, or just before <paramref name="beforeItemId"/>.
    /// </summary>
    public Task<QueueResult> MoveAsync(
        IReadOnlyCollection<long> itemIds,
        MoveTarget target,
        long? beforeItemId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        return Send(
            async () =>
            {
                var moving = new List<ItemEntity>();
                foreach (var id in itemIds.Distinct())
                {
                    var item = FindActive(id);
                    if (item is null)
                    {
                        return QueueResult.NotFound(id);
                    }

                    moving.Add(item);
                }

                ItemEntity? anchor = null;
                if (target == MoveTarget.Before)
                {
                    if (beforeItemId is not { } anchorId)
                    {
                        return QueueResult.InvalidMove("Moving before an item needs that item's id.");
                    }

                    anchor = FindActive(anchorId);
                    if (anchor is null)
                    {
                        return QueueResult.NotFound(anchorId);
                    }

                    if (moving.Contains(anchor))
                    {
                        return QueueResult.InvalidMove("An item cannot be moved before itself.");
                    }
                }

                moving.Sort((a, b) => a.Position.CompareTo(b.Position));
                var set = moving.ToHashSet();
                _active.RemoveAll(set.Contains);

                var index = target switch
                {
                    MoveTarget.Top => 0,
                    MoveTarget.Bottom => _active.Count,
                    MoveTarget.Before => _active.IndexOf(anchor!),
                    _ => throw new ArgumentOutOfRangeException(nameof(target)),
                };
                _active.InsertRange(index, moving);

                await CommitAsync(new Changes());
                return QueueResult.Ok;
            },
            cancellationToken);
    }

    /// <summary>
    /// Removes an item, from the queue or from history (PLAN.md §8: a state, never a hard delete). Its work folder is
    /// deleted; with <paramref name="deleteFiles"/> its downloaded file is too, otherwise the file is left untracked.
    /// </summary>
    public Task<QueueResult> RemoveAsync(long itemId, bool deleteFiles = false, CancellationToken cancellationToken = default) =>
        Send(
            async () =>
            {
                var item = FindActive(itemId) ?? await Db.Items.FindAsync(itemId);
                if (item is null)
                {
                    return QueueResult.NotFound(itemId);
                }

                var changes = new Changes();
                if (!Fire(item, ItemTrigger.Remove, ItemEventReasons.UserRemove, changes))
                {
                    if (FindActive(itemId) is null)
                    {
                        Db.Entry(item).State = EntityState.Detached;
                    }

                    return QueueResult.InvalidTransition(itemId, item.State, ItemTrigger.Remove);
                }

                if (deleteFiles && item.FilePath is { } file)
                {
                    changes.FilesToDelete.Add(file);
                }

                item.FilePath = null;
                item.RemovedAt = time.GetUtcNow();
                await CommitAsync(changes);
                return QueueResult.Ok;
            },
            cancellationToken);

    /// <summary>
    /// Pauses the whole queue (queue block <c>PausedByUser</c>): no job starts, and running items go back to
    /// <see cref="ItemState.Queued"/> (their jobs are stopped), so they continue when the queue is resumed.
    /// </summary>
    public Task PauseAllAsync(CancellationToken cancellationToken = default) =>
        Send(
            async () =>
            {
                if (_paused)
                {
                    return true;
                }

                var changes = new Changes();
                foreach (var item in _active.Where(i => ItemStateMachine.IsRunning(i.State)).ToList())
                {
                    Fire(item, ItemTrigger.Interrupt, ItemEventReasons.QueuePaused, changes);
                }

                await CommitAsync(changes);
                _paused = true;
                bus.Publish(new QueuePausedChangedEvent(true));
                return true;
            },
            cancellationToken);

    public Task ResumeAllAsync(CancellationToken cancellationToken = default) =>
        Send(
            () =>
            {
                if (_paused)
                {
                    _paused = false;
                    bus.Publish(new QueuePausedChangedEvent(false));
                }

                return Task.FromResult(true);
            },
            cancellationToken);

    // ---- Worker and scheduler commands -------------------------------------------------------------------------

    /// <summary>Starts a job on a queued item. Rejected while the whole queue is paused.</summary>
    public Task<QueueResult> StartResolvingAsync(long itemId, CancellationToken cancellationToken = default) =>
        TransitionAsync(itemId, ItemTrigger.StartResolving, ItemEventReasons.JobStarted, rejectWhenPaused: true, cancellationToken: cancellationToken);

    public Task<QueueResult> MetadataResolvedAsync(long itemId, ItemMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return TransitionAsync(
            itemId,
            ItemTrigger.StartDownloading,
            ItemEventReasons.MetadataResolved,
            item => ApplyMetadata(item, metadata),
            cancellationToken: cancellationToken);
    }

    public Task<QueueResult> StartPostProcessingAsync(long itemId, CancellationToken cancellationToken = default) =>
        TransitionAsync(itemId, ItemTrigger.StartPostProcessing, ItemEventReasons.PostProcessing, cancellationToken: cancellationToken);

    public Task<QueueResult> CompleteAsync(
        long itemId,
        string filePath,
        long? sizeBytes,
        JobOutput? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return TransitionAsync(
            itemId,
            ItemTrigger.Complete,
            ItemEventReasons.Completed,
            item =>
            {
                item.FilePath = filePath;
                item.SizeBytes = sizeBytes ?? item.SizeBytes;
                ClearError(item);
            },
            output: output,
            cancellationToken: cancellationToken);
    }

    /// <summary>The video is already in the download archive (PLAN.md §5.4).</summary>
    public Task<QueueResult> SkipAsync(long itemId, JobOutput? output = null, CancellationToken cancellationToken = default) =>
        TransitionAsync(itemId, ItemTrigger.Skip, ItemEventReasons.InArchive, output: output, cancellationToken: cancellationToken);

    public Task<QueueResult> FailAsync(long itemId, JobFailure failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Trigger is not (ItemTrigger.WaitForRetry or ItemTrigger.WaitForAuth or ItemTrigger.WaitForToolFix or ItemTrigger.Fail))
        {
            throw new ArgumentException($"{failure.Trigger} is not a failure trigger.", nameof(failure));
        }

        return TransitionAsync(
            itemId,
            failure.Trigger,
            failure.Error.Code,
            item =>
            {
                item.ErrorCategory = failure.Category;
                item.ErrorCode = failure.Error.Code;
                item.ErrorArgsJson = ErrorArgs.Write(failure.Error.Args);
                item.ErrorMessage = failure.Error.Message;
                item.NextAttemptAt = failure.Trigger == ItemTrigger.WaitForRetry ? failure.NextAttemptAt : null;
                if (failure.Trigger == ItemTrigger.WaitForRetry)
                {
                    item.Attempt++;
                }
            },
            errorCategory: failure.Category,
            output: failure.Output,
            cancellationToken: cancellationToken);
    }

    /// <summary>The backoff of a <see cref="ItemState.WaitingRetry"/> item is over; it is queued again.</summary>
    public Task<QueueResult> RetryDueAsync(long itemId, CancellationToken cancellationToken = default) =>
        TransitionAsync(itemId, ItemTrigger.RetryDue, ItemEventReasons.RetryDue, item => item.NextAttemptAt = null, cancellationToken: cancellationToken);

    /// <summary>
    /// Requeues every item waiting on auth (<see cref="ItemTrigger.AuthRestored"/>) or on a yt-dlp update
    /// (<see cref="ItemTrigger.ToolFixed"/>). Returns how many items were requeued.
    /// </summary>
    public Task<int> ReleaseWaitingAsync(ItemTrigger trigger, CancellationToken cancellationToken = default)
    {
        var reason = trigger switch
        {
            ItemTrigger.AuthRestored => ItemEventReasons.AuthRestored,
            ItemTrigger.ToolFixed => ItemEventReasons.ToolFixed,
            _ => throw new ArgumentException($"{trigger} does not release waiting items.", nameof(trigger)),
        };

        return Send(
            async () =>
            {
                var changes = new Changes();
                foreach (var item in _active.ToList())
                {
                    Fire(item, trigger, reason, changes);
                }

                await CommitAsync(changes);
                return changes.Updated.Count;
            },
            cancellationToken);
    }

    /// <summary>
    /// Records progress of a running item and publishes it. Progress lives in memory; the size estimate is written
    /// to the database at most every <see cref="ProgressCheckpointInterval"/> per item.
    /// </summary>
    public Task<QueueResult> ReportProgressAsync(long itemId, ItemProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        return Send(
            async () =>
            {
                var item = FindActive(itemId);
                if (item is null)
                {
                    return QueueResult.NotFound(itemId);
                }

                if (!ItemStateMachine.IsRunning(item.State))
                {
                    return QueueResult.NotRunning(itemId, item.State);
                }

                var now = time.GetUtcNow();
                var lastCheckpoint = _progress.TryGetValue(itemId, out var previous) ? previous.LastCheckpoint : DateTimeOffset.MinValue;
                if (progress.TotalBytes is { } total && total != item.SizeBytes && now - lastCheckpoint >= ProgressCheckpointInterval)
                {
                    item.SizeBytes = total;
                    await CommitAsync(new Changes());
                    lastCheckpoint = now;
                }

                _progress[itemId] = new ProgressState(progress, lastCheckpoint);
                bus.Publish(new ItemProgressEvent(itemId, progress));
                return QueueResult.Ok;
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        if (_loop is not null)
        {
            await _loop;
        }

        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    // ---- Internals ----------------------------------------------------------------------------------------------

    private Task<QueueResult> TransitionAsync(
        long itemId,
        ItemTrigger trigger,
        string reason,
        Action<ItemEntity>? mutate = null,
        string? errorCategory = null,
        JobOutput? output = null,
        bool rejectWhenPaused = false,
        CancellationToken cancellationToken = default) =>
        Send(
            async () =>
            {
                var item = FindActive(itemId);
                if (item is null)
                {
                    return QueueResult.NotFound(itemId);
                }

                if (rejectWhenPaused && _paused)
                {
                    return QueueResult.QueuePaused();
                }

                var changes = new Changes();
                if (!Fire(item, trigger, reason, changes, errorCategory, output))
                {
                    return QueueResult.InvalidTransition(itemId, item.State, trigger);
                }

                mutate?.Invoke(item);
                await CommitAsync(changes);
                return QueueResult.Ok;
            },
            cancellationToken);

    /// <summary>Applies a transition in memory and records its <c>item_events</c> row; <see cref="CommitAsync"/> saves it.</summary>
    private static bool Fire(
        ItemEntity item,
        ItemTrigger trigger,
        string reason,
        Changes changes,
        string? errorCategory = null,
        JobOutput? output = null)
    {
        if (!ItemStateMachine.TryFire(item.State, trigger, out var to))
        {
            return false;
        }

        var itemEvent = new ItemEventEntity
        {
            FromState = item.State,
            ToState = to,
            Reason = reason,
            ErrorCategory = errorCategory,
            ExitCode = output?.ExitCode,
        };
        if (output is not null)
        {
            itemEvent.SetOutputTail(output.Lines);
        }

        item.Events.Add(itemEvent);
        changes.Updated.Add(new Update(item, item.State, reason));
        item.State = to;
        return true;
    }

    /// <summary>
    /// Takes items that left the queue out of it, renumbers positions, saves everything in one transaction, and then
    /// publishes the events and deletes the folders and files the changes no longer need.
    /// </summary>
    private async Task CommitAsync(Changes changes)
    {
        _active.RemoveAll(i => IsInactive(i.State));
        var reordered = new List<ItemPosition>();
        for (var i = 0; i < _active.Count; i++)
        {
            var item = _active[i];
            if (item.Position != i + 1)
            {
                item.Position = i + 1;
                if (item != changes.Added)
                {
                    reordered.Add(new ItemPosition(item.Id, item.Position));
                }
            }
        }

        try
        {
            await Db.SaveChangesAsync();
        }
        catch
        {
            await ReloadAsync();
            throw;
        }

        // Events are written once and never read back by the actor, so stop tracking them.
        foreach (var entry in Db.ChangeTracker.Entries<ItemEventEntity>().ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var (item, _, _) in changes.Updated)
        {
            item.Events.Clear();
        }

        changes.Added?.Events.Clear();

        if (changes.Added is { } added)
        {
            bus.Publish(new ItemAddedEvent(QueueItem.From(added)));
        }

        foreach (var (item, from, reason) in changes.Updated)
        {
            if (!ItemStateMachine.IsRunning(item.State))
            {
                _progress.Remove(item.Id);
            }

            if (IsInactive(item.State))
            {
                Db.Entry(item).State = EntityState.Detached;
                TryDeleteDirectory(paths.WorkDirFor(item.Id));
            }

            if (item == changes.Added)
            {
                continue;
            }

            bus.Publish<EngineEvent>(item.State == ItemState.Removed
                ? new ItemRemovedEvent(item.Id)
                : new ItemUpdatedEvent(QueueItem.From(item), from, reason));
        }

        if (reordered.Count > 0)
        {
            bus.Publish(new QueueReorderedEvent(reordered));
        }

        foreach (var file in changes.FilesToDelete)
        {
            TryDeleteFile(file);
        }
    }

    /// <summary>After a failed save, the tracked entities no longer match the database: start over from it.</summary>
    private async Task ReloadAsync()
    {
        await Db.DisposeAsync();
        _db = await contextFactory.CreateDbContextAsync();
        _active.Clear();
        _active.AddRange(await _db.LoadActiveAsync());

        var running = _active.Where(i => ItemStateMachine.IsRunning(i.State)).Select(i => i.Id).ToHashSet();
        foreach (var id in _progress.Keys.Where(id => !running.Contains(id)).ToList())
        {
            _progress.Remove(id);
        }
    }

    private void DeleteOrphanWorkDirs()
    {
        if (!Directory.Exists(paths.WorkDir))
        {
            return;
        }

        var ids = _active.Select(i => i.Id).ToHashSet();
        foreach (var dir in Directory.EnumerateDirectories(paths.WorkDir))
        {
            var isActive = long.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && ids.Contains(id);
            if (!isActive)
            {
                TryDeleteDirectory(dir);
            }
        }
    }

    // Best effort: a running yt-dlp may still hold files open. Orphans are cleaned up at the next start.
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
            logger.LogWarning(ex, "Could not delete the work folder {Path}", path);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the downloaded file {Path}", path);
        }
    }

    private ItemEntity? FindActive(long itemId) => _active.Find(i => i.Id == itemId);

    private static bool IsInactive(ItemState state) =>
        state is ItemState.Completed or ItemState.Skipped or ItemState.Removed or ItemState.Seen;

    private static void ApplyMetadata(ItemEntity item, ItemMetadata? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        item.Extractor = metadata.Extractor ?? item.Extractor;
        item.VideoId = metadata.VideoId ?? item.VideoId;
        item.Title = metadata.Title ?? item.Title;
        item.Channel = metadata.Channel ?? item.Channel;
        item.ChannelId = metadata.ChannelId ?? item.ChannelId;
        item.UploadDate = metadata.UploadDate ?? item.UploadDate;
        item.DurationSeconds = metadata.DurationSeconds ?? item.DurationSeconds;
        item.Availability = metadata.Availability ?? item.Availability;
        item.LiveStatus = metadata.LiveStatus ?? item.LiveStatus;
        item.SizeBytes = metadata.SizeBytes ?? item.SizeBytes;
    }

    private static void ClearError(ItemEntity item)
    {
        item.ErrorCategory = null;
        item.ErrorCode = null;
        item.ErrorArgsJson = null;
        item.ErrorMessage = null;
        item.NextAttemptAt = null;
    }

    private async Task<T> Send<T>(Func<Task<T>> handler, CancellationToken cancellationToken)
    {
        var command = new Command<T>(handler);
        if (!_commands.Writer.TryWrite(command))
        {
            throw new ObjectDisposedException(nameof(QueueActor));
        }

        return await command.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task RunAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync())
        {
            await command.ExecuteAsync();
        }
    }

    private abstract class Command
    {
        public abstract Task ExecuteAsync();
    }

    private sealed class Command<T>(Func<Task<T>> handler) : Command
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task ExecuteAsync()
        {
            try
            {
                Completion.TrySetResult(await handler());
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }

    private sealed record Update(ItemEntity Item, ItemState From, string Reason);

    private sealed class Changes
    {
        public ItemEntity? Added { get; init; }

        public List<Update> Updated { get; } = [];

        public List<string> FilesToDelete { get; } = [];
    }

    private sealed record ProgressState(ItemProgress Latest, DateTimeOffset LastCheckpoint);
}
