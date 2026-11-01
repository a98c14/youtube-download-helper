using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Contracts.Queue;

/// <summary>A queue or history item (PLAN.md §5.1, §8).</summary>
/// <param name="Id">The item's ID; never reused.</param>
/// <param name="Url">The URL it was added with.</param>
/// <param name="Extractor">yt-dlp's extractor key, lowercase (e.g. <c>youtube</c>), once known.</param>
/// <param name="VideoId">The video's ID, once known.</param>
/// <param name="Title">The title, once resolved.</param>
/// <param name="Channel">The channel name, once resolved.</param>
/// <param name="ChannelId">The channel ID, once resolved.</param>
/// <param name="UploadDate">The upload date, once resolved.</param>
/// <param name="DurationSeconds">The duration, once resolved.</param>
/// <param name="Availability">yt-dlp's <c>availability</c>: <c>public</c>, <c>subscriber_only</c>, <c>premium_only</c>, <c>needs_auth</c>, …</param>
/// <param name="LiveStatus">yt-dlp's <c>live_status</c>; shown as a badge only.</param>
/// <param name="PresetId">The preset (<c>PresetIds</c>).</param>
/// <param name="Folder">The item's own destination folder, or null for the download folder setting.</param>
/// <param name="Force">Download even when the video was downloaded before.</param>
/// <param name="State">The state.</param>
/// <param name="Position">The place in the queue, from 1; 0 for items that left it (history).</param>
/// <param name="Attempt">The retry count the backoff uses; a manual retry resets it.</param>
/// <param name="NextAttemptAt">When a waiting item is retried, or when a <see cref="ItemState.WaitingToolFix"/> item fails.</param>
/// <param name="ErrorCategory">The category of the last error, while the item is waiting or failed.</param>
/// <param name="Error">The last error, while the item is waiting or failed.</param>
/// <param name="FilePath">The downloaded file, once completed.</param>
/// <param name="SizeBytes">The size estimate while resolving or downloading, then the final size.</param>
/// <param name="WatcherId">The watcher that queued it, if any.</param>
/// <param name="CreatedAt">When it was added.</param>
/// <param name="UpdatedAt">When it last changed; for history items, when they got there.</param>
/// <param name="Progress">The live progress of a running item; null otherwise.</param>
public sealed record ItemInfo(
    long Id,
    string Url,
    string? Extractor,
    string? VideoId,
    string? Title,
    string? Channel,
    string? ChannelId,
    DateOnly? UploadDate,
    double? DurationSeconds,
    string? Availability,
    string? LiveStatus,
    string PresetId,
    string? Folder,
    bool Force,
    ItemState State,
    long Position,
    int Attempt,
    DateTimeOffset? NextAttemptAt,
    ErrorCategory? ErrorCategory,
    EngineMessage? Error,
    string? FilePath,
    long? SizeBytes,
    long? WatcherId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ItemProgressInfo? Progress);

/// <summary>Live progress of a running item (<c>item.progress</c>). Every number is optional: yt-dlp omits what it doesn't know.</summary>
/// <param name="Phase">One of <see cref="ProgressPhases"/>.</param>
/// <param name="DownloadedBytes">Bytes downloaded so far over all formats.</param>
/// <param name="TotalBytes">The expected total over all formats: exact or an estimate.</param>
/// <param name="SpeedBytesPerSecond">The current download speed.</param>
/// <param name="EtaSeconds">Time left for the download (not post-processing).</param>
/// <param name="Fraction">Download progress from 0 to 1; null when unknown (e.g. a live stream).</param>
public sealed record ItemProgressInfo(
    string? Phase,
    long? DownloadedBytes,
    long? TotalBytes,
    double? SpeedBytesPerSecond,
    double? EtaSeconds,
    double? Fraction);

/// <summary>Values of <see cref="ItemProgressInfo.Phase"/>.</summary>
public static class ProgressPhases
{
    /// <summary>Started; nothing downloaded yet (extraction, format selection).</summary>
    public const string Preparing = "preparing";

    public const string Downloading = "downloading";

    /// <summary>Post-processing (merge, conversion, embedding); no percentage.</summary>
    public const string Processing = "processing";

    /// <summary>The file is going from the work folder to its destination.</summary>
    public const string Moving = "moving";

    public const string Done = "done";
}

/// <summary>The answer to <c>GET</c> <see cref="ApiRoutes.Items"/>: the queue in order.</summary>
/// <param name="Items">This page of the queue, in queue order.</param>
/// <param name="Sequence">
/// The event sequence number the snapshot is consistent with. Connect to <see cref="ApiRoutes.Events"/> with
/// <c>Last-Event-ID: &lt;sequence&gt;</c> to get every later change.
/// </param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; null on the last page.</param>
public sealed record ItemList(IReadOnlyList<ItemInfo> Items, long Sequence, string? NextCursor);

/// <summary>The answer to <c>GET</c> <see cref="ApiRoutes.History"/>: completed and skipped items, newest first.</summary>
/// <param name="Items">This page of history.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; null on the last page.</param>
public sealed record HistoryList(IReadOnlyList<ItemInfo> Items, string? NextCursor);

/// <summary>The answer to <c>GET</c> <see cref="ApiRoutes.Item"/>.</summary>
/// <param name="Item">The item.</param>
/// <param name="Events">Every state transition, oldest first.</param>
/// <param name="Attempts">Every yt-dlp run, oldest first.</param>
/// <param name="OutputTail">The output of the latest run that recorded any (up to 200 lines, raw yt-dlp text).</param>
public sealed record ItemDetail(
    ItemInfo Item,
    IReadOnlyList<ItemEventInfo> Events,
    IReadOnlyList<AttemptInfo> Attempts,
    IReadOnlyList<string> OutputTail);

/// <summary>One state transition of an item.</summary>
/// <param name="At">When it happened.</param>
/// <param name="FromState">The state before; null for the event that created the item.</param>
/// <param name="ToState">The state after.</param>
/// <param name="Reason">Why: an error code (<c>error.*</c>) or a queue reason (<c>user.pause</c>, <c>job.completed</c>, …).</param>
/// <param name="ErrorCategory">The error's category, when an error caused it.</param>
/// <param name="ExitCode">yt-dlp's exit code, on the event that ended a run.</param>
public sealed record ItemEventInfo(
    DateTimeOffset At,
    ItemState? FromState,
    ItemState ToState,
    string? Reason,
    ErrorCategory? ErrorCategory,
    int? ExitCode);

/// <summary>One yt-dlp run of an item: from entering <see cref="ItemState.Resolving"/> until it left the running states.</summary>
/// <param name="Number">The run's number, from 1.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndedAt">When it ended; null while it runs.</param>
/// <param name="EndState">The state it ended in.</param>
/// <param name="EndReason">Why it ended (see <see cref="ItemEventInfo.Reason"/>).</param>
/// <param name="ExitCode">yt-dlp's exit code, when recorded.</param>
/// <param name="OutputTail">The last output lines, when recorded.</param>
public sealed record AttemptInfo(
    int Number,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    ItemState? EndState,
    string? EndReason,
    int? ExitCode,
    IReadOnlyList<string> OutputTail);
