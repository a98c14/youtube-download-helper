namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>A managed tool's installed versions (table <c>tools</c>, PLAN.md §5.6). Behaviour lands in #43.</summary>
public sealed class ToolEntity
{
    /// <summary>e.g. <c>yt-dlp</c>, <c>deno</c>, <c>ffmpeg</c>.</summary>
    public required string Tool { get; set; }

    public string? Current { get; set; }

    public string? Previous { get; set; }

    /// <summary>Release channel of <see cref="Current"/> (<c>stable</c> or <c>nightly</c> for yt-dlp).</summary>
    public string? Channel { get; set; }

    public DateTimeOffset? LastCheck { get; set; }
}
