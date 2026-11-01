using System.Collections.Frozen;
using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Queue;

/// <summary>What asks an item to change state (PLAN.md §5.1).</summary>
public enum ItemTrigger
{
    /// <summary>A new item joins the queue.</summary>
    Enqueue,

    /// <summary>The user pauses the item; a running yt-dlp process is killed.</summary>
    Pause,

    /// <summary>The user resumes a paused item.</summary>
    Resume,

    /// <summary>The scheduler starts a job (metadata first).</summary>
    StartResolving,

    /// <summary>Metadata is in; the download starts.</summary>
    StartDownloading,

    /// <summary>yt-dlp moved on to merging, embedding or converting.</summary>
    StartPostProcessing,

    Complete,

    /// <summary>The video is already in the download archive and the item is not forced.</summary>
    Skip,

    /// <summary>A transient failure with retries left (backoff, PLAN.md §5.2).</summary>
    WaitForRetry,

    /// <summary>An auth failure: waits for a login or cookie refresh.</summary>
    WaitForAuth,

    /// <summary>The extractor looks broken: waits for a yt-dlp update.</summary>
    WaitForToolFix,

    /// <summary>A permanent failure, retries exhausted, or a wait that timed out.</summary>
    Fail,

    /// <summary>The backoff timer of a <see cref="ItemState.WaitingRetry"/> item fired.</summary>
    RetryDue,

    /// <summary>Valid cookies are back.</summary>
    AuthRestored,

    /// <summary>yt-dlp was updated.</summary>
    ToolFixed,

    /// <summary>The user retries a failed or waiting item; its retry count starts over.</summary>
    Retry,

    /// <summary>A running job stopped without a result: the engine restarted, or the whole queue was paused.</summary>
    Interrupt,

    /// <summary>The user removes the item.</summary>
    Remove,
}

/// <summary>
/// The allowed item transitions, as a pure function. <see cref="QueueActor"/> applies them; nothing else changes an
/// item's state.
/// </summary>
public static class ItemStateMachine
{
    private static readonly ItemState[] Running = [ItemState.Resolving, ItemState.Downloading, ItemState.PostProcessing];

    private static readonly ItemState[] Waiting = [ItemState.WaitingRetry, ItemState.WaitingAuth, ItemState.WaitingToolFix];

    private static readonly FrozenDictionary<(ItemState From, ItemTrigger Trigger), ItemState> Table = BuildTable();

    /// <summary>Every allowed transition, for tests and diagnostics.</summary>
    public static IEnumerable<(ItemState From, ItemTrigger Trigger, ItemState To)> Transitions =>
        Table.Select(t => (t.Key.From, t.Key.Trigger, t.Value));

    /// <summary>The state <paramref name="trigger"/> moves an item in <paramref name="from"/> to, or false if it is not allowed.</summary>
    public static bool TryFire(ItemState from, ItemTrigger trigger, out ItemState to) =>
        Table.TryGetValue((from, trigger), out to);

    /// <summary>States with a yt-dlp job in progress.</summary>
    public static bool IsRunning(ItemState state) => Running.Contains(state);

    private static FrozenDictionary<(ItemState, ItemTrigger), ItemState> BuildTable()
    {
        var table = new Dictionary<(ItemState, ItemTrigger), ItemState>();

        void Add(ItemTrigger trigger, ItemState to, params ReadOnlySpan<ItemState> from)
        {
            foreach (var state in from)
            {
                table.Add((state, trigger), to);
            }
        }

        Add(ItemTrigger.Enqueue, ItemState.Queued, ItemState.Added);
        Add(ItemTrigger.Pause, ItemState.Paused, [ItemState.Added, ItemState.Queued, .. Running, .. Waiting]);
        Add(ItemTrigger.Resume, ItemState.Queued, ItemState.Paused);

        Add(ItemTrigger.StartResolving, ItemState.Resolving, ItemState.Queued);
        Add(ItemTrigger.StartDownloading, ItemState.Downloading, ItemState.Resolving);
        Add(ItemTrigger.StartPostProcessing, ItemState.PostProcessing, ItemState.Downloading);
        Add(ItemTrigger.Complete, ItemState.Completed, ItemState.Downloading, ItemState.PostProcessing);
        Add(ItemTrigger.Skip, ItemState.Skipped, ItemState.Resolving);

        Add(ItemTrigger.WaitForRetry, ItemState.WaitingRetry, Running);
        Add(ItemTrigger.WaitForAuth, ItemState.WaitingAuth, Running);
        Add(ItemTrigger.WaitForToolFix, ItemState.WaitingToolFix, Running);
        Add(ItemTrigger.Fail, ItemState.Failed, [.. Running, .. Waiting]);

        Add(ItemTrigger.RetryDue, ItemState.Queued, ItemState.WaitingRetry);
        Add(ItemTrigger.AuthRestored, ItemState.Queued, ItemState.WaitingAuth);
        Add(ItemTrigger.ToolFixed, ItemState.Queued, ItemState.WaitingToolFix);
        Add(ItemTrigger.Retry, ItemState.Queued, [ItemState.Failed, .. Waiting]);
        Add(ItemTrigger.Interrupt, ItemState.Queued, Running);

        // Reserved v2 states are never entered, so there is nothing to remove from them.
        foreach (var state in Enum.GetValues<ItemState>())
        {
            if (state is not (ItemState.Removed or ItemState.WaitingForStream or ItemState.Recording))
            {
                Add(ItemTrigger.Remove, ItemState.Removed, state);
            }
        }

        return table.ToFrozenDictionary();
    }
}
