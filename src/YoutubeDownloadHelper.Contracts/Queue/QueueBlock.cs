namespace YoutubeDownloadHelper.Contracts.Queue;

/// <summary>
/// Conditions that stop new downloads without failing items (PLAN.md §4.3). Each one is shown in the status bar.
/// </summary>
public enum QueueBlock
{
    PausedByUser,
    WaitingForNetwork,
    DiskFull,
    ToolsMissing,
}
