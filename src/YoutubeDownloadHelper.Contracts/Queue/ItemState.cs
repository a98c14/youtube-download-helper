namespace YoutubeDownloadHelper.Contracts.Queue;

/// <summary>
/// Queue item states (PLAN.md §5.1). Persisted by name, so members can be reordered or added without a data
/// migration; renaming one is a breaking change for stored items.
/// </summary>
public enum ItemState
{
    Added,
    Queued,

    /// <summary>
    /// The user paused the item: it keeps its place and partial files but is not started until resumed. Pausing a
    /// running item kills its yt-dlp process; resuming puts it back in <see cref="Queued"/>.
    /// </summary>
    Paused,

    Resolving,
    Downloading,
    PostProcessing,
    Completed,
    WaitingRetry,
    WaitingAuth,
    WaitingToolFix,
    Failed,
    Skipped,

    /// <summary>
    /// A watcher saw the video but did not queue it (e.g. it was in the channel when the watcher was created). It is
    /// only a marker, so the video is not queued later; it is never downloaded and is in neither the queue nor history.
    /// </summary>
    Seen,

    /// <summary>The user removed the item: the engine no longer tracks it, and its file was deleted or left untracked.</summary>
    Removed,

    /// <summary>Reserved for v2 (live streams); never entered in v1.</summary>
    WaitingForStream,

    /// <summary>Reserved for v2 (live streams); never entered in v1.</summary>
    Recording,
}
