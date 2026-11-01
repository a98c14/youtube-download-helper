using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Core.Queue;

/// <summary>A new item. <paramref name="Metadata"/> is what <c>/resolve</c> already knows, if anything.</summary>
public sealed record AddItemRequest(
    string Url,
    string PresetId,
    string? Folder = null,
    bool Force = false,
    bool Paused = false,
    long? WatcherId = null,
    ItemMetadata? Metadata = null);

/// <summary>What resolving an item found (PLAN.md §5.1). Null fields leave the item's value unchanged.</summary>
public sealed record ItemMetadata(
    string? Extractor = null,
    string? VideoId = null,
    string? Title = null,
    string? Channel = null,
    string? ChannelId = null,
    DateOnly? UploadDate = null,
    double? DurationSeconds = null,
    string? Availability = null,
    string? LiveStatus = null,
    long? SizeBytes = null);

/// <summary>Download progress; kept in memory only, except the size estimate (checkpointed, at most every 10 s).</summary>
public sealed record ItemProgress(
    string? Phase = null,
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    double? SpeedBytesPerSecond = null,
    double? EtaSeconds = null,
    double? Fraction = null);

/// <summary>How a yt-dlp run ended; stored on the event that ends it.</summary>
public sealed record JobOutput(int ExitCode, IReadOnlyList<string> Lines);

/// <summary>
/// A failed job, usually from <see cref="Errors.RetryDecision.ToJobFailure"/>. <paramref name="Trigger"/> is one of
/// <see cref="ItemTrigger.WaitForRetry"/>, <see cref="ItemTrigger.WaitForAuth"/>, <see cref="ItemTrigger.WaitForToolFix"/> or
/// <see cref="ItemTrigger.Fail"/>. <see cref="ItemTrigger.WaitForRetry"/> adds one to the item's retry count.
/// <paramref name="NextAttemptAt"/> is the retry time for <see cref="ItemTrigger.WaitForRetry"/> and the deadline for
/// <see cref="ItemTrigger.WaitForToolFix"/>; it is ignored otherwise.
/// </summary>
public sealed record JobFailure(
    ItemTrigger Trigger,
    ErrorCategory Category,
    EngineMessage Error,
    DateTimeOffset? NextAttemptAt = null,
    JobOutput? Output = null);

public enum MoveTarget
{
    Top,
    Bottom,

    /// <summary>Just before another item.</summary>
    Before,
}
