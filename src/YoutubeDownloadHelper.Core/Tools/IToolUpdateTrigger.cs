namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Asks the tool manager for a yt-dlp update check, because an item looks like it failed on a broken extractor
/// (PLAN.md §5.2). The scheduler calls it at most once per hour. After an update the tool manager requeues the waiting
/// items with <c>QueueActor.ReleaseWaitingAsync(ItemTrigger.ToolFixed)</c>. <see cref="NoToolUpdateTrigger"/> is the
/// default; the tool manager (#43) replaces it.
/// </summary>
public interface IToolUpdateTrigger
{
    /// <summary>Starts a check in the background. Must return quickly and never throw.</summary>
    void RequestYtDlpUpdateCheck();
}

/// <summary>Does nothing: there is no tool manager yet.</summary>
public sealed class NoToolUpdateTrigger : IToolUpdateTrigger
{
    public void RequestYtDlpUpdateCheck()
    {
    }
}
