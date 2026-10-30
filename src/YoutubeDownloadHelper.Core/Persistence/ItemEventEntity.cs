using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// One state transition of an item (table <c>item_events</c>, PLAN.md §5.1). The event that ends a yt-dlp run also
/// carries its exit code and output tail (§5.4); full logs go to <c>logs\jobs\</c>. A run starts with the event
/// into <see cref="ItemState.Resolving"/>, so counting those numbers the runs.
/// </summary>
public sealed class ItemEventEntity
{
    /// <summary>The most output lines kept per run.</summary>
    public const int MaxOutputLines = 200;

    public long Id { get; set; }

    public Guid ItemId { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Null for the event that created the item.</summary>
    public ItemState? FromState { get; set; }

    public ItemState ToState { get; set; }

    public string? Reason { get; set; }

    public string? ErrorCategory { get; set; }

    /// <summary>Set only on the event that ends a yt-dlp run.</summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// Up to <see cref="MaxOutputLines"/> lines joined by <c>\n</c>, without <c>dl</c> progress lines. Set only on the
    /// event that ends a yt-dlp run, through <see cref="SetOutputTail"/>.
    /// </summary>
    public string? OutputTail { get; set; }

    /// <summary>Keeps the last <see cref="MaxOutputLines"/> of <paramref name="lines"/>.</summary>
    public void SetOutputTail(IEnumerable<string> lines) =>
        OutputTail = string.Join('\n', lines.TakeLast(MaxOutputLines));
}
