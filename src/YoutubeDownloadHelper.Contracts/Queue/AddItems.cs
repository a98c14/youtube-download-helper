using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Contracts.Queue;

/// <summary>The body of <c>POST</c> <see cref="ApiRoutes.Items"/>. Give <see cref="Urls"/>, <see cref="VideoIds"/>, or both.</summary>
/// <param name="Urls">
/// Video URLs (http or https). Each URL is one item, unless <paramref name="ExpandPlaylists"/> is
/// <see cref="PlaylistExpansion.All"/>.
/// </param>
/// <param name="VideoIds">YouTube video IDs (11 characters), added as <c>https://www.youtube.com/watch?v=&lt;id&gt;</c>.</param>
/// <param name="PresetId">The preset (<c>PresetIds</c>); by default the <c>defaultPresetId</c> setting.</param>
/// <param name="Folder">A destination folder for these items; by default the download folder setting.</param>
/// <param name="Paused">Add the items <see cref="ItemState.Paused"/>.</param>
/// <param name="Force">Add (and download) videos that are already queued or downloaded.</param>
/// <param name="ExpandPlaylists">
/// <see cref="PlaylistExpansion.All"/>: every URL that is not a YouTube video URL is resolved first, and a playlist or
/// channel (tab) is replaced by all its videos. To add a selection instead, resolve the URL (<c>POST /resolve</c>) and
/// send the chosen <see cref="VideoIds"/>.
/// </param>
public sealed record AddItemsRequest(
    IReadOnlyList<string>? Urls = null,
    IReadOnlyList<string>? VideoIds = null,
    string? PresetId = null,
    string? Folder = null,
    bool Paused = false,
    bool Force = false,
    PlaylistExpansion ExpandPlaylists = PlaylistExpansion.None);

/// <summary>What <c>POST /items</c> does with playlist and channel URLs.</summary>
public enum PlaylistExpansion
{
    /// <summary>Every URL is one item, as given.</summary>
    None,

    /// <summary>
    /// Playlists and channel tabs are replaced by all their videos (at most <c>ResolveLimits.MaxEntries</c> per request);
    /// a channel URL without a tab by the videos of each of its tabs.
    /// </summary>
    All,
}

/// <summary>What happened to one URL or video ID of an <see cref="AddItemsRequest"/>.</summary>
public enum AddItemOutcome
{
    /// <summary>A new item was queued.</summary>
    Added,

    /// <summary>
    /// Not added: the video is already in the queue or in the download archive (or listed twice in the request).
    /// <see cref="AddItemResult.ItemId"/> is the existing item, when known.
    /// </summary>
    Duplicate,

    /// <summary>
    /// Not added: not an http(s) URL or not a video ID, or a playlist that is empty or too large.
    /// <see cref="AddItemResult.Message"/> says why.
    /// </summary>
    Invalid,

    /// <summary>
    /// Not added: expanding the playlist failed (a yt-dlp error, or the tools are missing). <see cref="AddItemResult.Message"/>
    /// has the <c>error.*</c> or <c>resolve.*</c> code; trying again later may work.
    /// </summary>
    Failed,
}

/// <summary>
/// The result for one input, in request order (URLs first, then video IDs). An expanded playlist has a result per video
/// instead, in playlist order, each with the playlist URL in <paramref name="Playlist"/>.
/// </summary>
/// <param name="Input">The URL or video ID as given, or the video URL of an expanded playlist entry.</param>
/// <param name="Outcome">Added, duplicate or invalid.</param>
/// <param name="ItemId">The new item (<see cref="AddItemOutcome.Added"/>) or the existing one (<see cref="AddItemOutcome.Duplicate"/>).</param>
/// <param name="Message">Why it was not added (<see cref="ItemCodes"/>, or for <see cref="AddItemOutcome.Failed"/> an <c>error.*</c> or <c>resolve.*</c> code); null when added.</param>
/// <param name="Playlist">The playlist URL (as given) an expanded entry came from; null otherwise.</param>
public sealed record AddItemResult(string Input, AddItemOutcome Outcome, long? ItemId, EngineMessage? Message, string? Playlist = null);

/// <summary>The answer to <c>POST</c> <see cref="ApiRoutes.Items"/>.</summary>
/// <param name="Results">One result per input.</param>
public sealed record AddItemsResponse(IReadOnlyList<AddItemResult> Results);

/// <summary>
/// Message codes about items: <see cref="AddItemResult.Message"/> and the error bodies of the item commands. Every one has
/// the arg <c>input</c> or <c>itemId</c> as listed.
/// </summary>
public static class ItemCodes
{
    /// <summary>Not an absolute http(s) URL. Args: <c>input</c>.</summary>
    public const string InvalidUrl = "item.invalidUrl";

    /// <summary>Not an 11-character YouTube video ID. Args: <c>input</c>.</summary>
    public const string InvalidVideoId = "item.invalidVideoId";

    /// <summary>Already queued. Args: <c>input</c>, <c>itemId</c>.</summary>
    public const string AlreadyQueued = "item.alreadyQueued";

    /// <summary>Already downloaded (in the archive). Args: <c>input</c>, <c>itemId</c>.</summary>
    public const string AlreadyDownloaded = "item.alreadyDownloaded";

    /// <summary>Listed more than once in the same request. Args: <c>input</c>.</summary>
    public const string DuplicateInRequest = "item.duplicateInRequest";

    /// <summary>An expanded playlist has no videos. Args: <c>input</c>.</summary>
    public const string PlaylistEmpty = "item.playlistEmpty";

    /// <summary>
    /// Expanding the playlist would add more than <c>limit</c> items in one request; nothing of it was added. Resolve it
    /// and add a selection instead. Args: <c>input</c>, <c>limit</c>.
    /// </summary>
    public const string PlaylistTooLarge = "item.playlistTooLarge";
}
