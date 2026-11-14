namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>A managed tool's installed versions (table <c>tools</c>, PLAN.md §5.6), kept by the <c>ToolManager</c>.</summary>
public sealed class ToolEntity : Entity
{
    /// <summary>e.g. <c>yt-dlp</c>, <c>deno</c>, <c>ffmpeg</c>.</summary>
    public required string Tool { get; set; }

    public string? Current { get; set; }

    public string? Previous { get; set; }

    /// <summary>Release channel of <see cref="Current"/> (<c>stable</c> or <c>nightly</c> for yt-dlp).</summary>
    public string? Channel { get; set; }

    /// <summary>Release channel of <see cref="Previous"/>, so a rollback restores both.</summary>
    public string? PreviousChannel { get; set; }

    /// <summary>
    /// A version that was rolled back from (it failed its smoke test, or the user rolled it back). Update checks do not
    /// install it again; a newer release replaces it.
    /// </summary>
    public string? SkippedVersion { get; set; }

    /// <summary>When the feed was last checked for this tool (an update check or an install).</summary>
    public DateTimeOffset? LastCheck { get; set; }
}
