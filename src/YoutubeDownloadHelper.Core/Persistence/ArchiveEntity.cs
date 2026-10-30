namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// A downloaded video (table <c>archive</c>, PLAN.md §5.4). Keyed like yt-dlp's archive: extractor + video id,
/// compared ordinally.
/// </summary>
public sealed class ArchiveEntity
{
    public required string Extractor { get; set; }

    public required string VideoId { get; set; }

    /// <summary>The item that downloaded it; null once that item is purged.</summary>
    public Guid? ItemId { get; set; }

    public string? FilePath { get; set; }

    public DateTimeOffset DownloadedAt { get; set; }
}
