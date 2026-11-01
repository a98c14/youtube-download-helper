namespace YoutubeDownloadHelper.Contracts.Resolve;

/// <summary>The body of <c>POST</c> <see cref="ApiRoutes.Resolve"/>.</summary>
/// <param name="Url">A video, playlist or channel URL (http or https).</param>
/// <param name="Limit">
/// The most playlist entries to list (default <see cref="ResolveLimits.DefaultEntries"/>, at most
/// <see cref="ResolveLimits.MaxEntries"/>); <see cref="ResolveResponse.Truncated"/> says whether there were more.
/// </param>
public sealed record ResolveRequest(string? Url, int? Limit = null);

/// <summary>Limits of <c>POST</c> <see cref="ApiRoutes.Resolve"/> and of playlist expansion in <c>POST</c> <see cref="ApiRoutes.Items"/>.</summary>
public static class ResolveLimits
{
    /// <summary>The default <see cref="ResolveRequest.Limit"/>.</summary>
    public const int DefaultEntries = 1000;

    /// <summary>The largest <see cref="ResolveRequest.Limit"/>, and the most items one <c>POST /items</c> request adds.</summary>
    public const int MaxEntries = 5000;
}

/// <summary>What a URL is.</summary>
public enum ResolveKind
{
    /// <summary>A single video: <see cref="ResolveResponse.Entries"/> has exactly one entry, with full metadata.</summary>
    Video,

    /// <summary>A playlist or channel (tab): <see cref="ResolveResponse.Entries"/> lists it, flat.</summary>
    Playlist,
}

/// <summary>Whether a resolved entry is a video or a playlist of its own.</summary>
public enum ResolvedEntryKind
{
    /// <summary>A video; queue it by <see cref="ResolvedEntry.VideoId"/> (YouTube) or <see cref="ResolvedEntry.Url"/>.</summary>
    Video,

    /// <summary>
    /// A playlist inside the playlist: a tab of a channel URL without a tab (Videos, Shorts, Live), or a playlist of a
    /// channel's playlists tab. Resolve its <see cref="ResolvedEntry.Url"/> to list its videos.
    /// </summary>
    Playlist,
}

/// <summary>Why an entry would be a duplicate in <c>POST /items</c> (PLAN.md §6.2).</summary>
public enum EntryDuplicate
{
    /// <summary>The video (same URL or YouTube video ID) is in the queue. <see cref="ResolvedEntry.ItemId"/> is that item.</summary>
    Queued,

    /// <summary>The video is in the download archive (a completed item). <see cref="ResolvedEntry.ItemId"/> is that item.</summary>
    Downloaded,
}

/// <summary>The answer to <c>POST</c> <see cref="ApiRoutes.Resolve"/>: the metadata, no download.</summary>
/// <param name="Kind">A video or a playlist.</param>
/// <param name="Url">The URL as resolved (trimmed).</param>
/// <param name="Id">The video, playlist or channel id.</param>
/// <param name="Title">The video or playlist title.</param>
/// <param name="Channel">The channel name.</param>
/// <param name="ChannelId">The channel id.</param>
/// <param name="TotalCount">The playlist's full length, when yt-dlp knows it (not for channel tabs); 1 for a video.</param>
/// <param name="Truncated">The playlist has more entries than <see cref="ResolveRequest.Limit"/>; only the first ones are listed.</param>
/// <param name="Entries">The video, or the playlist's entries in order (newest first for channel tabs).</param>
public sealed record ResolveResponse(
    ResolveKind Kind,
    string Url,
    string? Id,
    string? Title,
    string? Channel,
    string? ChannelId,
    int? TotalCount,
    bool Truncated,
    IReadOnlyList<ResolvedEntry> Entries);

/// <summary>
/// A video (or nested playlist) of a <see cref="ResolveResponse"/>. A playlist entry has only what a flat listing gives:
/// usually the title, duration and, on some tabs, availability or live status.
/// </summary>
/// <param name="Kind">A video or a nested playlist.</param>
/// <param name="Url">The URL to queue (a video) or to resolve (a playlist).</param>
/// <param name="Extractor">yt-dlp's extractor key, lowercase (e.g. <c>youtube</c>).</param>
/// <param name="VideoId">The YouTube video ID, for YouTube videos; pass it in <c>AddItemsRequest.VideoIds</c>.</param>
/// <param name="Title">The title.</param>
/// <param name="Channel">The channel name.</param>
/// <param name="UploadDate">The upload date, when known.</param>
/// <param name="DurationSeconds">The duration, when known.</param>
/// <param name="Availability">
/// yt-dlp's <c>availability</c> (<c>public</c>, <c>subscriber_only</c>, <c>premium_only</c>, <c>needs_auth</c>, …), when
/// known: the Members/Premium badge.
/// </param>
/// <param name="LiveStatus">yt-dlp's <c>live_status</c> (<c>not_live</c>, <c>is_live</c>, <c>was_live</c>, <c>is_upcoming</c>, …), when known.</param>
/// <param name="SizeBytes">The size estimate of the default formats; single videos only.</param>
/// <param name="Duplicate">Why adding it would be a duplicate; null when it would be added.</param>
/// <param name="ItemId">The queued or downloaded item it duplicates.</param>
public sealed record ResolvedEntry(
    ResolvedEntryKind Kind,
    string Url,
    string? Extractor,
    string? VideoId,
    string? Title,
    string? Channel,
    DateOnly? UploadDate,
    double? DurationSeconds,
    string? Availability,
    string? LiveStatus,
    long? SizeBytes,
    EntryDuplicate? Duplicate,
    long? ItemId);

/// <summary>
/// Codes of the error bodies of <c>POST</c> <see cref="ApiRoutes.Resolve"/> and of failed playlist expansions in
/// <c>POST</c> <see cref="ApiRoutes.Items"/>. A yt-dlp failure answers with its <c>error.*</c> code instead
/// (<see cref="Errors.ErrorCodes"/>).
/// </summary>
public static class ResolveCodes
{
    /// <summary>503: yt-dlp is not installed or not working yet (the <c>toolsMissing</c> queue block). Args: <c>input</c>.</summary>
    public const string ToolsMissing = "resolve.toolsMissing";

    /// <summary>422: yt-dlp ran but found neither a video nor a playlist. Args: <c>input</c>.</summary>
    public const string Unsupported = "resolve.unsupported";
}
