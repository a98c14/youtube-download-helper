namespace YoutubeDownloadHelper.Contracts.Queue;

/// <summary>
/// Queue item states (PLAN.md §5.1). Persisted by name, so members can be reordered or added without a data
/// migration; renaming one is a breaking change for stored items.
/// </summary>
public enum ItemState
{
    Added,
    Queued,
    Resolving,
    Downloading,
    PostProcessing,
    Completed,
    WaitingRetry,
    WaitingAuth,
    WaitingToolFix,
    Failed,
    Skipped,

    /// <summary>Reserved for v2 (live streams); never entered in v1.</summary>
    WaitingForStream,

    /// <summary>Reserved for v2 (live streams); never entered in v1.</summary>
    Recording,
}
