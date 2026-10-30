using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>A queue item, active or in history (table <c>items</c>, PLAN.md §8). Mutated only by the queue actor.</summary>
public sealed class ItemEntity
{
    /// <summary>Also names the item's work folder (<see cref="DataPaths.WorkDirFor"/>).</summary>
    public Guid Id { get; set; }

    public required string Url { get; set; }

    /// <summary>yt-dlp's extractor key, lowercase (e.g. <c>youtube</c>); with <see cref="VideoId"/> it identifies the video (<see cref="ItemQueries.GetDownloadedVideoIdsAsync"/>).</summary>
    public string? Extractor { get; set; }

    public string? VideoId { get; set; }

    public string? Title { get; set; }

    public string? Channel { get; set; }

    public string? ChannelId { get; set; }

    public DateOnly? UploadDate { get; set; }

    public double? DurationSeconds { get; set; }

    /// <summary>yt-dlp's <c>availability</c> (<c>public</c>, <c>subscriber_only</c>, …).</summary>
    public string? Availability { get; set; }

    /// <summary>yt-dlp's <c>live_status</c>; shown as a badge only in v1.</summary>
    public string? LiveStatus { get; set; }

    public required string PresetId { get; set; }

    /// <summary>The destination folder, or null for the download folder setting.</summary>
    public string? Folder { get; set; }

    /// <summary>Download even when this video was downloaded before.</summary>
    public bool Force { get; set; }

    public ItemState State { get; set; }

    /// <summary>Queue order; unique and contiguous among active items (kept by the queue actor).</summary>
    public long Position { get; set; }

    /// <summary>The retry count the backoff uses (PLAN.md §5.2); a manual retry resets it.</summary>
    public int Attempt { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>The error category name (#39 owns the enum).</summary>
    public string? ErrorCategory { get; set; }

    /// <summary>Stable message code, e.g. <c>error.unavailable.private</c>.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>The message args as a JSON object.</summary>
    public string? ErrorArgsJson { get; set; }

    /// <summary>English fallback message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The downloaded file. Set when the item enters <see cref="ItemState.Completed"/> and never cleared, so it marks
    /// the video as downloaded after the item is removed or re-queued (<see cref="ItemQueries.GetDownloadedVideoIdsAsync"/>).
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>The size estimate while resolving or downloading, then the final size.</summary>
    public long? SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the item entered a history state (<see cref="ItemQueries.HistoryStates"/>); the history sort key.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// When the user removed the item. Removing hides it from the queue and history without changing its state; the
    /// actor stops a running item first. The row stays, so a downloaded video stays in the archive.
    /// </summary>
    public DateTimeOffset? RemovedAt { get; set; }

    public Guid? WatcherId { get; set; }

    /// <summary>Single account in v1; kept for a later multi-account migration (PLAN.md §2).</summary>
    public string AccountId { get; set; } = Accounts.Default;

    public WatcherEntity? Watcher { get; set; }

    public List<ItemEventEntity> Events { get; } = [];
}
