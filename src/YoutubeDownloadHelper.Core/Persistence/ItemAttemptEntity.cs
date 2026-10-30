namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// One yt-dlp run of an item (table <c>item_attempts</c>) with the tail of its output for the details pane
/// (PLAN.md §5.4). Full logs go to <c>logs\jobs\</c>.
/// </summary>
public sealed class ItemAttemptEntity
{
    /// <summary>The most output lines kept per attempt.</summary>
    public const int MaxOutputLines = 200;

    public Guid ItemId { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>
    /// Up to <see cref="MaxOutputLines"/> lines joined by <c>\n</c>, without <c>dl</c> progress lines. Set through
    /// <see cref="SetOutputTail"/>.
    /// </summary>
    public string OutputTail { get; set; } = "";

    /// <summary>Keeps the last <see cref="MaxOutputLines"/> of <paramref name="lines"/>.</summary>
    public void SetOutputTail(IEnumerable<string> lines) =>
        OutputTail = string.Join('\n', lines.TakeLast(MaxOutputLines));
}
